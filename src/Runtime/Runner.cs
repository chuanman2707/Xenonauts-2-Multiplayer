using System;
using System.Diagnostics;
using FlyingWormConsole3.LiteNetLib;
using UnityEngine;
using X2Coop.Net;

namespace X2Coop.Runtime
{
    /// <summary>
    /// The single Unity-thread pump for all co-op work (spec §4: every world
    /// access stays on the main thread). Lives on a DontDestroyOnLoad GameObject
    /// named "X2CoopRunner", created by X2CoopLifecycle.Create via
    /// <see cref="EnsureStarted"/> and destroyed by <see cref="Shutdown"/> on
    /// pack disable.
    ///
    /// Frame order (seams later tasks extend):
    ///   Update()      → Transport.PollEvents()  — LiteNetLib callbacks land
    ///                   HERE on the main thread — then the NetEvent queue is
    ///                   drained through DispatchNetEvent → NetEventReceived
    ///                   (T2.2 codec / T2.3 session subscribe there).
    ///   LateUpdate()  → FlushReplication()      — T5.2 coalesced-delta flush.
    ///   OnGUI()       → DrawCoopPanel()         — T2.4 co-op IMGUI panel.
    /// </summary>
    public sealed class Runner : MonoBehaviour
    {
        /// <summary>Name of the root GameObject (DontDestroyOnLoad).</summary>
        public const string RootName = "X2CoopRunner";

        public static Runner Instance { get; private set; }

        /// <summary>The live transport. Non-null after Awake.</summary>
        public NetTransport Transport { get; private set; }

        /// <summary>
        /// Main-thread notification stream for the session layer: every
        /// NetTransport event dequeued this frame is pushed here (T2.3 session
        /// subscribes; transport logs cover the T2.1 verify).
        /// </summary>
        public event Action<NetTransport.NetEvent> NetEventReceived;

        /// <summary>
        /// Decoded protocol messages (T2.3 session layer subscribes - no
        /// re-decode needed downstream). Fires only for messages that passed
        /// the codec's sessionId/bounds validation, on the main thread.
        /// </summary>
        public event Action<int, NetMessage> MessageDecoded;

        /// <summary>
        /// The live session id (envelope of every message). Empty before a
        /// session exists - codec allows Hello and HelloAck through in that
        /// state; T2.3 assigns via <see cref="SetSessionId"/>.
        /// </summary>
        public Guid SessionId => _sessionId;

        /// <summary>Session-layer hook (T2.3): set or clear the envelope id.</summary>
        public void SetSessionId(Guid id)
        {
            if (_sessionId == id) { return; }
            _sessionId = id;
            Log.Info("net session " + (id == Guid.Empty ? "cleared" : "id=" + MsgCodec.ShortId(id)));
        }

        /// <summary>
        /// Idempotent: returns the live Runner, or builds the GameObject +
        /// component. Awake runs synchronously inside AddComponent.
        /// </summary>
        public static Runner EnsureStarted()
        {
            if (Instance != null && !Instance._tornDown)
            {
                return Instance;
            }
            Instance = null; // dead or mid-teardown - replace it
            GameObject go = GameObject.Find(RootName);
            Runner r = go != null ? go.GetComponent<Runner>() : null;
            if (r != null && r._tornDown)
            {
                // A stale object awaiting its end-of-frame destroy (a
                // re-enable inside the teardown frame) - drop it and spawn a
                // fresh one; a torn-down component must never be handed out.
                UnityEngine.Object.Destroy(go);
                r = null;
                go = null;
            }
            if (r == null)
            {
                if (go == null)
                {
                    go = new GameObject(RootName);
                }
                r = go.AddComponent<Runner>();
            }
            return r;
        }

        /// <summary>
        /// Stop the transport + destroy the Runner object. TeardownNow runs
        /// SYNCHRONOUSLY here: the socket must be released before the caller
        /// (lifecycle Destroy / netstat check) returns - Object.Destroy only
        /// schedules the GameObject's death for end-of-frame, where OnDestroy
        /// re-runs TeardownNow as a no-op.
        /// </summary>
        public static void Shutdown()
        {
            Runner r = Instance;
            if (r == null)
            {
                GameObject go = GameObject.Find(RootName);
                if (go != null)
                {
                    r = go.GetComponent<Runner>();
                }
            }
            Instance = null;
            if (r != null)
            {
                r.TeardownNow();
                UnityEngine.Object.Destroy(r.gameObject);
            }
        }

