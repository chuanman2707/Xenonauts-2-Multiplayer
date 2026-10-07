using System;
using UnityEngine;
using X2Coop.Net;
using X2Coop.Runtime;
using X2Coop.Session;

namespace X2Coop.UI
{
    /// <summary>
    /// T2.4 co-op IMGUI panel. Drawn from Runner.OnGUI via DrawCoopPanel;
    /// <see cref="PollInput"/> runs in Runner.Update (GetKeyDown is per-frame
    /// state - unreliable inside OnGUI's multi-pass event pump).
    ///
    /// Layout (all plain IMGUI, default skin, small + unobtrusive):
    ///   - status strip: one line, always on screen once attached:
    ///     "X2-Coop &lt;role/state&gt; sid=&lt;short&gt; rtt=&lt;n&gt;ms | &lt;last notice&gt;".
    ///     This is the screenshot-safe status surface (no IP ever on it).
    ///   - banner: centered under the strip, guest-only:
    ///     "Spectating host" / "Host busy" / "Resyncing..." /
    ///     "Host ended session" (CoopPanelState).
    ///   - panel window: F6 toggles. Host button, IP field + Join, Stop.
    ///
    /// Hotkey F6 - decompile grep of KeyCode.* (xenonauts2-decomp\full):
    /// the game binds F1-F5 (screens + quick_save), F7 (FPSTest), F8
    /// (CommonDebugSystem), F9 (quick_load), F10 (DebugCheatPanel), F11
    /// (BugReporter). F6/F12-F15 appear only in KeyBinding.GET_CODE_STRING_
    /// OVERRIDE (a display-name map for the rebind UI, not real bindings).
    /// F12 is the de-facto screenshot key (Steam overlay etc.) - avoided.
    /// Existing mod probes use H/J/K/B (+ F7/F8 probe hotkeys).
    ///
    /// IP privacy (brief rule: never in screenshots, never in logs):
    ///   - the text field exists ONLY inside the toggleable panel;
    ///   - it shows the typed address ONLY while focused (editing); unfocused
    ///     it renders a fixed "********" mask - a screenshot or stream of an
    ///     idle panel can never leak the address (or its length);
    ///   - it is NEVER logged: click handlers log the port + address LENGTH
    ///     only; NetTransport itself logs peer ids, not endpoints.
    ///   - the status strip/banner contain no address by construction.
    ///
    /// Future-task seams:
    ///   - <see cref="NoteResyncSent"/>: T5.x calls when the guest sends a
    ///     ResyncReq - the "Resyncing..." banner then rides until
    ///     SessionNotice.Resynced.
    ///   - T3.1 ScreenGuard sends SessionNotice HostLeftStrategy/HostReturned
    ///     (via SessionHost.NotifyStrategyPresence); the "Host busy" banner is
    ///     wired to those kinds below.
    ///   - T3.3 may tighten "Spectating host" from in-session to
    ///     MirrorState.Active once the guest mirror actually loads.
    /// </summary>
    public static class CoopPanel
    {
        /// <summary>Panel toggle hotkey (see class doc for the binding survey).</summary>
        public const KeyCode ToggleKey = KeyCode.F6;

        /// <summary>IMGUI window id - high arbitrary value, clear of game ids.</summary>
        private const int WindowId = 726300;
        private const string IpControlName = "x2coop-ip";

        private static Runner _runner;
        private static readonly CoopPanelState State = new CoopPanelState();
        private static bool _visible;
        private static string _ipText = string.Empty;
        private static Rect _winRect = new Rect(8, 34, 480, 160);
        private static GUIStyle _bannerStyle;
        private static GUIStyle _stripStyle;
        private static GUIStyle _hintStyle;

        /// <summary>True while the control panel window is open (F6).</summary>
        public static bool Visible => _visible;

        /// <summary>Current guest banner (test/DebugSettings surface).</summary>
        public static CoopBanner Banner => State.CurrentBanner;

        /// <summary>Subscribe to the runner's decoded/event streams (idempotent).</summary>
        public static void Attach(Runner runner)
        {
            if (runner == null || ReferenceEquals(_runner, runner)) { return; }
            Detach();
            _runner = runner;
            _runner.MessageDecoded += OnMessage;
            _runner.NetEventReceived += OnNetEvent;
            string pv = DebugSettings.Get("panel");
            _visible = pv != null && (pv.Equals("open", StringComparison.OrdinalIgnoreCase)
                || pv.Equals("true", StringComparison.OrdinalIgnoreCase) || pv == "1");
            Log.Info("ui: panel attached - F6 toggles" + (_visible ? " (starts open)" : string.Empty));
        }

