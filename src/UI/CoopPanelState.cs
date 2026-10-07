using X2Coop.Net;

namespace X2Coop.UI
{
    /// <summary>Which guest banner is on screen right now.</summary>
    public enum CoopBanner : byte
    {
        None = 0,
        Spectating = 1,   // "Spectating host"     - guest in-session, nothing blocking
        HostBusy = 2,     // "Host busy"           - host left Strategy (battle etc.)
        Resyncing = 3,    // "Resyncing..."        - resync in flight
        HostEnded = 4,    // "Host ended session"  - HostEnded notice seen; terminal until rejoin
        MirrorLost = 5,   // "Mirror lost"         - T3.6: mirror world died mid-session (Mode=Detached)
    }

    /// <summary>
    /// Banner/status state machine for <see cref="CoopPanel"/> - deliberately
    /// free of UnityEngine/Runner references so the test host can link it
    /// (same trick as SessionParity). The Unity-side panel feeds it decoded
    /// session traffic and reads <see cref="CurrentBanner"/> each OnGUI.
    ///
    /// Banner sources (guest side; the host never banners):
    ///   "Spectating host"  - set per frame from session state by the panel
    ///                        (InSession property; S1 guests are spectators by
    ///                        definition. T3.3 may tighten this to
    ///                        MirrorState.Active once mirror-mode exists).
    ///   "Host busy"        - SessionNotice HostLeftStrategy -> HostReturned;
    ///                        T3.1 ScreenGuard/SessionHost is the sender.
    ///   "Resyncing..."     - ResyncReq sent (NoteResyncReqSent seam, T5.x
    ///                        caller) OR an inbound SaveOffer while already
    ///                        in-session (host restreaming the world) ->
    ///                        cleared by SessionNotice Resynced.
    ///   "Host ended session" - SessionNotice HostEnded (T2.5). Terminal: it
    ///                        SURVIVES the transport disconnect that follows
    ///                        the notice, and clears only on a fresh dial or
    ///                        a re-sent Hello.
    ///   "Mirror lost"      - T3.6: MirrorState.Mode == Detached (the mirror
    ///                        world died mid-session - quit-to-menu or a
    ///                        local transition). Fed per frame like InSession;
    ///                        the Resync button is the recovery.
    ///
    /// Priority: HostEnded > Resyncing > MirrorLost > HostBusy > Spectating
    /// > None. Resyncing outranks MirrorLost on purpose: a detached guest
    /// who pressed Resync shows the recovery in flight, not the loss.
    ///
    /// LastReject carries the HelloAck Detail string verbatim - it is
    /// host-authored human-readable text ("mod version 0.0.0 != 0.1.0"), safe
    /// to draw and to log: it can never contain the IP (the panel never puts
    /// it there) or the player name (the host never builds it from Hello).
    /// </summary>
    public sealed class CoopPanelState
    {
        /// <summary>
        /// Written by the panel every frame: guest role + in-session. The
        /// spectate banner derives from it (not latched) so a silent session
        /// loss can never leave the banner stuck on.
        /// </summary>
        public bool InSession { get; set; }

        /// <summary>
        /// Written by the panel every frame (T3.6): MirrorState.Mode ==
        /// Detached - the mirror world died mid-session. Per-frame like
        /// InSession so a fresh save stream (Mode -&gt; Receiving) clears it
        /// without an extra event.
        /// </summary>
        public bool MirrorDetached { get; set; }

        /// <summary>HostLeftStrategy received, HostReturned not yet seen.</summary>
        public bool HostBusy { get; private set; }

        /// <summary>ResyncReq sent / resync SaveOffer seen, Resynced not yet seen.</summary>
        public bool Resyncing { get; private set; }

        /// <summary>
        /// HostEnded notice received. Unlike the other latches this is NOT
        /// cleared by NoteDisconnected - the transport teardown arriving right
        /// behind the notice must not erase the reason the session ended.
        /// </summary>
        public bool HostEndedSession { get; private set; }

        /// <summary>Last HelloAck reject detail (null when none/accepted). Safe to draw.</summary>
        public string LastReject { get; private set; }

        /// <summary>Last session/transport event as short safe text (enum names only).</summary>
        public string LastNotice { get; private set; }

        /// <summary>HelloAck received (guest). Accept clears a stored reject.</summary>
        public void NoteHelloAck(bool accepted, HelloRejectReason reason, string detail)
        {
            if (accepted)
            {
                LastReject = null;
                HostEndedSession = false;
                LastNotice = "join accepted";
                return;
            }
            LastReject = !string.IsNullOrEmpty(detail) ? detail : reason.ToString();
            LastNotice = "join rejected: " + reason;
        }

