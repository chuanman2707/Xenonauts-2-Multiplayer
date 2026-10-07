using System;
using System.Diagnostics;
using FlyingWormConsole3.LiteNetLib;
using X2Coop.Mirror;
using X2Coop.Net;
using X2Coop.Runtime;

namespace X2Coop.Session
{
    /// <summary>
    /// Host side of the session handshake (spec section 6): answers Hello with
    /// HelloAck-accept or a human-readable reject. Attached to the Runner's
    /// decoded-message/event streams; active only while the transport role is
    /// Host - a guest-role instance ignores everything.
    ///
    /// Accept path (order matters):
    ///   1. assign the session id FIRST via Runner.SetSessionId - the guest's
    ///      keepalive Pings already carry it, and without this they would hit
    ///      the codec's SessionMismatch drop the moment they arrive;
    ///   2. set MirrorState.HostJoinFreeze - the ClockFreeze prefix on
    ///      TimeTransitionSystem.UpdateTimeOnBase (Mirror/ClockFreeze.cs,
    ///      T3.2 host half) holds the geoscape clock from here until
    ///      GuestReady/abort;
    ///   3. mark the guest peer and send HelloAck{Accepted, SessionId=sid};
    ///   4. log "HelloAck accept" (never the player name);
    ///   5. SaveStreamer.QueueOnAccept - baselineSeq snapshot, then the
    ///      deferred SaveOffer + SaveChunk stream (T3.2).
    ///
    /// Reject order: SessionFull (S1 = 1 guest) -> parity mismatch
    /// (protocolVer/modVer/gameVer/commits/packs via SessionParity.Compare) ->
    /// NotOnStrategy (T3.1 ScreenGuard: live, undisposed "StrategyWorld" bound
    /// by lifecycle OnWorldCreate/OnWorldDispose). Parity outranks screen
    /// state on purpose: a version mismatch is unfixable-by-waiting, so it is
    /// reported even from the main menu.
    ///
    /// A rejected peer stays CONNECTED (no kick): the guest may retry Hello
    /// (x2coop.joinRetryMs / a future Join button) after e.g. the host reaches
    /// the geoscape. Its in-session messages would fail the sessionId gate, so
    /// a connected-but-rejected peer is inert.
    ///
    /// T2.5 teardown paths:
    ///   - guest peer disconnect (timeout or graceful): BroadcastNotice
    ///     GuestLeft to any remaining peers (none in S1) + a literal
    ///     "GuestLeft" log line, then ClearGuest frees the slot/sid so a
    ///     rejoin works immediately.
    ///   - host ends the session (panel Stop / dev K / x2coop.stopHostAfterMs):
    ///     <see cref="RequestStop"/> sends HostEnded to the guest and stops the
    ///     transport <see cref="GracefulStopDelayMs"/> later from
    ///     <see cref="Tick"/> - LiteNetLib NetManager.Stop() flushes only its
    ///     own disconnect packet, so a same-frame stop would drop the reliable
    ///     notice on the floor.
    /// </summary>
    public static class SessionHost
    {
        /// <summary>
        /// Delay between the HostEnded send and socket teardown. Also used by
        /// Runner.TeardownNow: pack teardown cannot wait for a Tick deferral,
        /// so it blocks the main thread for this span instead (T2.6).
        /// </summary>
        public const int GracefulStopDelayMs = 300;

        private static Runner _runner;
        private static int _guestPeerId = -1;
        private static readonly Stopwatch _stopWatch = new Stopwatch();   // pending graceful stop
        private static readonly Stopwatch _hostSince = new Stopwatch();   // x2coop.stopHostAfterMs timer

        /// <summary>The peer that owns the live session, -1 when free.</summary>
        public static int GuestPeerId => _guestPeerId;

        /// <summary>True while a guest holds the (single) session slot.</summary>
        public static bool HasGuest => _guestPeerId >= 0;