        /// <summary>
        /// Unsubscribe + wipe all panel statics (T2.6 teardown). Full
        /// <see cref="CoopPanelState.Reset"/> rather than NoteDisconnected:
        /// the HostEnded latch that survives a normal disconnect must NOT
        /// survive a pack disable - the re-enabled panel starts clean. The
        /// typed IP text is dropped too (transient per-attach UI state,
        /// never persisted). _winRect keeps its drag position.
        /// </summary>
        public static void Detach()
        {
            Runner r = _runner;
            if (r != null)
            {
                try { r.MessageDecoded -= OnMessage; } catch { }
                try { r.NetEventReceived -= OnNetEvent; } catch { }
            }
            _runner = null;
            _visible = false;
            _ipText = string.Empty;
            _bannerStyle = null;   // recreated lazily by EnsureStyles
            _stripStyle = null;
            _hintStyle = null;
            State.Reset();
            Log.Info("ui: panel detached");
        }

        /// <summary>Runner.Update hook: poll the toggle hotkey on the main thread.</summary>
        public static void PollInput()
        {
            if (Input.GetKeyDown(ToggleKey))
            {
                Toggle();
            }
        }

        /// <summary>Toggle the control panel (banner + strip are unaffected).</summary>
        public static void Toggle()
        {
            _visible = !_visible;
            Log.Info("ui: panel " + (_visible ? "shown" : "hidden") + " (" + ToggleKey + ")");
        }

        /// <summary>T3.6 seam: call after the guest sends a ResyncReq (button, HostReturned relay, timer, or a stream-failure retry).</summary>
        public static void NoteResyncSent()
        {
            State.NoteResyncReqSent();
        }

        /// <summary>T3.3 seam: the mirror load activated - clear the Resyncing banner.</summary>
        public static void NoteSaveLoaded()
        {
            State.NoteSaveLoaded();
        }

        /// <summary>T3.6 seam: the mirror world died mid-session (SaveReceiver detach) - strip notice.</summary>
        public static void NoteMirrorLost()
        {
            State.NoteMirrorLost();
        }

        /// <summary>Runner.OnGUI hook: strip + banner + (toggled) window.</summary>
        public static void Draw(Runner runner)
        {
            if (runner == null) { return; }
            NetTransport t = runner.Transport;
            // Spectating derives from live session state, not a latched flag -
            // a silent session loss can never leave the banner stuck on.
            State.InSession = t != null
                && t.CurrentRole == NetTransport.Role.Guest
                && SessionClient.Status == "in-session";
            State.MirrorDetached = Mirror.MirrorState.Mode == Mirror.MirrorMode.Detached;

            EnsureStyles();
            string status = BuildStatus(runner, t);
            DrawStrip(status);
            DrawBanner(t);
            if (_visible)
            {
                _winRect = GUI.Window(WindowId, _winRect, DrawWindow, "X2 Co-op");
            }
        }

        // ------------------------------------------------------------------
        // drawing
        // ------------------------------------------------------------------

        private static void EnsureStyles()
        {
            if (_stripStyle == null)
            {
                _stripStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 12,
                };
            }
            if (_bannerStyle == null)
            {
                _bannerStyle = new GUIStyle(GUI.skin.box)
                {
                    fontSize = 16,
                    fontStyle = FontStyle.Bold,
                    alignment = TextAnchor.MiddleCenter,
                };
            }
            if (_hintStyle == null)
            {
                _hintStyle = new GUIStyle(GUI.skin.label)
                {
                    fontSize = 10,
                };
            }
        }

        private static void DrawStrip(string status)
        {
            GUI.Box(new Rect(6, 6, 560, 24), GUIContent.none);
            GUI.Label(new Rect(12, 8, 552, 20), "X2-Coop  " + status + "   [F6]", _stripStyle);
        }

        private static void DrawBanner(NetTransport t)
        {
            // "The host never banners" (CoopPanelState doc) - enforced here, not
            // just by convention: a stray inbound notice can't raise one.
            if (t == null || t.CurrentRole != NetTransport.Role.Guest) { return; }
            CoopBanner banner = State.CurrentBanner;
            if (banner == CoopBanner.None) { return; }
            if (banner == CoopBanner.HostBusy)
            {
                _bannerStyle.normal.textColor = new Color(1f, 0.82f, 0.3f);
            }
            else if (banner == CoopBanner.Resyncing)
            {
                _bannerStyle.normal.textColor = new Color(0.55f, 0.85f, 1f);
            }
            else if (banner == CoopBanner.HostEnded)
            {
                _bannerStyle.normal.textColor = new Color(1f, 0.55f, 0.45f);
            }
            else if (banner == CoopBanner.MirrorLost)
            {
                _bannerStyle.normal.textColor = new Color(1f, 0.65f, 0.35f);
            }
            else
            {
                _bannerStyle.normal.textColor = Color.white;
            }
            float w = 300;
            GUI.Box(new Rect((Screen.width - w) * 0.5f, 34, w, 30),
                CoopPanelState.TextFor(banner), _bannerStyle);
        }

