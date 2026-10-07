using Xunit;
using X2Coop.Net;
using X2Coop.UI;

namespace X2Coop.Tests
{
    /// <summary>
    /// T2.4 panel state machine: which banner shows for which session traffic
    /// ("Spectating host" / "Host busy" / "Resyncing..."), reject-detail text,
    /// and the T5.x resync seam. CoopPanelState is game-free (no UnityEngine),
    /// so it links and runs here.
    /// </summary>
    public class PanelStateTests
    {
        [Fact]
        public void FreshState_NoBanner()
        {
            var s = new CoopPanelState();
            Assert.Equal(CoopBanner.None, s.CurrentBanner);
            Assert.Equal(string.Empty, CoopPanelState.TextFor(CoopBanner.None));
        }

        [Fact]
        public void InSession_ShowsSpectating()
        {
            var s = new CoopPanelState();
            s.InSession = true;
            Assert.Equal(CoopBanner.Spectating, s.CurrentBanner);
            Assert.Equal("Spectating host", CoopPanelState.TextFor(s.CurrentBanner));
        }

        [Fact]
        public void HostLeftStrategy_ShowsHostBusy_UntilReturned()
        {
            var s = new CoopPanelState();
            s.InSession = true;
            s.NoteSessionNotice(SessionNoticeKind.HostLeftStrategy);
            Assert.True(s.HostBusy);
            Assert.Equal(CoopBanner.HostBusy, s.CurrentBanner);
            Assert.Equal("Host busy", CoopPanelState.TextFor(s.CurrentBanner));
            s.NoteSessionNotice(SessionNoticeKind.HostReturned);
            Assert.False(s.HostBusy);
            Assert.Equal(CoopBanner.Spectating, s.CurrentBanner);
        }

        [Fact]
        public void ResyncReq_ShowsResyncing_UntilResynced()
        {
            var s = new CoopPanelState();
            s.InSession = true;
            s.NoteResyncReqSent();
            Assert.True(s.Resyncing);
            Assert.Equal(CoopBanner.Resyncing, s.CurrentBanner);
            Assert.Equal("Resyncing...", CoopPanelState.TextFor(s.CurrentBanner));
            s.NoteSessionNotice(SessionNoticeKind.Resynced);
            Assert.False(s.Resyncing);
            Assert.Equal(CoopBanner.Spectating, s.CurrentBanner);
        }

        [Fact]
        public void SaveOffer_AlsoStartsResyncing()
        {
            var s = new CoopPanelState();
            s.InSession = true;
            s.NoteSaveOfferReceived();
            Assert.Equal(CoopBanner.Resyncing, s.CurrentBanner);
        }

        [Fact]
        public void Resyncing_BeatsHostBusy_BeatsSpectating()
        {
            var s = new CoopPanelState();
            s.InSession = true;
            s.NoteSessionNotice(SessionNoticeKind.HostLeftStrategy);
            s.NoteResyncReqSent();
            Assert.Equal(CoopBanner.Resyncing, s.CurrentBanner);
            s.NoteSessionNotice(SessionNoticeKind.Resynced);
            Assert.Equal(CoopBanner.HostBusy, s.CurrentBanner);
            s.NoteSessionNotice(SessionNoticeKind.HostReturned);
            Assert.Equal(CoopBanner.Spectating, s.CurrentBanner);
        }

        [Fact]
        public void Reject_StoresDetail_AcceptClears()
        {
            var s = new CoopPanelState();
            s.NoteHelloAck(false, HelloRejectReason.VersionMismatch, "mod version 0.0.0 != 0.1.0");
            Assert.Equal("mod version 0.0.0 != 0.1.0", s.LastReject);
            s.NoteHelloAck(true, HelloRejectReason.None, string.Empty);
            Assert.Null(s.LastReject);
            Assert.Equal("join accepted", s.LastNotice);
        }

        [Fact]
        public void Reject_FallsBackToReasonName_WhenDetailEmpty()
        {
            var s = new CoopPanelState();
            s.NoteHelloAck(false, HelloRejectReason.SessionFull, "");
            Assert.Equal("SessionFull", s.LastReject);
        }

        [Fact]
        public void HostEnded_ShowsBanner_SurvivesTeardownDisconnect()
        {
            var s = new CoopPanelState();
            s.InSession = true;
            s.NoteSessionNotice(SessionNoticeKind.HostLeftStrategy);
            s.NoteSessionNotice(SessionNoticeKind.HostEnded);
            Assert.False(s.HostBusy);
            Assert.True(s.HostEndedSession);
            Assert.Equal("host ended session", s.LastNotice);
            Assert.Equal("Host ended session", CoopPanelState.TextFor(s.CurrentBanner));
            // The transport teardown arrives right behind the notice (T2.5):
            // it must NOT erase the banner or the reason text.
            s.InSession = false;
            s.NoteDisconnected("RemoteConnectionClose");
            Assert.Equal(CoopBanner.HostEnded, s.CurrentBanner);
            Assert.Equal("host ended session", s.LastNotice);
        }

