using System;
using System.Diagnostics;
using FlyingWormConsole3.LiteNetLib;
using X2Coop.Mirror;
using X2Coop.Net;
using X2Coop.Runtime;

namespace X2Coop.Session
{
    /// <summary>
    /// Guest side of the session handshake (spec section 6): on transport
    /// Connected it sends Hello (SessionHello.Collect fills versions, commits,
    /// player name, pack stamps), then waits for HelloAck.
    ///
    ///   accept -> Runner already adopted the assigned session id (envelope);
    ///             logs "HelloAck accept". T3.3's SaveReceiver takes over next.
    ///   reject -> logs "join rejected: <host's human-readable Detail>" and
    ///             stays CONNECTED - the host does not kick, so a later retry
    ///             (x2coop.joinRetryMs cadence, or the T2.4 Join button calling
    ///             SendHello again) can succeed after e.g. the host reaches the
    ///             Strategy screen. Retry is OFF by default (joinRetryMs=0).
    ///   timeout -> AwaitingAck for longer than x2coop.ackTimeoutMs (default
    ///             10 s, T2.5) drops back to Idle so a stuck "joining" can
    ///             never wedge the UI - Hello is then re-drivable.
    ///   HostEnded -> the host closed the session: log, drop to Idle and
    ///             disconnect the peer gracefully (no 15 s timeout wait).
    ///   disconnect -> back to Idle; a fresh Connect restarts the handshake.
    ///
    /// T3.6 additions:
    ///   - <see cref="RequestResync"/>: the single guest -> host resync
    ///     request path (Resync button, HostReturned relay, periodic
    ///     x2coop.resyncMinutes timer, deferred mid-stream resync).
    ///   - HostLeftStrategy/HostReturned drive _hostBusy (pauses the timer)
    ///     and the on-return auto-resync.
    ///   - <see cref="NotifyMirrorLost"/>: the guest's ACTIVE mirror world
    ///     died mid-session -> SessionNotice{GuestMirrorLost} to the host.
    /// </summary>
    public static class SessionClient
    {
        private enum State : byte { Idle = 0, AwaitingAck = 1, InSession = 2, Rejected = 3 }

        private static Runner _runner;
        private static State _state = State.Idle;
        private static readonly Stopwatch _retryWatch = new Stopwatch();
        private static readonly Stopwatch _ackWatch = new Stopwatch(); // AwaitingAck timeout (T2.5)
        // T3.6 resync plumbing: _resyncWatch paces the periodic
        // x2coop.resyncMinutes timer (restarts on every mirror ACTIVATION so
        // spacing is between mirrors, not wall clock); _hostBusy pauses it
        // while the host is off-strategy; _resyncOnActivate defers a
        // HostReturned-triggered resync while a stream is already in flight;
        // _lastMode detects the ->Active transition.
        // T3.6-r1 (review I1) single-flight + debounce: _resyncOutstanding
        // latches from a ResyncReq send until the answering SaveOffer lands
        // (or HostReturned/disconnect/stale-timeout clears it) - a second
        // trigger MERGES into the in-flight stream instead of re-queuing the
        // host (each re-queue restarts SaveStreamer's _pendingTimer and
        // starves the offer) and resetting our receive buffer.
        // _resyncReqWatch doubles as the post-completion debounce
        // (x2coop.resyncReqMinMs) and the stale-outstanding timeout clock;
        // _resyncOnReturn holds a request made while the host was off-strategy
        // until HostReturned collapses it into the single auto-resync.
        private static readonly Stopwatch _resyncWatch = new Stopwatch();
        private static bool _hostBusy;
        private static bool _resyncOnActivate;
        private static bool _resyncOnReturn;
        private static bool _resyncOutstanding;
        private static readonly Stopwatch _resyncReqWatch = new Stopwatch();
        private static MirrorMode _lastMode = MirrorMode.Off;
        private const int DefaultResyncReqMinMs = 1500;   // debounce completed resyncs
        private const int DefaultResyncReqStaleMs = 30000; // offerReadyTimeoutMs(15s) + slack