        private void Awake()
        {
            if (Instance != null && !ReferenceEquals(Instance, this))
            {
                // A second copy must never own the transport.
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            try
            {
                // An unfocused instance must keep pumping: without this the
                // player loop pauses when the window loses focus and the peer
                // times out (DisconnectTimeout=15000). Host stays authoritative
                // while minimized; required for two-instance dev on one PC.
                if (!Application.runInBackground)
                {
                    Application.runInBackground = true;
                    Log.Info("runner: runInBackground was false -> set true");
                }
            }
            catch (Exception ex)
            {
                Log.Exception("runner runInBackground", ex);
            }
            try
            {
                Transport = new NetTransport();
            }
            catch (Exception ex)
            {
                Log.Exception("runner transport-init", ex);
            }
            try
            {
                DebugSettings.LogActive();
            }
            catch (Exception ex)
            {
                Log.Exception("runner dev-settings", ex);
            }
            try
            {
                // T2.3 session layer: host answers Hello, guest sends it on
                // connect. Both self-gate on the transport role.
                Session.SessionHost.Attach(this);
                Session.SessionClient.Attach(this);
            }
            catch (Exception ex)
            {
                Log.Exception("runner session-attach", ex);
            }
            try
            {
                // T2.4 co-op panel: banner state feeds off the decoded stream.
                UI.CoopPanel.Attach(this);
            }
            catch (Exception ex)
            {
                Log.Exception("runner ui-attach", ex);
            }
            Log.Info("runner: started go=" + RootName);
        }

        private void OnDestroy()
        {
            TeardownNow();
        }

        /// <summary>
        /// Synchronous session+transport teardown - idempotent, runs at most
        /// once per component. Called from <see cref="Shutdown"/> (pack
        /// teardown via lifecycle Destroy, or app quit via
        /// <see cref="OnApplicationQuit"/>) and re-entered harmlessly by
        /// OnDestroy when Unity finally destroys the GameObject.
        ///
        /// T2.6 ordering decision: SessionHost.Detach sends HostEnded while
        /// the socket is ALIVE, then we block the main thread for
        /// <see cref="Session.SessionHost.GracefulStopDelayMs"/> so LiteNetLib's
        /// logic thread can flush the reliable notice before Stop() kills it
        /// (T2.5: a same-frame Stop drops it). The usual deferred stop via
        /// SessionHost.Tick cannot run here - the runner dies inside this
        /// frame and never sees another Update - so a short sleep is the only
        /// way to keep the notice. 300 ms on a menu APPLY or process exit is
        /// acceptable; teardown is never a hot path.
        /// </summary>
        private void TeardownNow()
        {
            if (_tornDown) { return; }
            _tornDown = true;
            bool noticeInFlight = false;
            try
            {
                NetTransport t = Transport;
                noticeInFlight = t != null && t.IsRunning
                    && t.CurrentRole == NetTransport.Role.Host
                    && Session.SessionHost.HasGuest;
            }
            catch
            {
            }
            // Session layer first: it may still send a HostEnded notice; then
            // the socket. Both tolerate a dead transport.
            try { Session.SessionHost.Detach(); Session.SessionClient.Detach(); }
            catch (Exception ex) { Log.Exception("runner session-detach", ex); }
            try { UI.CoopPanel.Detach(); }
            catch (Exception ex) { Log.Exception("runner ui-detach", ex); }
            if (noticeInFlight)
            {
                try { System.Threading.Thread.Sleep(Session.SessionHost.GracefulStopDelayMs); }
                catch { }
            }
            try { Transport?.Stop(); }
            catch (Exception ex) { Log.Exception("runner destroy-stop", ex); }
            if (ReferenceEquals(Instance, this))
            {
                Instance = null;
            }
            Log.Info("runner: stopped");
        }

        private void OnApplicationQuit()
        {
            // The game skips lifecycle Destroy at quit (Unpatch never runs);
            // run the same teardown so the peer sees HostEnded instead of a
            // 15 s timeout and the profile scratch dirs are still cleaned.
            try { Teardown.Run(); }
            catch (Exception ex) { Log.Exception("runner quit-teardown", ex); }
        }

        private void Update()
        {
            try
            {
                Transport?.PollEvents();
            }
            catch (Exception ex)
            {
                Log.Exception("runner poll", ex);
            }
            DrainNetEvents();
            PumpKeepAlive();
            // Session-layer per-frame tick (guest join-retry cadence, host
            // graceful-stop delay + stopHostAfterMs knob).
            try { Session.SessionClient.Tick(); }
            catch (Exception ex) { Log.Exception("runner session-tick", ex); }
            try { Session.SessionHost.Tick(); }
            catch (Exception ex) { Log.Exception("runner host-tick", ex); }
            // T5.5: world-hash pump - host auto-trigger + resp timeout, guest
            // req watermark sweep + stale-req timeouts. Self-gating per role.
            try { Replication.WorldHash.Tick(this); }
            catch (Exception ex) { Log.Exception("runner hash-tick", ex); }
            // T4.1: the firewall's log tap only COUNTS correlated exceptions
            // (it runs inside Unity's log dispatch - reporting/logging there
            // would recurse); Tick flushes the report + the auto-resync on
            // the main thread.
            try { Mirror.MirrorFirewall.Tick(); }
            catch (Exception ex) { Log.Exception("runner firewall-tick", ex); }
            // T4.2: scripted guest-interaction run marker (Home key).
            try { PollRunMarker(); }
            catch (Exception ex) { Log.Exception("runner run-marker", ex); }
            // T2.4 panel hotkey (per-frame input state - not reliable in OnGUI).
            try { UI.CoopPanel.PollInput(); }
            catch (Exception ex) { Log.Exception("runner ui-poll", ex); }
#if X2COOP_PROBES
            Probes.MutationDelayProbe.SampleUpdate();
            DevPoll();
#endif
        }

        private void DrainNetEvents()
        {
            NetTransport t = Transport;
            // T3.1: a local Stop() (panel Stop button, teardown) drops the
            // queued Disconnected event silently, so the envelope sid would
            // survive the socket. A guest left with a stale expected sid then
            // drops every future HelloAck (accept AND reject) at the codec and
            // can never rejoin. Clear on "socket not running" - a superset of
            // the disconnect-event clear that also covers the host role
            // (SessionHost's own clear is idempotent).
            if (_sessionId != Guid.Empty && (t == null || !t.IsRunning))
            {
                Log.Info("net session cleared (transport stopped)");
                _sessionId = Guid.Empty;
            }
            if (t == null)
            {
                return;
            }
            while (t.TryDequeue(out NetTransport.NetEvent evt))
            {
                try
                {
                    DispatchNetEvent(evt);
                }
                catch (Exception ex)
                {
                    Log.Exception("runner dispatch " + evt.Kind, ex);
                }
            }
        }

        /// <summary>
        /// One transport event → codec → session layer. T2.2 keeps a minimal
        /// consumer: every inbound buffer goes through MsgCodec (drops are
        /// logged inside the codec), Ping is answered with Pong, HelloAck-accept
        /// adopts the host's session id, session teardown clears it. Everything
        /// decoded is also pushed to <see cref="MessageDecoded"/> for T2.3.
        /// </summary>
        private void DispatchNetEvent(NetTransport.NetEvent evt)
        {
            if (evt.Kind == NetTransport.NetEventKind.Message && evt.Data != null)
            {
                if (MsgCodec.TryDecode(evt.Data, _sessionId, out NetMessage msg))
                {
                    OnDecodedMessage(evt.PeerId, msg);
                }
            }
            else if (evt.Kind == NetTransport.NetEventKind.Disconnected)
            {
                // Peer is gone; a fresh connect starts a new handshake. Guest
                // side: the only peer is the host, so any disconnect ends the
                // session - clear the sid to unblock the codec. Host side:
                // SessionHost owns sid teardown (it clears once the GUEST peer
                // leaves, after emitting GuestLeft) - a stray rejected/dangling
                // peer disconnect must not wipe a live session id (T2.5).
                if (_sessionId != Guid.Empty
                    && Transport != null && Transport.CurrentRole == NetTransport.Role.Guest)
                {
                    Log.Info("net session cleared (disconnect peer=" + evt.PeerId + ")");
                    _sessionId = Guid.Empty;
                }
                if (Transport != null && Transport.PeerIds.Length == 0)
                {
                    _lastRttMs = -1; // no peers - the next pong repopulates it
                }
            }
#if X2COOP_PROBES
            DevOnNetEvent(evt);
#endif
            try
            {
                NetEventReceived?.Invoke(evt);
            }
            catch (Exception ex)
            {
                Log.Exception("runner NetEventReceived " + evt.Kind, ex);
            }
        }

        /// <summary>Minimal protocol-level handling for decoded messages.</summary>
        private void OnDecodedMessage(int peerId, NetMessage msg)
        {
            if (msg is PingMsg ping)
            {
                // Keepalive reply - echo the sender's ticks so it can measure RTT.
                byte[] pong = MsgCodec.Encode(new PongMsg { SessionId = _sessionId, Ticks = ping.Ticks });
                if (pong != null && Transport != null)
                {
                    Transport.Send(peerId, MsgCodec.ChannelFor(MsgType.Pong),
                        pong, 0, pong.Length, DeliveryMethod.ReliableOrdered);
                }
                return; // no rx log - the 2 s cadence would spam
            }
            if (msg is PongMsg pongMsg)
            {
                long rttMs = (DateTime.UtcNow.Ticks - pongMsg.Ticks) / TimeSpan.TicksPerMillisecond;
                _lastRttMs = rttMs; // T2.4 status line surfaces this
                Log.Info("net pong peer=" + peerId + " rtt=" + rttMs + "ms");
            }
            else if (msg is HelloAckMsg ack)
            {
                if (ack.Accepted && _sessionId == Guid.Empty)
                {
                    _sessionId = msg.SessionId; // adopt the host's assignment
                    Log.Info("net session assigned id=" + MsgCodec.ShortId(_sessionId));
                }
                else if (!ack.Accepted)
                {
                    Log.Info("net join rejected reason=" + ack.RejectReason);
                }
            }
            else if (msg is SessionNoticeMsg notice)
            {
                Log.Info("net notice peer=" + peerId + " kind=" + notice.Notice);
                if (notice.Notice == SessionNoticeKind.HostEnded)
                {
                    _sessionId = Guid.Empty;
                }
            }
            else
            {
                // Handshake/save/delta traffic - session layer (T2.3+) owns it.
                Log.Info("net rx peer=" + peerId + " type=" + msg.Type);
            }
            try
            {
                MessageDecoded?.Invoke(peerId, msg);
            }
            catch (Exception ex)
            {
                Log.Exception("runner MessageDecoded " + msg.Type, ex);
            }
        }

        /// <summary>Spec keepalive: Ping every 2 s on the control channel.</summary>
        private const int KeepAliveIntervalMs = 2000;
        private readonly Stopwatch _keepAliveWatch = Stopwatch.StartNew();
        private Guid _sessionId = Guid.Empty;
        private long _lastRttMs = -1;
        /// <summary>TeardownNow ran - this component is dead, never hand it out again.</summary>
        private bool _tornDown;

        /// <summary>Latest Pong-measured round-trip ms (-1 until measured). T2.4 status line.</summary>
        public long LastRttMs => _lastRttMs;

        private void PumpKeepAlive()
        {
            if (_keepAliveWatch.ElapsedMilliseconds < KeepAliveIntervalMs) { return; }
            _keepAliveWatch.Restart();
            NetTransport t = Transport;
            if (t == null || !t.IsRunning) { return; }
            int[] peers = t.PeerIds;
            if (peers.Length == 0) { return; }
            byte[] ping = MsgCodec.Encode(new PingMsg
            {
                SessionId = _sessionId,
                Ticks = DateTime.UtcNow.Ticks,
            });
            if (ping == null) { return; }
            foreach (int id in peers)
            {
                t.Send(id, MsgCodec.ChannelFor(MsgType.Ping),
                    ping, 0, ping.Length, DeliveryMethod.ReliableOrdered);
            }
        }

        /// <summary>
        /// T4.2 scripted guest-interaction run marker
        /// (docs/guest-interaction-script.md). One hotkey toggles the run:
        /// first press logs "RUN BEGIN" + a baseline firewall summary, second
        /// press logs "RUN END" + the final summary - the line spec criterion
        /// 5 reads ("firewall summary: unwhitelisted=0" = no leaks). Counters
        /// are session-scoped (MirrorFirewall.Reset on session end), so the
        /// BEGIN line is only a baseline for the diff, never a reset.
        ///
        /// Key choice (decompile survey, xenonauts2-decomp\full): the game
        /// binds F1-F4 (time speeds), F5/F9 (quick save/load), F7 (FPSTest),
        /// F8 (CommonDebugSystem), F10 (GC DebugCheatPanel), F11 (BugReporter),
        /// Escape/Space/Return/Tab, arrows + WASDQEXFR CI, Alpha1-9, +/-,
        /// PageUp/PageDown and the mouse buttons. Our mod already owns F6
        /// (panel), F7/F8 (probes) and H/J/K/B (dev). F12 is the de-facto
        /// screenshot key (avoided since T2.4); letters fire while typing in
        /// text fields; Delete is hazardous near the save menu; Insert risks
        /// overtype toggles. KeyCode.Home has ZERO references in the whole
        /// decompile and types no text - safe to press mid-checklist.
        /// Not probe-gated: a log-only marker is harmless in Release.
        /// </summary>
        private const KeyCode RunMarkerKey = KeyCode.Home;
        private bool _runMarkerOn;

        private void PollRunMarker()
        {
            if (!Input.GetKeyDown(RunMarkerKey)) { return; }
            _runMarkerOn = !_runMarkerOn;
            string role = Transport != null ? Transport.CurrentRole.ToString() : "none";
            if (_runMarkerOn)
            {
                Log.Info("run: RUN BEGIN (key=" + RunMarkerKey + " role=" + role
                    + " mode=" + Mirror.MirrorState.Mode + ") - baseline "
                    + Mirror.MirrorFirewall.Summary());
            }
            else
            {
                Log.Info("run: RUN END (role=" + role + " mode=" + Mirror.MirrorState.Mode
                    + ") - " + Mirror.MirrorFirewall.Summary());
            }
        }

        private void LateUpdate()
        {
            FlushReplication();
#if X2COOP_PROBES
            Probes.MutationDelayProbe.SampleLateUpdate();
#endif
        }

        /// <summary>
        /// T5.1/T5.2 seam: drain the frame's coalesced host-delta capture
        /// (T5.1 ReplicationSource), then T5.2 DeltaSender consumes
        /// FrameOps SYNCHRONOUSLY here - CapturedOp.Next is a live component
        /// reference that dies at the next FlushFrame, so serialize/snapshot
        /// can never be deferred out of this seam.
        /// </summary>
        private void FlushReplication()
        {
            try { Replication.ReplicationSource.FlushFrame(); }
            catch (Exception ex) { Log.Exception("runner capture-flush", ex); }
            try { Replication.DeltaSender.Consume(this); }
            catch (Exception ex) { Log.Exception("runner delta-send", ex); }
            // T5.5: post-Consume settle pump - the frame's ops are all folded
            // by now, so a quiet queue + stable seq means "hashable state".
            try { Replication.WorldHash.LateUpdate(this); }
            catch (Exception ex) { Log.Exception("runner hash-lateupdate", ex); }
        }

        private void OnGUI()
        {
            DrawCoopPanel();
        }

        /// <summary>T2.4: co-op IMGUI panel (status strip + banner + F6 window).</summary>
        private void DrawCoopPanel()
        {
            try
            {
                UI.CoopPanel.Draw(this);
            }
            catch (Exception ex)
            {
                Log.Exception("runner ui-draw", ex);
            }
        }

#if X2COOP_PROBES
        // ------------------------------------------------------------------
        // T2.1/T2.2 dev triggers (probe/Debug builds only — T2.4 replaces them
        // with the co-op panel):
        //   command line  -x2coop-host | -x2coop-join   (two-instance.ps1
        //                 -CoopAutoStart passes these; connect is fixed to
        //                 127.0.0.1:DefaultPort)
        //   hotkeys       H = host listen, J = join 127.0.0.1, K = stop,
        //                 B = codec self-test (logs "codec ok n/n"),
        //                 P = HashNow (T5.5 world-hash request/local hash)
        //   ping          on Connected each side sends a codec Ping — the peer
        //                 answers Pong; a two-way codec+transport proof.
        // ------------------------------------------------------------------
        private bool _devArgsDone;
        private bool _autoLoadDone;

        private void DevPoll()
        {
            if (!_devArgsDone)
            {
                _devArgsDone = true;
                DevCommandLine();
            }
            if (!_autoLoadDone)
            {
                TryAutoLoadSave();
            }
            NetTransport t = Transport;
            if (t == null)
            {
                return;
            }
            if (Input.GetKeyDown(KeyCode.H))
            {
                Log.Info("dev: H -> host :" + Protocol.DefaultPort);
                t.StartHost(Protocol.DefaultPort);
            }
            else if (Input.GetKeyDown(KeyCode.J))
            {
                Log.Info("dev: J -> join 127.0.0.1:" + Protocol.DefaultPort);
                t.Connect("127.0.0.1", Protocol.DefaultPort);
            }
            else if (Input.GetKeyDown(KeyCode.K))
            {
                // T2.5: host-role K is the graceful session end (HostEnded
                // first, transport down ~300 ms later via SessionHost.Tick);
                // any other role just drops the socket immediately.
                Log.Info("dev: K -> stop (graceful if hosting)");
                Session.SessionHost.RequestStop();
            }
            else if (Input.GetKeyDown(KeyCode.B))
            {
                Log.Info("dev: B -> codec self-test");
                CodecSelfTest.Run();
            }
            else if (Input.GetKeyDown(KeyCode.P))
            {
                // T5.5 HashNow: host -> pause (x2coop.hashPause) + settle +
                // hash + HashReq; guest -> local hash at the applied seq.
                Log.Info("dev: P -> HashNow");
                Replication.WorldHash.RequestNow(this, "hotkey-P");
            }
        }

        /// <summary>
        /// T5.5 scripted-verify knob: -x2coop.autoLoadSave=&lt;name-or-path&gt;
        /// issues SaveGameSystem.LoadGame once the game reaches the main
        /// menu - a scripted two-instance host needs a live Strategy world
        /// with zero input driving (CONTINUE click). A bare name resolves as
        /// Constants.Folders.Save() + name; a rooted path is used verbatim
        /// (FileDescriptor accepts any resolvable path - R8 recon). The
        /// guest never needs this knob: it loads the streamed baseline.
        /// One-shot: a failed/absent target logs once and never retries.
        /// </summary>
        private void TryAutoLoadSave()
        {
            try
            {
                string sv = DebugSettings.Get("autoLoadSave");
                if (string.IsNullOrEmpty(sv)) { _autoLoadDone = true; return; }
                var main = Xenonauts.XenonautsMain.Instance;
                var sm = main != null ? main.ScreenManager : null;
                if (sm == null) { return; } // boot not finished - keep polling
                if (Session.ScreenGuard.IsOnStrategy)
                {
                    _autoLoadDone = true; // a world is already live - never reload over it
                    return;
                }
                if (sm.CurrentScreen != Xenonauts.GameScreens.MainMenu
                    || sm.IsTransitioning)
                {
                    return; // loading screen / battle / mid-transition - wait
                }
                _autoLoadDone = true;
                string path = System.IO.Path.IsPathRooted(sv)
                    ? sv
                    : System.IO.Path.Combine(Common.Constants.Folders.Save(), sv);
                var fd = new Common.Content.Descriptors.FileDescriptor(
                    path, typeof(Common.Content.DataStructures.SaveGame));
                bool exists = false;
                try { exists = Common.Content.AssetReference.ContentManager.Exists(fd); }
                catch (Exception ex) { Log.Exception("dev autoLoadSave exists", ex); }
                if (!exists)
                {
                    Log.Warn("dev: autoLoadSave target missing: " + path);
                    return;
                }
                Log.Info("dev: autoLoadSave -> LoadGame " + path);
                Code.Screens.Common.Systems.SaveGameSystem.LoadGame(fd, false);
            }
            catch (Exception ex)
            {
                _autoLoadDone = true; // fail once - never loop a bad load
                Log.Exception("dev autoLoadSave", ex);
            }
        }

        private void DevCommandLine()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                for (int i = 0; i < args.Length; i++)
                {
                    if (args[i] == "-x2coop-host")
                    {
                        Log.Info("dev: -x2coop-host -> listen :" + Protocol.DefaultPort);
                        Transport?.StartHost(Protocol.DefaultPort);
                    }
                    else if (args[i] == "-x2coop-join")
                    {
                        Log.Info("dev: -x2coop-join -> 127.0.0.1:" + Protocol.DefaultPort);
                        Transport?.Connect("127.0.0.1", Protocol.DefaultPort);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("dev commandline", ex);
            }
        }

        private void DevOnNetEvent(NetTransport.NetEvent evt)
        {
            if (evt.Kind == NetTransport.NetEventKind.Connected)
            {
                // T2.2: the raw "x2coop-devping" string is replaced by a real
                // codec Ping (the keepalive PumpKeepAlive then sustains 2 s).
                byte[] ping = MsgCodec.Encode(new PingMsg
                {
                    SessionId = _sessionId,
                    Ticks = DateTime.UtcNow.Ticks,
                });
                if (ping != null && Transport != null
                    && Transport.Send(evt.PeerId, MsgCodec.ChannelFor(MsgType.Ping),
                        ping, 0, ping.Length, DeliveryMethod.ReliableOrdered))
                {
                    Log.Info("dev: sent ping peer=" + evt.PeerId + " len=" + ping.Length);
                }
            }
        }
#endif
    }
}