        [Fact]
        public void HostEnded_ClearsOnReconnect()
        {
            var s = new CoopPanelState();
            s.InSession = true;
            s.NoteSessionNotice(SessionNoticeKind.HostEnded);
            Assert.Equal(CoopBanner.HostEnded, s.CurrentBanner);
            // Fresh dial (or a re-sent Hello) resets the terminal latch.
            // InSession is panel-fed per Draw - a dialing guest is not in one.
            s.InSession = false;
            s.NoteConnected();
            Assert.False(s.HostEndedSession);
            Assert.Equal(CoopBanner.None, s.CurrentBanner);
            s.NoteSessionNotice(SessionNoticeKind.HostEnded);
            s.NoteHelloSent();
            Assert.False(s.HostEndedSession);
            Assert.Equal(CoopBanner.None, s.CurrentBanner);
        }

        [Fact]
        public void HostEnded_OutranksEverything()
        {
            var s = new CoopPanelState();
            s.InSession = true;
            s.NoteSessionNotice(SessionNoticeKind.HostLeftStrategy);
            s.NoteResyncReqSent();
            s.NoteSessionNotice(SessionNoticeKind.HostEnded);
            Assert.Equal(CoopBanner.HostEnded, s.CurrentBanner);
        }

        [Fact]
        public void Disconnect_ClearsEverything()
        {
            var s = new CoopPanelState();
            s.InSession = true;
            s.NoteSessionNotice(SessionNoticeKind.HostLeftStrategy);
            s.NoteResyncReqSent();
            s.NoteHelloAck(false, HelloRejectReason.SessionFull, "full");
            s.NoteDisconnected("RemoteConnectionClose");
            Assert.False(s.HostBusy);
            Assert.False(s.Resyncing);
            Assert.False(s.InSession);
            Assert.Null(s.LastReject);
            Assert.Equal("disconnected: RemoteConnectionClose", s.LastNotice);
            Assert.Equal(CoopBanner.None, s.CurrentBanner);
        }

        [Fact]
        public void Reset_WipesEverything_IncludingHostEndedLatch()
        {
            var s = new CoopPanelState();
            s.InSession = true;
            s.NoteSessionNotice(SessionNoticeKind.HostLeftStrategy);
            s.NoteResyncReqSent();
            s.NoteHelloAck(false, HelloRejectReason.SessionFull, "full");
            s.NoteSessionNotice(SessionNoticeKind.HostEnded);
            Assert.Equal(CoopBanner.HostEnded, s.CurrentBanner);
            // T2.6 pack teardown: the HostEnded latch that survives
            // NoteDisconnected must NOT survive a pack disable.
            s.Reset();
            Assert.False(s.InSession);
            Assert.False(s.HostBusy);
            Assert.False(s.Resyncing);
            Assert.False(s.HostEndedSession);
            Assert.Null(s.LastReject);
            Assert.Null(s.LastNotice);
            Assert.Equal(CoopBanner.None, s.CurrentBanner);
        }

        [Fact]
        public void HelloSent_ClearsStaleReject()
        {
            var s = new CoopPanelState();
            s.NoteHelloAck(false, HelloRejectReason.NotOnStrategy, "host is not on the Strategy screen");
            s.NoteHelloSent();
            Assert.Null(s.LastReject);
            Assert.Equal("joining...", s.LastNotice);
        }

        // ---------------------------------------------------------------
        // T3.6: MirrorLost banner (per-frame MirrorDetached feed) +
        // GuestMirrorLost strip text + ManualRequest resync reason.
        // ---------------------------------------------------------------

        [Fact]
        public void MirrorDetached_ShowsMirrorLost()
        {
            var s = new CoopPanelState();
            s.InSession = true;
            s.MirrorDetached = true;
            Assert.Equal(CoopBanner.MirrorLost, s.CurrentBanner);
            Assert.Equal("Mirror lost - Resync to rejoin", CoopPanelState.TextFor(s.CurrentBanner));
            s.NoteMirrorLost();
            Assert.Equal("mirror lost - Resync to rejoin", s.LastNotice);
        }

        [Fact]
        public void Resyncing_OutranksMirrorLost_AndMirrorLost_BeatsHostBusy()
        {
            var s = new CoopPanelState();
            s.InSession = true;
            s.NoteSessionNotice(SessionNoticeKind.HostLeftStrategy);
            s.MirrorDetached = true;
            // Mirror lost is the actionable state while nothing is in flight.
            Assert.Equal(CoopBanner.MirrorLost, s.CurrentBanner);
            // A resync in flight shows the recovery, not the loss.
            s.NoteResyncReqSent();
            Assert.Equal(CoopBanner.Resyncing, s.CurrentBanner);
            // Stream activated -> the panel drops MirrorDetached; Resynced clears Resyncing.
            s.MirrorDetached = false;
            s.NoteSessionNotice(SessionNoticeKind.Resynced);
            Assert.Equal(CoopBanner.HostBusy, s.CurrentBanner); // host still away
            s.NoteSessionNotice(SessionNoticeKind.HostReturned);
            Assert.Equal(CoopBanner.Spectating, s.CurrentBanner);
        }

        [Fact]
        public void Disconnect_ClearsMirrorDetached()
        {
            var s = new CoopPanelState();
            s.InSession = true;
            s.MirrorDetached = true;
            s.NoteDisconnected("RemoteConnectionClose");
            Assert.False(s.MirrorDetached);
            Assert.Equal(CoopBanner.None, s.CurrentBanner);
        }

        [Fact]
        public void GuestMirrorLost_Notice_SetsStripText()
        {
            // Host-side: the guest's mirror died mid-session -> bookkeeping line.
            var s = new CoopPanelState();
            s.NoteSessionNotice(SessionNoticeKind.GuestMirrorLost);
            Assert.Equal("guest mirror lost", s.LastNotice);
        }
    }
}