        /// <summary>SessionNotice received (either role; banners only matter guest-side).</summary>
        public void NoteSessionNotice(SessionNoticeKind kind)
        {
            switch (kind)
            {
                case SessionNoticeKind.HostLeftStrategy:
                    HostBusy = true;
                    LastNotice = "host left strategy";
                    break;
                case SessionNoticeKind.HostReturned:
                    HostBusy = false;
                    LastNotice = "host returned";
                    break;
                case SessionNoticeKind.Resynced:
                    Resyncing = false;
                    LastNotice = "resynced";
                    break;
                case SessionNoticeKind.HostEnded:
                    HostBusy = false;
                    Resyncing = false;
                    HostEndedSession = true;
                    LastNotice = "host ended session";
                    break;
                case SessionNoticeKind.GuestJoined:
                    LastNotice = "guest joined";
                    break;
                case SessionNoticeKind.GuestLeft:
                    LastNotice = "guest left";
                    break;
                case SessionNoticeKind.GuestMirrorLost:
                    LastNotice = "guest mirror lost"; // host-side strip text
                    break;
                default:
                    LastNotice = "notice " + kind;
                    break;
            }
        }

        /// <summary>Transport Connected event: fresh dial - drop a stale reject/end.</summary>
        public void NoteConnected()
        {
            LastReject = null;
            HostEndedSession = false;
            LastNotice = "connected";
        }

        /// <summary>
        /// Transport Disconnected (or the panel's own Stop click): the session
        /// is over - every banner condition clears, keep only the notice. The
        /// HostEnded latch survives on purpose: the teardown lands right behind
        /// the notice and must not erase "host ended session".
        /// </summary>
        public void NoteDisconnected(string reason)
        {
            HostBusy = false;
            Resyncing = false;
            InSession = false;
            MirrorDetached = false;
            LastReject = null;
            LastNotice = HostEndedSession
                ? "host ended session"
                : "disconnected: " + (reason ?? "?");
        }

        /// <summary>Host-side strip text while a graceful stop is pending.</summary>
        public void NoteHostEnding()
        {
            LastNotice = "host ended session";
        }

        /// <summary>Guest (re)sent a Hello: a fresh attempt drops a stale reject/end.</summary>
        public void NoteHelloSent()
        {
            LastReject = null;
            HostEndedSession = false;
            LastNotice = "joining...";
        }

        /// <summary>
        /// Full wipe for pack teardown (T2.6, CoopPanel.Detach): every latch,
        /// flag and text back to initial - including the HostEnded latch that
        /// deliberately survives <see cref="NoteDisconnected"/>. A re-enabled
        /// pack must not inherit the previous session's banner state.
        /// </summary>
        public void Reset()
        {
            InSession = false;
            MirrorDetached = false;
            HostBusy = false;
            Resyncing = false;
            HostEndedSession = false;
            LastReject = null;
            LastNotice = null;
        }

        /// <summary>T5.x seam: the guest just sent a ResyncReq.</summary>
        public void NoteResyncReqSent()
        {
            Resyncing = true;
            LastNotice = "resync requested";
        }

        /// <summary>
        /// Guest received a SaveOffer - the host is (re)streaming the world.
        /// During the FIRST sync this is also the correct banner (the guest is
        /// syncing); T3.2/T3.3 own the actual transfer.
        /// </summary>
        public void NoteSaveOfferReceived()
        {
            Resyncing = true;
            LastNotice = "receiving save";
        }

        /// <summary>
        /// T3.3: the guest's mirror load finished and activated (GuestReady
        /// sent) - the "Resyncing..." banner drops back to plain spectating.
        /// </summary>
        public void NoteSaveLoaded()
        {
            Resyncing = false;
            LastNotice = "mirror loaded";
        }

        /// <summary>
        /// T3.6: the mirror world died mid-session (SaveReceiver detach) -
        /// the banner itself is the per-frame MirrorDetached feed; this just
        /// leaves the strip notice.
        /// </summary>
        public void NoteMirrorLost()
        {
            LastNotice = "mirror lost - Resync to rejoin";
        }

        /// <summary>Current banner after priority resolution.</summary>
        public CoopBanner CurrentBanner
        {
            get
            {
                if (HostEndedSession) { return CoopBanner.HostEnded; }
                if (Resyncing) { return CoopBanner.Resyncing; }
                if (MirrorDetached) { return CoopBanner.MirrorLost; }
                if (HostBusy) { return CoopBanner.HostBusy; }
                if (InSession) { return CoopBanner.Spectating; }
                return CoopBanner.None;
            }
        }

        /// <summary>Display text for a banner (empty for None).</summary>
        public static string TextFor(CoopBanner banner)
        {
            switch (banner)
            {
                case CoopBanner.Spectating: return "Spectating host";
                case CoopBanner.HostBusy: return "Host busy";
                case CoopBanner.Resyncing: return "Resyncing...";
                case CoopBanner.HostEnded: return "Host ended session";
                case CoopBanner.MirrorLost: return "Mirror lost - Resync to rejoin";
                default: return string.Empty;
            }
        }
    }
}
