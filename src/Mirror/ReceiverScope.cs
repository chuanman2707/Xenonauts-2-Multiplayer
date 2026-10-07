namespace X2Coop.Mirror
{
    /// <summary>
    /// T3.5: the single carve-out inside the guest save/load suppression.
    ///
    /// SaveGameSystem.LoadGame(FileDescriptor, bool) is the funnel for EVERY
    /// load this process can start - the load-menu confirm, LoadGameCommand
    /// subscribers (Continue, quickload), the boot-time direct-load - AND the
    /// one call we must let through: SaveReceiver's own mirror load. The
    /// SaveSuppress prefix on that static blocks them all while
    /// MirrorState.Mode != Off, EXCEPT when <see cref="InScope"/> is set -
    /// SaveReceiver wraps its call in a try/finally around this flag, so only
    /// the receiver's load survives the gate.
    ///
    /// A plain bool, not a scope object: every read/write happens on the Unity
    /// main thread (the receiver calls LoadGame from Runner's message pump;
    /// the Harmony prefix runs on the caller's thread), so no locking is
    /// needed and the flag can never leak past the finally.
    /// </summary>
    internal static class ReceiverScope
    {
        /// <summary>True only inside SaveReceiver's own static LoadGame call.</summary>
        public static bool InScope;
    }
}
