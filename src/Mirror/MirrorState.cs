namespace X2Coop.Mirror
{
    /// <summary>
    /// Guest mirror-mode lifecycle (spec section 4 "MirrorMode"). Set by
    /// T3.3 SaveReceiver (+ T3.6 resync states); consumed by T3.4 (guest
    /// clock freeze), T3.5 (save suppression / mutation firewall) and T4.x
    /// (delta applier).
    ///
    /// Lifecycle order: Off -&gt; Receiving -&gt; PendingActivate -&gt; Active
    /// (-&gt; Detached on a mid-session failure/mirror death; any resync
    /// stream re-enters at Receiving). Session end always returns to Off.
    ///
    /// The suppression gate is "Mode != Off" by design: Receiving,
    /// PendingActivate, Active AND Detached all keep guest-local
    /// save/load/delete paths blocked for the life of the session (spec:
    /// the guest never writes Saves\ while attached; a detached guest that
    /// wants free local play disconnects first).
    /// </summary>
    public enum MirrorMode : byte
    {
        /// <summary>Not mirroring (idle, host role, or post-session).</summary>
        Off = 0,

        /// <summary>
        /// Guest (T3.6): a SaveOffer arrived and the reassembly buffer is
        /// armed - the stream is in flight but LoadGame has NOT been called
        /// yet. Distinct from Off so the suppression gate stays armed during
        /// the receive window (a guest-local load inside it would spawn a
        /// foreign StrategyWorld). Distinct from PendingActivate so a
        /// foreign world-create landing inside the window can never be
        /// claimed as the mirror (the bind gate reads PendingActivate only).
        /// </summary>
        Receiving = 1,

        /// <summary>
        /// Guest: a verified save stream was written to Saves\x2coop_guest\
        /// and LoadGame was called; the receiver is waiting for the new
        /// StrategyWorld to be created and populated. The pending flag is
        /// what tells "this OnWorldCreate is our mirror load" apart from a
        /// guest-initiated local campaign load.
        /// </summary>
        PendingActivate = 2,

        /// <summary>Guest: mirror world live - the freeze/firewall/applier gates read this.</summary>
        Active = 3,

        /// <summary>
        /// Guest (T3.6): still in-session but the mirror is gone - either
        /// the mirror world was disposed mid-session (quit-to-menu / a
        /// local transition that replaced it), or the load failed past the
        /// resync budget. Suppression stays ON (Mode != Off), world binding
        /// and clock freeze stay OFF (both need their exact mode). Recovery
        /// is a fresh resync (Resync button / HostReturned does not re-arm
        /// it - a detach is treated as "guest stepped away from the
        /// mirror", not as an error to paper over).
        /// </summary>
        Detached = 4,
    }

    /// <summary>
    /// Session-state flags the mirror/freeze machinery (Phase 3+) reads.
    /// Plain statics: read and written only on the Unity main thread (Runner
    /// pump and Harmony patches all run there - spec section 4), so no locking.
    ///
    /// <see cref="HostJoinFreeze"/> is the T2.3 seam for spec section 6's
    /// "host pauses the clock on accept": SessionHost sets it when it sends
    /// HelloAck-accept and clears it when the guest leaves / the session ends.
    /// T3.2 SaveStreamer takes over the set/clear lifecycle (cleared exactly
    /// once at GuestReady or abort); T3.2's Mirror/ClockFreeze.cs is the
    /// prefix on TimeTransitionSystem.UpdateTimeOnBase that actually returns
    /// false while this flag is set (T3.4 extends the same prefix for the
    /// guest's mirror world). T2.3 owned ONLY the flag - no clock patch yet.
    ///
    /// <see cref="Active"/> is the guest-side mirror-mode gate T3.3 sets after
    /// the streamed save loads (clock freeze + mutation firewall + save
    /// suppression all hang off it). Declared now so the flag's semantics are
    /// in one place; stays false until Phase 3.
    ///
    /// Delta sequencing placeholders (T3.2): <see cref="BaselineSeq"/> is the
    /// seq boundary baked into the streamed save - spec section 6 "buffered
    /// deltas > baselineSeq flush at GuestReady". <see cref="LastDeltaSeq"/>
    /// is the last seq the (T5.1) replication source emitted; the first live
    /// delta will be stamped LastDeltaSeq+1, so BaselineSeq = LastDeltaSeq at
    /// snapshot time makes "first live delta = baselineSeq + 1" fall out for
    /// free. Both are 0 placeholders until T5.1/T5.2 consume them; SaveOffer
    /// already carries BaselineSeq on the wire.
    /// </summary>
    public static class MirrorState
    {
        /// <summary>Host: a join is mid-handshake/mid-save-stream; the clock must not advance.</summary>
        public static bool HostJoinFreeze;

        /// <summary>
        /// Guest mirror-mode state (T3.3/T3.6: Receiving at SaveOffer,
        /// PendingActivate at LoadGame, Active once the new StrategyWorld is
        /// populated, Detached on mid-session mirror loss; Off on session
        /// end). T3.4/T3.5 gates read <see cref="Mode"/>.
        /// </summary>
        public static MirrorMode Mode;

        /// <summary>Guest: mirroring a host's Strategy world (true only once the loaded world is live).</summary>
        public static bool Active => Mode == MirrorMode.Active;

        /// <summary>Seq boundary of the save offered to the joining guest (SaveOffer.baselineSeq).</summary>
        public static long BaselineSeq;

        /// <summary>Last emitted StateDelta seq (T5.1 increments before stamping). 0 = none yet.</summary>
        public static long LastDeltaSeq;

        /// <summary>
        /// T2.6 pack teardown: drop both flags. HostJoinFreeze is normally
        /// cleared by SessionHost.ClearGuest already, but a torn-down host
        /// session must never leave the freeze latched for the next pack
        /// enable (same-assembly copy keeps the statics, R3). T3.2: also
        /// reset the seq counters - a stale BaselineSeq would silently
        /// mis-scope the next session's delta flush. T3.3: Mode too - a
        /// stale PendingActivate would mis-claim the next Strategy world
        /// create as a mirror load.
        /// </summary>
        public static void Reset()
        {
            HostJoinFreeze = false;
            Mode = MirrorMode.Off;
            BaselineSeq = 0;
            LastDeltaSeq = 0;
        }
    }
}