        /// <summary>Guest lifecycle for the T2.4 panel / tests.</summary>
        public static string Status
        {
            get
            {
                switch (_state)
                {
                    case State.AwaitingAck: return "joining";
                    case State.InSession: return "in-session";
                    case State.Rejected: return "rejected";
                    default: return "idle";
                }
            }
        }

        /// <summary>Subscribe to a Runner's event streams (idempotent per runner).</summary>
        public static void Attach(Runner runner)
        {
            if (runner == null || ReferenceEquals(_runner, runner)) { return; }
            Detach();
            _runner = runner;
            _runner.MessageDecoded += OnMessage;
            _runner.NetEventReceived += OnNetEvent;
            Log.Info("session client attached");
        }

        public static void Detach()
        {
            Runner r = _runner;
            if (r == null) { return; }
            try { r.MessageDecoded -= OnMessage; } catch { }
            try { r.NetEventReceived -= OnNetEvent; } catch { }
            _runner = null;
            EndSessionToIdle();
            Log.Info("session client detached");
        }

        /// <summary>
        /// Session-end reset shared by every leave path: remote disconnect,
        /// HostEnded notice, and the Tick-level transport-stop backstop.
        /// Drops join/resync state and runs SaveReceiver.Reset - which is
        /// what releases MirrorMode, the guest save dir, firewall/pick-gate
        /// counters, DeltaSink watermarks and the whole T5.3 applier state.
        /// </summary>
        private static void EndSessionToIdle()
        {
            _state = State.Idle;
            _retryWatch.Reset();
            _ackWatch.Reset();
            _resyncWatch.Reset();
            _hostBusy = false;
            _resyncOnActivate = false;
            _resyncOnReturn = false;
            _resyncOutstanding = false;
            _resyncReqWatch.Reset();
            _lastMode = MirrorMode.Off;
            SaveReceiver.Reset();
        }

        private static void OnNetEvent(NetTransport.NetEvent evt)
        {
            try
            {
                NetTransport t = _runner != null ? _runner.Transport : null;
                if (t == null || t.CurrentRole != NetTransport.Role.Guest) { return; }

                if (evt.Kind == NetTransport.NetEventKind.Connected)
                {
                    Log.Info("session guest: connected peer=" + evt.PeerId + ", sending hello");
                    SendHello();
                }
                else if (evt.Kind == NetTransport.NetEventKind.Disconnected)
                {
                    if (_state == State.InSession)
                    {
                        Log.Info("session guest: disconnected from host (" + (evt.Reason ?? "?") + ")");
                    }
                    else if (_state != State.Idle)
                    {
                        Log.Info("session guest: connect/handshake ended (" + (evt.Reason ?? "?") + ")");
                    }
                    EndSessionToIdle();
                }
            }
            catch (Exception ex)
            {
                Log.Exception("session guest net-event", ex);
            }
        }