        private static void DrawWindow(int id)
        {
            Runner runner = _runner;
            NetTransport t = runner != null ? runner.Transport : null;
            GUILayout.Label(BuildStatus(runner, t));

            GUILayout.BeginHorizontal();
            bool canHost = t != null && t.CurrentRole == NetTransport.Role.None;
            GUI.enabled = canHost;
            if (GUILayout.Button("Host", GUILayout.Width(90)))
            {
                OnHostClicked(t);
            }
            GUI.enabled = t != null && t.IsRunning;
            if (GUILayout.Button("Stop", GUILayout.Width(90)))
            {
                OnStopClicked(t);
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("IP:", GUILayout.Width(24));
            DrawIpField();
            bool guestConnected = t != null
                && t.CurrentRole == NetTransport.Role.Guest
                && t.ServerPeerId >= 0 && t.IsConnected(t.ServerPeerId);
            string joinLabel = guestConnected ? "Retry Hello" : "Join";
            GUI.enabled = t != null && t.CurrentRole != NetTransport.Role.Host;
            if (GUILayout.Button(joinLabel, GUILayout.Width(100)))
            {
                OnJoinClicked(t);
            }
            GUI.enabled = true;
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            // T3.6: guest-only manual resync - re-requests the host save stream
            // (SessionClient.RequestResync -> ResyncReq -> re-stream ->
            // SessionNotice{Resynced}). Enabled while in-session in ANY mirror
            // mode: it is also the recovery path out of Detached.
            // T3.6-r1 (review I1) UI layer of the single-flight guard: greyed
            // while a resync is latched in-flight (request outstanding or a
            // stream receiving/activating) - click-spam can't stack streams.
            // SessionClient.RequestResync carries the logic-level guard under
            // this, so a stale banner can never wedge the button either.
            {
                Mirror.MirrorMode m = Mirror.MirrorState.Mode;
                bool resyncBusy = State.Resyncing
                    && (SessionClient.ResyncRequestInFlight
                        || m == Mirror.MirrorMode.Receiving
                        || m == Mirror.MirrorMode.PendingActivate);
                GUI.enabled = guestConnected && SessionClient.Status == "in-session"
                    && !resyncBusy;
            }
            if (GUILayout.Button("Resync", GUILayout.Width(100)))
            {
                OnResyncClicked();
            }
            GUI.enabled = true;
            GUILayout.Label("re-request the host save (guest)", _hintStyle);
            GUILayout.EndHorizontal();

            GUILayout.Label("F6 toggles - IP masked when idle, never logged", _hintStyle);

            // Only the title strip drags - a whole-window DragWindow would
            // swallow clicks meant for the text field/buttons.
            GUI.DragWindow(new Rect(0, 0, _winRect.width, 18));
        }

        /// <summary>
        /// IP field: editable while focused; a fixed mask otherwise (length is
        /// hidden too - screenshots of an idle panel leak nothing).
        /// </summary>
        private static void DrawIpField()
        {
            GUI.SetNextControlName(IpControlName);
            bool editing = GUI.GetNameOfFocusedControl() == IpControlName;
            if (editing)
            {
                _ipText = GUILayout.TextField(_ipText ?? string.Empty, 64, GUILayout.Width(180));
            }
            else
            {
                string masked = string.IsNullOrEmpty(_ipText) ? string.Empty : "********";
                GUILayout.TextField(masked, 64, GUILayout.Width(180)); // return value discarded
            }
        }

        // ------------------------------------------------------------------
        // status text
        // ------------------------------------------------------------------

        private static string BuildStatus(Runner runner, NetTransport t)
        {
            if (t == null) { return "transport offline"; }
            string s;
            switch (t.CurrentRole)
            {
                case NetTransport.Role.Host:
                    s = "hosting :" + t.LocalPort;
                    if (runner != null && runner.SessionId != Guid.Empty)
                    {
                        s += " sid=" + MsgCodec.ShortId(runner.SessionId);
                    }
                    s += SessionHost.HasGuest
                        ? " guest=peer" + SessionHost.GuestPeerId
                        : " (no guest)";
                    if (Mirror.MirrorState.HostJoinFreeze) { s += " join-freeze"; }
                    break;
                case NetTransport.Role.Guest:
                    s = "guest " + SessionClient.Status;
                    if (runner != null && runner.SessionId != Guid.Empty)
                    {
                        s += " sid=" + MsgCodec.ShortId(runner.SessionId);
                    }
                    // T3.3/T3.6: surface the mirror lifecycle on the strip.
                    if (Mirror.MirrorState.Mode == Mirror.MirrorMode.Active) { s += " mirror"; }
                    else if (Mirror.MirrorState.Mode == Mirror.MirrorMode.PendingActivate
                        || Mirror.MirrorState.Mode == Mirror.MirrorMode.Receiving) { s += " syncing"; }
                    else if (Mirror.MirrorState.Mode == Mirror.MirrorMode.Detached) { s += " detached"; }
                    if (SessionClient.Status == "rejected" && !string.IsNullOrEmpty(State.LastReject))
                    {
                        s += ": " + State.LastReject; // host-authored text - no IP
                    }
                    break;
                default:
                    s = "idle";
                    break;
            }
            if (runner != null && runner.LastRttMs >= 0 && t.PeerCount > 0)
            {
                s += "  rtt=" + runner.LastRttMs + "ms";
            }
            if (!string.IsNullOrEmpty(State.LastNotice))
            {
                s += "  | " + State.LastNotice;
            }
            return s;
        }

        // ------------------------------------------------------------------
        // button handlers - never log the IP field's content
        // ------------------------------------------------------------------

        private static void OnHostClicked(NetTransport t)
        {
            if (t == null) { return; }
            Log.Info("ui: host -> :" + Protocol.DefaultPort);
            t.StartHost(Protocol.DefaultPort);
        }

        private static void OnStopClicked(NetTransport t)
        {
            if (t == null) { return; }
            Log.Info("ui: stop");
            if (t.CurrentRole == NetTransport.Role.Host)
            {
                // T2.5: HostEnded to the guest FIRST, socket down ~300 ms later
                // (SessionHost.Tick) - a same-frame Stop() would drop the notice.
                SessionHost.RequestStop();
                State.NoteHostEnding();
                return;
            }
            t.Stop();
            State.NoteDisconnected("stopped"); // Stop() drops queued events silently
        }

        /// <summary>T3.6: the Resync button - SessionClient owns the send (gated on in-session).</summary>
        private static void OnResyncClicked()
        {
            Log.Info("ui: resync clicked");
            SessionClient.RequestResync(ResyncReason.ManualRequest, "manual resync button");
        }

        private static void OnJoinClicked(NetTransport t)
        {
            if (t == null) { return; }
            // Already-connected guest (e.g. after a reject): re-drive the
            // handshake instead of dialing again.
            if (t.CurrentRole == NetTransport.Role.Guest
                && t.ServerPeerId >= 0 && t.IsConnected(t.ServerPeerId))
            {
                Log.Info("ui: retry hello");
                State.NoteHelloSent();
                SessionClient.SendHello();
                return;
            }
            if (t.CurrentRole == NetTransport.Role.Host) { return; }

            string addr = (_ipText ?? string.Empty).Trim();
            int port = Protocol.DefaultPort;
            int colon = addr.IndexOf(':');
            if (colon > 0 && addr.IndexOf(':', colon + 1) < 0)
            {
                int p;
                if (int.TryParse(addr.Substring(colon + 1), out p) && p > 0 && p <= 65535)
                {
                    port = p;
                    addr = addr.Substring(0, colon);
                }
            }
            if (addr.Length == 0) { addr = "127.0.0.1"; }
            Log.Info("ui: join -> port=" + port + " addrlen=" + addr.Length);
            t.Connect(addr, port);
        }

        // ------------------------------------------------------------------
        // runner event feeds -> state machine
        // ------------------------------------------------------------------

        private static void OnMessage(int peerId, NetMessage msg)
        {
            try
            {
                HelloAckMsg ack = msg as HelloAckMsg;
                if (ack != null)
                {
                    State.NoteHelloAck(ack.Accepted, ack.RejectReason, ack.Detail);
                    return;
                }
                SessionNoticeMsg notice = msg as SessionNoticeMsg;
                if (notice != null)
                {
                    State.NoteSessionNotice(notice.Notice);
                    return;
                }
                if (msg is SaveOfferMsg)
                {
                    // H->G only; covers both first sync and resync restreams.
                    State.NoteSaveOfferReceived();
                }
            }
            catch (Exception ex)
            {
                Log.Exception("ui msg", ex);
            }
        }

        private static void OnNetEvent(NetTransport.NetEvent evt)
        {
            try
            {
                if (evt.Kind == NetTransport.NetEventKind.Connected)
                {
                    State.NoteConnected();
                }
                else if (evt.Kind == NetTransport.NetEventKind.Disconnected)
                {
                    State.NoteDisconnected(evt.Reason);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("ui net-event", ex);
            }
        }
    }
}