        /// <summary>Subscribe to a Runner's event streams (idempotent per runner).</summary>
        public static void Attach(Runner runner)
        {
            if (runner == null || ReferenceEquals(_runner, runner)) { return; }
            Detach();
            _runner = runner;
            _runner.MessageDecoded += OnMessage;
            _runner.NetEventReceived += OnNetEvent;
            Log.Info("session host attached");
        }

        /// <summary>
        /// Unsubscribe + teardown. Best-effort HostEnded notice first (the
        /// transport may already be stopped during OnDestroy - Send fails
        /// gracefully and logs).
        /// </summary>
        public static void Detach()
        {
            Runner r = _runner;
            if (r == null) { return; }
            try
            {
                if (_guestPeerId >= 0)
                {
                    SendNotice(_guestPeerId, SessionNoticeKind.HostEnded);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("session host detach-notice", ex);
            }
            try { r.MessageDecoded -= OnMessage; } catch { }
            try { r.NetEventReceived -= OnNetEvent; } catch { }
            _runner = null;
            _stopWatch.Reset();
            _hostSince.Reset();
            ClearGuest("guest left (detach)");
            Log.Info("session host detached");
        }

        private static void OnNetEvent(NetTransport.NetEvent evt)
        {
            try
            {
                if (evt.Kind != NetTransport.NetEventKind.Disconnected) { return; }
                if (evt.PeerId == _guestPeerId && _guestPeerId >= 0)
                {
                    // Broadcast BEFORE ClearGuest so the notice still carries the
                    // live session id (S1 has no other peers - spec §6 forward path).
                    BroadcastNotice(SessionNoticeKind.GuestLeft, evt.PeerId);
                    ClearGuest("GuestLeft peer=" + evt.PeerId + " reason=" + (evt.Reason ?? "?"));
                }
            }
            catch (Exception ex)
            {
                Log.Exception("session host net-event", ex);
            }
        }

        private static void OnMessage(int peerId, NetMessage msg)
        {
            try
            {
                if (_runner == null || _runner.Transport == null
                    || _runner.Transport.CurrentRole != NetTransport.Role.Host)
                {
                    return;
                }
                if (msg is HelloMsg hello)
                {
                    HandleHello(peerId, hello);
                }
                else if (msg is GuestReadyMsg ready)
                {
                    HandleGuestReady(peerId, ready);
                }
                else if (msg is ResyncReqMsg resync)
                {
                    HandleResyncReq(peerId, resync);
                }
                else if (msg is SessionNoticeMsg notice)
                {
                    HandleSessionNotice(peerId, notice);
                }
                else if (msg is HashRespMsg hresp)
                {
                    // T5.5: the guest's world-hash answer to our HashReq.
                    Replication.WorldHash.OnHashResp(peerId, hresp);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("session host msg " + (msg != null ? msg.Type.ToString() : "?"), ex);
            }
        }

        /// <summary>
        /// T3.6: guest-originated notices. GuestMirrorLost means the guest's
        /// mirror world died mid-session (it detached, suppression still
        /// armed) - bookkeeping only; the guest's Resync press / reconnect is
        /// the recovery path, the host takes no action itself.
        /// </summary>
        private static void HandleSessionNotice(int peerId, SessionNoticeMsg notice)
        {
            if (peerId != _guestPeerId) { return; }
            if (notice.Notice == SessionNoticeKind.GuestMirrorLost)
            {
                Log.Info("session host: guest mirror lost (its strategy world"
                    + " died mid-session) - awaiting explicit resync");
            }
        }

        private static void HandleHello(int peerId, HelloMsg hello)
        {
            // Idempotent re-handshake: a Hello retry from the ALREADY-ACCEPTED
            // guest re-sends the same session id (rekeying would desync it).
            if (_guestPeerId >= 0 && peerId == _guestPeerId && _runner.SessionId != Guid.Empty)
            {
                SendAck(peerId, accepted: true, _runner.SessionId, HelloRejectReason.None, string.Empty);
                Log.Info("session host: HelloAck accept (re-handshake) peer=" + peerId
                    + " sid=" + MsgCodec.ShortId(_runner.SessionId));
                // T3.2 review fix: a re-Hello after the stream aborted (send
                // fail / strategy-left / world-timeout) would otherwise
                // dead-end - acked but never re-offered. Re-queue only when
                // no complete stream went out; a completed stream needs no
                // re-send (ReliableOrdered delivers or the peer dies; deeper
                // recovery is T3.6 ResyncReq).
                if (!SaveStreamer.StreamCompleted)
                {
                    SaveStreamer.QueueOnAccept(_runner, peerId, _runner.SessionId);
                }
                return;
            }

            if (_guestPeerId >= 0)
            {
                Reject(peerId, HelloRejectReason.SessionFull,
                    "session is full (1 guest max)");
                return;
            }

            var local = new HelloMsg();
            SessionHello.Collect(local);
            HelloRejectReason parity = SessionParity.Compare(local, hello, out string detail);
            if (parity != HelloRejectReason.None)
            {
                Reject(peerId, parity, detail);
                return;
            }

            if (!ScreenGuard.IsOnStrategy)
            {
                Reject(peerId, HelloRejectReason.NotOnStrategy,
                    "host is not on the Strategy screen");
                return;
            }

            Guid sid = Guid.NewGuid();
            _runner.SetSessionId(sid);          // BEFORE the ack (guest Pings carry it)
            MirrorState.HostJoinFreeze = true;  // T3.2/T3.4: ClockFreeze prefix now gates on this
            _guestPeerId = peerId;
            SendAck(peerId, accepted: true, sid, HelloRejectReason.None, string.Empty);
            BroadcastNotice(SessionNoticeKind.GuestJoined, peerId); // spec §6 (no-op with 1 peer)
            Log.Info("session host: HelloAck accept peer=" + peerId
                + " sid=" + MsgCodec.ShortId(sid)
                + " modVer=" + local.ModVersion
                + " gameVer=" + local.GameVersion
                + " packs=" + (local.Packs != null ? local.Packs.Length : 0));
            // T3.2 spec §6: accept -> freeze (latched above) -> baseline
            // snapshot -> save stream. Deferred ~150ms inside SaveStreamer so
            // the HelloAck wins the wire race (no cross-channel ordering).
            SaveStreamer.QueueOnAccept(_runner, peerId, sid);
        }

        /// <summary>
        /// G->H "save loaded, ready for deltas" - the join freeze's release
        /// trigger (spec §6: released exactly once at GuestReady or abort).
        /// T5.2 will flush buffered deltas > baselineSeq here first; S1 has
        /// none, so this is unfreeze + log. A Ready status also closes the
        /// T3.6 resync loop: SessionNotice{Resynced} goes back to the guest
        /// as the host-observed "the re-synced mirror is live" boundary.
        /// A non-Ready status still releases the freeze - the host must
        /// never stay paused for a broken guest (its ResyncReq/disconnect is
        /// its own recovery path).
        /// </summary>
        private static void HandleGuestReady(int peerId, GuestReadyMsg ready)
        {
            if (peerId != _guestPeerId)
            {
                Log.Warn("session host: GuestReady from non-guest peer=" + peerId + " ignored");
                return;
            }
            bool was = MirrorState.HostJoinFreeze;
            MirrorState.HostJoinFreeze = false;
            Log.Info("session host: GuestReady peer=" + peerId
                + " status=" + ready.Status
                + (was ? " - join-freeze released" : " (join-freeze already clear)")
                + (ready.Status == GuestReadyStatus.Ready
                    ? string.Empty
                    : " - guest did NOT load cleanly"));
            if (ready.Status == GuestReadyStatus.Ready)
            {
                // T5.2: buffered join-window deltas (all seq > baselineSeq)
                // flush ordered ahead of the live stream. An over-cap buffer
                // was dropped - the guest's world is missing those ops, so
                // force a fresh resync instead of sending the Resynced ack.
                if (Replication.DeltaSender.OnGuestReady(_runner, peerId, ready.Status))
                {
                    Log.Warn("session host: join deltas overflowed - forcing resync");
                    SaveStreamer.QueueOnAccept(_runner, peerId, _runner.SessionId);
                    return;
                }
                SendNotice(peerId, SessionNoticeKind.Resynced);
                // T5.5: arm the scripted post-activation hash check when
                // x2coop.hashOnReadyMs > 0 (covers the first join AND every
                // resync - each GuestReady re-arms one request).
                Replication.WorldHash.NoteGuestReady();
            }
            else
            {
                // Non-Ready: drop any buffered join deltas - the guest's own
                // ResyncReq is the recovery path (its load failed).
                Replication.DeltaSender.OnGuestReady(_runner, peerId, ready.Status);
            }
        }

        /// <summary>
        /// T3.6 resync request (guest button / HostReturned relay / periodic
        /// timer / stream-load failure): re-queue the streamer against the
        /// same session - QueueOnAccept re-latches HostJoinFreeze (T3.5 fold)
        /// and the deferred send re-serializes the live world, so every
        /// resync carries a NEW baselineSeq, fresh sha and a full file. The
        /// loop closes when the guest's GuestReady lands back in
        /// <see cref="HandleGuestReady"/> -> SessionNotice{Resynced}.
        /// </summary>
        private static void HandleResyncReq(int peerId, ResyncReqMsg resync)
        {
            if (peerId != _guestPeerId || _runner.SessionId == Guid.Empty)
            {
                Log.Warn("session host: ResyncReq from non-guest/no-session peer="
                    + peerId + " ignored");
                return;
            }
            Log.Info("session host: ResyncReq peer=" + peerId
                + " reason=" + resync.Reason
                + " detail=\"" + (resync.Detail ?? string.Empty)
                + "\" -> re-streaming save (new baselineSeq)");
            SaveStreamer.QueueOnAccept(_runner, peerId, _runner.SessionId);
        }

        private static void Reject(int peerId, HelloRejectReason reason, string detail)
        {
            SendAck(peerId, accepted: false, Guid.Empty, reason, detail);
            Log.Info("session host: HelloAck reject peer=" + peerId
                + " reason=" + reason + " detail=\"" + (detail ?? string.Empty) + "\"");
        }

        private static void SendAck(int peerId, bool accepted, Guid sid,
            HelloRejectReason reason, string detail)
        {
            var ack = new HelloAckMsg
            {
                SessionId = sid,    // Empty on reject (pre-session)
                Accepted = accepted,
                RejectReason = reason,
                Detail = detail ?? string.Empty,
            };
            byte[] data = MsgCodec.Encode(ack);
            if (data == null) { return; }
            _runner.Transport.Send(peerId, MsgCodec.ChannelFor(MsgType.HelloAck),
                data, 0, data.Length, DeliveryMethod.ReliableOrdered);
        }

        private static void SendNotice(int peerId, SessionNoticeKind kind)
        {
            Runner r = _runner;
            if (r == null || r.Transport == null) { return; }
            byte[] data = MsgCodec.Encode(new SessionNoticeMsg
            {
                SessionId = r.SessionId,
                Notice = kind,
            });
            if (data == null) { return; }
            r.Transport.Send(peerId, MsgCodec.ChannelFor(MsgType.SessionNotice),
                data, 0, data.Length, DeliveryMethod.ReliableOrdered);
        }

        /// <summary>Notice to every connected peer except <paramref name="exceptPeerId"/>.</summary>
        private static void BroadcastNotice(SessionNoticeKind kind, int exceptPeerId)
        {
            Runner r = _runner;
            NetTransport t = r != null ? r.Transport : null;
            if (t == null) { return; }
            byte[] data = MsgCodec.Encode(new SessionNoticeMsg
            {
                SessionId = r.SessionId,
                Notice = kind,
            });
            if (data == null) { return; }
            byte ch = MsgCodec.ChannelFor(MsgType.SessionNotice);
            foreach (int id in t.PeerIds)
            {
                if (id == exceptPeerId) { continue; }
                t.Send(id, ch, data, 0, data.Length, DeliveryMethod.ReliableOrdered);
            }
        }

        /// <summary>
        /// T3.1 ScreenGuard feed: the host's StrategyWorld became live
        /// (<paramref name="onStrategy"/>=true, OnWorldCreate) or was disposed
        /// (false, OnWorldDispose - battle/main-menu/load transitions). While
        /// a guest is in-session this sends HostReturned / HostLeftStrategy so
        /// the guest's "Host busy" banner follows the host's screen. No guest
        /// -> nothing to send (a rejected-but-connected peer would gate the
        /// session id out at the codec anyway). T3.3 owns the on-return full
        /// resync; this seam only carries the notice.
        /// </summary>
        public static void NotifyStrategyPresence(bool onStrategy)
        {
            try
            {
                Runner r = _runner;
                if (r == null || r.Transport == null
                    || r.Transport.CurrentRole != NetTransport.Role.Host
                    || _guestPeerId < 0)
                {
                    return;
                }
                if (!onStrategy && MirrorState.HostJoinFreeze)
                {
                    // T3.2 review fix: the join is semantically dead once the
                    // serialized world is disposed (the streamed save is
                    // stale; the guest resyncs on HostReturned per T3.6), so
                    // the freeze must NOT leak into the next StrategyWorld -
                    // with a never-ready guest it would freeze the new world
                    // indefinitely. Release + drop any queued stream.
                    MirrorState.HostJoinFreeze = false;
                    SaveStreamer.Reset("strategy-left");
                    Log.Info("join-freeze released (strategy-left)");
                    // T5.2: pending deltas/join buffer all reference the
                    // disposed world - drop them now (the next Consume would
                    // anyway, but never leave stale state latched).
                    try { Replication.DeltaSender.Reset("strategy-left"); }
                    catch (Exception ex) { Log.Exception("delta strategy-left", ex); }
                }
                SessionNoticeKind kind = onStrategy
                    ? SessionNoticeKind.HostReturned
                    : SessionNoticeKind.HostLeftStrategy;
                SendNotice(_guestPeerId, kind);
                Log.Info("session host: " + kind + " -> peer=" + _guestPeerId);
            }
            catch (Exception ex)
            {
                Log.Exception("session host presence-notice", ex);
            }
        }

        /// <summary>
        /// Graceful host-session end (T2.5): send HostEnded to the guest, then
        /// stop the transport GracefulStopDelayMs later from <see cref="Tick"/>.
        /// The delay matters: NetManager.Stop() transmits only its own
        /// disconnect packet - a reliable send queued in the same frame is
        /// dropped before the logic thread can flush it. Safe in any role:
        /// non-host callers fall back to an immediate transport stop.
        /// </summary>
        public static void RequestStop()
        {
            Runner r = _runner;
            NetTransport t = r != null ? r.Transport : null;
            if (t == null || t.CurrentRole != NetTransport.Role.Host)
            {
                try { t?.Stop(); }
                catch (Exception ex) { Log.Exception("session host stop(non-host)", ex); }
                return;
            }
            if (_stopWatch.IsRunning) { return; } // already pending
            if (_guestPeerId >= 0)
            {
                SendNotice(_guestPeerId, SessionNoticeKind.HostEnded);
                Log.Info("session host: HostEnded -> peer=" + _guestPeerId);
            }
            Log.Info("session host: graceful stop pending (" + GracefulStopDelayMs + "ms)");
            _stopWatch.Restart();
        }

        /// <summary>
        /// Runner.Update seam. Fires the deferred transport stop after a
        /// <see cref="RequestStop"/>, and services the x2coop.stopHostAfterMs
        /// dev knob (0 = off): a deterministic "end the session N ms after the
        /// listener came up" teardown for scripted two-instance verifies.
        /// </summary>
        public static void Tick()
        {
            try
            {
                Runner r = _runner;
                NetTransport t = r != null ? r.Transport : null;
                if (t == null) { return; }

                // T3.2: deferred save-stream send + join-freeze heartbeat.
                SaveStreamer.Tick();

                if (t.CurrentRole == NetTransport.Role.Host)
                {
                    int stopMs = DebugSettings.GetInt("stopHostAfterMs", 0);
                    if (stopMs > 0)
                    {
                        if (!_hostSince.IsRunning)
                        {
                            _hostSince.Restart();
                        }
                        else if (!_stopWatch.IsRunning
                            && _hostSince.ElapsedMilliseconds >= stopMs)
                        {
                            Log.Info("session host: stopHostAfterMs=" + stopMs
                                + " elapsed -> ending session");
                            RequestStop();
                        }
                    }
                }
                else if (_hostSince.IsRunning)
                {
                    _hostSince.Reset();
                }

                if (!_stopWatch.IsRunning
                    || _stopWatch.ElapsedMilliseconds < GracefulStopDelayMs)
                {
                    return;
                }
                _stopWatch.Reset();
                Log.Info("session host: stopping transport (session ended)");
                try { t.Stop(); }
                catch (Exception ex) { Log.Exception("session host stop", ex); }
                ClearGuest("host ended session");
            }
            catch (Exception ex)
            {
                Log.Exception("session host tick", ex);
            }
        }

        /// <summary>
        /// T5.2-r1 (review C2) join watchdog target: the guest never answered
        /// the streamed save within x2coop.joinReadyTimeoutMs - wedged
        /// mid-load (the T5.2 cleanup-marker bug), dead mid-stream, or its
        /// process killed so hard its own keepalive timeout is still minutes
        /// away. Best-effort HostEnded, a real transport-level Disconnect
        /// (a WEDGED guest never hits LiteNetLib's 15 s silence timeout on
        /// its own), then the normal ClearGuest sweep: guest slot, session
        /// id, join freeze, streamer + delta + capture state. Idempotent -
        /// the disconnect event's own ClearGuest finds the slot already free.
        /// </summary>
        public static void DropStalledGuest(string why)
        {
            try
            {
                Runner r = _runner;
                NetTransport t = r != null ? r.Transport : null;
                int peer = _guestPeerId;
                if (peer >= 0 && t != null
                    && t.CurrentRole == NetTransport.Role.Host && t.IsConnected(peer))
                {
                    SendNotice(peer, SessionNoticeKind.HostEnded);
                    t.Disconnect(peer);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("session host drop-stalled", ex);
            }
            ClearGuest(why);
        }

        /// <summary>
        /// Drop the guest slot, the session id and the join freeze.
        /// <paramref name="why"/> is the payload of the "session host:" line.
        /// </summary>
        private static void ClearGuest(string why)
        {
            bool had = _guestPeerId >= 0;
            _guestPeerId = -1;
            SaveStreamer.Reset(why); // drop any queued/in-flight stream state
            try { Replication.ReplicationSource.Reset(); } // T5.1: drop pending capture ops (live component refs must not survive session end)
            catch (Exception ex) { Log.Exception("capture session-reset", ex); }
            try { Replication.DeltaSender.Reset(why); } // T5.2: drop queued deltas + join buffer + serializer binding
            catch (Exception ex) { Log.Exception("delta session-reset", ex); }
            try { Replication.WorldHash.Reset(why); } // T5.5: drop in-flight hash req/resp state
            catch (Exception ex) { Log.Exception("hash session-reset", ex); }
            if (MirrorState.HostJoinFreeze)
            {
                MirrorState.HostJoinFreeze = false;
                Log.Info("join-freeze released (" + (why ?? "?") + ")");
            }
            Runner r = _runner;
            if (r != null && r.SessionId != Guid.Empty)
            {
                r.SetSessionId(Guid.Empty);
            }
            if (had)
            {
                Log.Info("session host: " + why);
            }
        }
    }
}