        private static void OnMessage(int peerId, NetMessage msg)
        {
            try
            {
                NetTransport t = _runner != null ? _runner.Transport : null;
                if (t == null || t.CurrentRole != NetTransport.Role.Guest) { return; }
                SessionNoticeMsg notice = msg as SessionNoticeMsg;
                if (notice != null)
                {
                    if (notice.Notice == SessionNoticeKind.HostEnded)
                    {
                        // The host closed the session: stop waiting/retrying and
                        // drop the link gracefully - the transport teardown lands
                        // right behind the notice on the host side anyway, and
                        // the panel's HostEnded banner survives the disconnect.
                        Log.Info("session guest: HostEnded - host closed the session");
                        if (t.ServerPeerId >= 0)
                        {
                            t.Disconnect(t.ServerPeerId);
                        }
                        EndSessionToIdle();
                    }
                    else if (notice.Notice == SessionNoticeKind.HostLeftStrategy)
                    {
                        // Host went to battle/menu: pause the periodic resync
                        // clock (a resync mid-absence would stream a stale
                        // snapshot anyway).
                        _hostBusy = true;
                    }
                    else if (notice.Notice == SessionNoticeKind.HostReturned)
                    {
                        _hostBusy = false;
                        // Any request sent before the departure is stale by
                        // definition - the host-side pending offer either
                        // already streamed (answered) or timed out while the
                        // world was gone. Clear so exactly one fresh request
                        // goes out below.
                        _resyncOutstanding = false;
                        if (_state == State.InSession)
                        {
                            MirrorMode mode = MirrorState.Mode;
                            bool wantResync = _resyncOnReturn;
                            _resyncOnReturn = false;
                            if (mode == MirrorMode.Receiving || mode == MirrorMode.PendingActivate)
                            {
                                // A stream is already in flight, but it was
                                // serialized BEFORE the host returned - its
                                // baseline is stale. Re-sync once it activates
                                // instead of mirroring a pre-return snapshot.
                                _resyncOnActivate = true;
                                Log.Info("session guest: HostReturned mid-stream"
                                    + " - resync deferred to activation");
                            }
                            else if (mode == MirrorMode.Active || wantResync)
                            {
                                // Active: our mirror's snapshot predates the
                                // host's battle/menu round-trip - stale by
                                // definition. wantResync (incl. Detached):
                                // the user asked for a resync while the host
                                // was busy - fire it now that the world is
                                // back instead of stacking a second request
                                // on top of it.
                                RequestResync(ResyncReason.HostRequested,
                                    wantResync && mode != MirrorMode.Active
                                        ? "host returned (deferred request)"
                                        : "host returned to strategy");
                            }
                            // Detached/Off with no pending request: the guest
                            // left the mirror (or never loaded it) - only an
                            // explicit Resync rejoins.
                        }
                    }
                    // Resynced is purely informational: the banner was already
                    // cleared at activation; Runner's "net notice" log is the
                    // <=10 s manual-resync evidence.
                    return;
                }
                // T3.3: save-stream traffic -> SaveReceiver reassembly +
                // sha verify + write + LoadGame (MirrorMode lifecycle there).
                if (msg is SaveOfferMsg offer)
                {
                    // The host answered our ResyncReq - the single-flight
                    // latch drops here (the stream itself is guarded by
                    // Mode==Receiving/PendingActivate until activation).
                    _resyncOutstanding = false;
                    SaveReceiver.OnSaveOffer(_runner, offer);
                    return;
                }
                if (msg is SaveChunkMsg chunk)
                {
                    SaveReceiver.OnSaveChunk(_runner, chunk);
                    return;
                }
                // T5.2: host replication streams -> DeltaSink accounting
                // (T5.3 decodes+applies). Reliable StateDelta seq gaps and
                // latest-state PosDelta drops are counted/logged there.
                if (msg is StateDeltaMsg delta)
                {
                    Replication.DeltaSink.OnDelta(delta);
                    return;
                }
                if (msg is PosDeltaMsg posDelta)
                {
                    Replication.DeltaSink.OnPosDelta(posDelta);
                    return;
                }
                // T5.5: the host's world-hash request - answered at exactly the
                // requested applied seq once StateApplier's drain reaches it.
                if (msg is HashReqMsg hreq)
                {
                    Replication.WorldHash.OnHashReq(_runner, hreq);
                    return;
                }
                HelloAckMsg ack = msg as HelloAckMsg;
                if (ack == null) { return; }

                if (ack.Accepted)
                {
                    _state = State.InSession;
                    _retryWatch.Reset();
                    _ackWatch.Reset();
                    Log.Info("session guest: HelloAck accept sid=" + MsgCodec.ShortId(msg.SessionId));
                }
                else
                {
                    _state = State.Rejected;
                    string why = !string.IsNullOrEmpty(ack.Detail) ? ack.Detail : ack.RejectReason.ToString();
                    Log.Info("session guest: join rejected: " + why);
                    int retryMs = DebugSettings.GetInt("joinRetryMs", 0);
                    if (retryMs > 0)
                    {
                        _retryWatch.Restart();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("session guest msg " + (msg != null ? msg.Type.ToString() : "?"), ex);
            }
        }

        /// <summary>
        /// Runner.Update seam: while Rejected and still connected, re-send
        /// Hello every x2coop.joinRetryMs ms (off by default - a dev-loop
        /// convenience, not shipping behaviour). Also bounds AwaitingAck: no
        /// HelloAck within x2coop.ackTimeoutMs (default 10 s) drops to Idle so
        /// the guest is never stuck "joining" (T2.5).
        ///
        /// T3.6 adds the resync drivers: a -&gt;Active transition restarts the
        /// periodic clock and flushes a deferred resync (HostReturned arriving
        /// mid-stream), and x2coop.resyncMinutes (default 0 = off) re-requests
        /// a full save while a session mirror is Active and the host is on
        /// strategy. A resync in flight reads Receiving/PendingActivate, so
        /// the timer can never stack requests.
        /// </summary>
        public static void Tick()
        {
            try
            {
                // T5.3-r2 (live verify): a LOCAL transport Stop() clears the
                // event queue before Runner can drain it (NetTransport.Stop)
                // so no Disconnected NetEvent ever reaches OnNetEvent - the
                // Runner backstop clears the sid but _state/MirrorMode and
                // every session-scoped replication latch (DeltaSink
                // watermark, StateApplier map+pending+poison, firewall /
                // pick-gate counters, the SaveSuppress LoadGame block) would
                // stay armed for a session that no longer exists: the mirror
                // stays frozen Active and guest-local loads stay blocked
                // forever. Detect the dead guest transport here and run the
                // same end-of-session reset the disconnect path runs.
                NetTransport tNow = _runner != null ? _runner.Transport : null;
                if ((tNow == null || tNow.CurrentRole != NetTransport.Role.Guest
                        || !tNow.IsRunning)
                    && (_state != State.Idle
                        || MirrorState.Mode != MirrorMode.Off))
                {
                    Log.Info("session guest: transport stopped"
                        + " (state=" + _state + " mode=" + MirrorState.Mode
                        + ") - session end reset");
                    EndSessionToIdle();
                }
                SaveReceiver.Tick(_runner); // T3.3 mirror-load pump (world wait + activate)
                Replication.DeltaSink.Tick(); // T5.2 delta-rx stats line
                Replication.StateApplier.Tick(); // T5.3 drain pending deltas into the mirror world

                MirrorMode mode = MirrorState.Mode;
                bool justActivated = mode == MirrorMode.Active && _lastMode != MirrorMode.Active;
                if (mode != _lastMode
                    && (mode == MirrorMode.Detached || mode == MirrorMode.Off))
                {
                    // r1 review (item 4): a deferred resync latched mid-stream
                    // must not survive a detach/fail - otherwise the NEXT
                    // manual resync's activation fires a second, redundant
                    // request. Detached rejoin is the explicit Resync button.
                    _resyncOnActivate = false;
                    _resyncOnReturn = false;
                }
                _lastMode = mode;
                if (justActivated)
                {
                    _resyncWatch.Restart();
                    if (_resyncOnActivate)
                    {
                        _resyncOnActivate = false;
                        RequestResync(ResyncReason.HostRequested,
                            "host returned during mirror load");
                    }
                }
                if (_resyncOutstanding
                    && _resyncReqWatch.ElapsedMilliseconds
                        > DebugSettings.GetInt("resyncReqStaleMs", DefaultResyncReqStaleMs))
                {
                    // The host never answered (its queued offer aborted
                    // silently while off-strategy, or it wedged). Reliable-
                    // ordered delivery means the request arrived; unlatch so
                    // the next trigger is not starved forever.
                    _resyncOutstanding = false;
                    Log.Warn("session guest: ResyncReq unanswered - guard released");
                }
                float resyncMins = DebugSettings.GetFloat("resyncMinutes", 0f);
                if (resyncMins > 0f && _state == State.InSession
                    && mode == MirrorMode.Active && !_hostBusy
                    && _resyncWatch.IsRunning
                    && _resyncWatch.ElapsedMilliseconds >= (long)(resyncMins * 60000f))
                {
                    _resyncWatch.Restart();
                    RequestResync(ResyncReason.ManualRequest,
                        "periodic resyncMinutes=" + resyncMins);
                }

                if (_state == State.AwaitingAck && _ackWatch.IsRunning)
                {
                    int ackTimeoutMs = DebugSettings.GetInt("ackTimeoutMs", 10000);
                    if (ackTimeoutMs > 0 && _ackWatch.ElapsedMilliseconds >= ackTimeoutMs)
                    {
                        _ackWatch.Reset();
                        _state = State.Idle;
                        Log.Warn("session guest: no HelloAck within "
                            + ackTimeoutMs + "ms - back to idle");
                        return;
                    }
                }
                if (_state != State.Rejected || !_retryWatch.IsRunning) { return; }
                int retryMs = DebugSettings.GetInt("joinRetryMs", 0);
                if (retryMs <= 0) { _retryWatch.Reset(); return; }
                if (_retryWatch.ElapsedMilliseconds < retryMs) { return; }
                NetTransport t = _runner != null ? _runner.Transport : null;
                if (t == null || t.CurrentRole != NetTransport.Role.Guest
                    || t.ServerPeerId < 0 || !t.IsConnected(t.ServerPeerId))
                {
                    _state = State.Idle;
                    _retryWatch.Reset();
                    return;
                }
                _retryWatch.Restart();
                Log.Info("session guest: retrying hello");
                SendHello();
            }
            catch (Exception ex)
            {
                Log.Exception("session guest tick", ex);
            }
        }

        /// <summary>
        /// T3.6: send ResyncReq on behalf of an external trigger - the Resync
        /// button (ManualRequest), HostReturned (HostRequested) or the
        /// resyncMinutes timer. Reuses SaveReceiver.SendResyncReq so every
        /// resync shares one wire path + the Resyncing banner flip. Externally
        /// -requested resyncs get a fresh failure-retry budget
        /// (SaveReceiver.NoteExternalResync) - only failure-driven retries are
        /// counted, so a broken stream still bounds instead of looping.
        ///
        /// T3.6-r1 (review I1) single-flight + debounce:
        ///   - _hostBusy: a sent request can only queue a host-side offer that
        ///     starves on WorldReady until the host returns - defer to
        ///     _resyncOnReturn so HostReturned collapses it into the one
        ///     auto-resync instead of double-streaming.
        ///   - Receiving/PendingActivate: a stream is already in flight; the
        ///     request is satisfied by it, not by a re-queue that resets the
        ///     receive buffer mid-transfer.
        ///   - _resyncOutstanding: the request is already on the wire waiting
        ///     for its SaveOffer - merge.
        ///   - x2coop.resyncReqMinMs debounce after completion so click-spam
        ///     can't pipeline N full re-streams.
        /// </summary>
        public static bool RequestResync(ResyncReason reason, string detail)
        {
            NetTransport t = _runner != null ? _runner.Transport : null;
            if (_runner == null || t == null || t.CurrentRole != NetTransport.Role.Guest
                || t.ServerPeerId < 0 || !t.IsConnected(t.ServerPeerId)
                || _state != State.InSession)
            {
                Log.Warn("session guest: ResyncReq skipped - not in a session");
                return false;
            }
            if (_hostBusy)
            {
                _resyncOnReturn = true;
                Log.Info("session guest: ResyncReq deferred (host busy, fires on HostReturned): "
                    + detail);
                return false;
            }
            MirrorMode mode = MirrorState.Mode;
            if (mode == MirrorMode.Receiving || mode == MirrorMode.PendingActivate)
            {
                Log.Info("session guest: ResyncReq skipped - stream already in flight ("
                    + mode + "): " + detail);
                return false;
            }
            if (_resyncOutstanding)
            {
                Log.Info("session guest: ResyncReq merged - request already outstanding: " + detail);
                return false;
            }
            int minMs = DebugSettings.GetInt("resyncReqMinMs", DefaultResyncReqMinMs);
            if (minMs > 0 && _resyncReqWatch.IsRunning
                && _resyncReqWatch.ElapsedMilliseconds < minMs)
            {
                Log.Info("session guest: ResyncReq debounced (<" + minMs + "ms since send): " + detail);
                return false;
            }
            SaveReceiver.NoteExternalResync();
            // SendResyncReq -> NoteResyncSent latches the single-flight flag
            // on an actual wire send (internal failure retries take the same
            // latch so manual requests can't race them either).
            return SaveReceiver.SendResyncReq(_runner, reason, detail);
        }

        /// <summary>
        /// Latch for the single-flight guard - SaveReceiver.SendResyncReq
        /// calls it on every wire send (external requests AND internal
        /// failure retries), so "in flight" means "a request is out there",
        /// not just a button press.
        /// </summary>
        internal static void NoteResyncSent()
        {
            _resyncOutstanding = true;
            _resyncReqWatch.Restart();
        }

        /// <summary>
        /// T3.6-r1 panel seam: a ResyncReq is on the wire unanswered. The
        /// Resync button greys while this or a stream (Mode Receiving /
        /// PendingActivate) is live; the stale timeout above re-arms it so a
        /// silently-dropped request can't wedge the recovery button.
        /// </summary>
        internal static bool ResyncRequestInFlight => _resyncOutstanding;

        /// <summary>
        /// T3.6: the guest's ACTIVE mirror world died mid-session (SaveReceiver
        /// detach path) - tell the host via SessionNotice{GuestMirrorLost} so
        /// its bookkeeping knows the mirror is gone, and surface the loss on
        /// the panel (Resync button rejoins).
        /// </summary>
        internal static void NotifyMirrorLost()
        {
            NetTransport t = _runner != null ? _runner.Transport : null;
            if (_runner == null || t == null || t.CurrentRole != NetTransport.Role.Guest
                || t.ServerPeerId < 0 || !t.IsConnected(t.ServerPeerId)
                || _state != State.InSession)
            {
                return;
            }
            byte[] wire = MsgCodec.Encode(new SessionNoticeMsg
            {
                SessionId = _runner.SessionId,
                Notice = SessionNoticeKind.GuestMirrorLost,
            });
            if (wire != null && t.Send(t.ServerPeerId, MsgCodec.ChannelFor(MsgType.SessionNotice),
                wire, 0, wire.Length, DeliveryMethod.ReliableOrdered))
            {
                Log.Info("session guest: SessionNotice{GuestMirrorLost} sent");
            }
            try { UI.CoopPanel.NoteMirrorLost(); }
            catch (Exception ex) { Log.Exception("session guest ui-note", ex); }
        }

        /// <summary>
        /// Build + send the Hello (SessionId stays Guid.Empty - pre-session).
        /// Public so T2.4's Join button can re-drive it; also used by retries.
        /// </summary>
        public static void SendHello()
        {
            NetTransport t = _runner != null ? _runner.Transport : null;
            if (t == null || t.CurrentRole != NetTransport.Role.Guest || t.ServerPeerId < 0)
            {
                return;
            }
            var hello = new HelloMsg { SessionId = Guid.Empty };
            SessionHello.Collect(hello);
            byte[] data = MsgCodec.Encode(hello);
            if (data == null)
            {
                Log.Warn("session guest: hello encode failed");
                return;
            }
            if (t.Send(t.ServerPeerId, MsgCodec.ChannelFor(MsgType.Hello),
                data, 0, data.Length, DeliveryMethod.ReliableOrdered))
            {
                _state = State.AwaitingAck;
                _ackWatch.Restart();
                Log.Info("session guest: hello sent len=" + data.Length
                    + " modVer=" + hello.ModVersion
                    + " gameVer=" + hello.GameVersion
                    + " packs=" + (hello.Packs != null ? hello.Packs.Length : 0));
            }
        }
    }
}
