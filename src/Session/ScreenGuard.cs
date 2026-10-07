using System;
using Artitas;
using Common.Content.Lifecycle;

namespace X2Coop.Session
{
    /// <summary>
    /// T3.1 ScreenGuard - "is the local game on the Strategy layer?" tracking
    /// plus the host-side leave/return notices that ride it.
    ///
    /// The Strategy world is held as a WeakReference&lt;World&gt; fed by
    /// X2CoopLifecycle.OnWorldCreate/OnWorldDispose - the game's own lifecycle
    /// seam (StrategyScreen.cs:212 creates it under Section.Strategy with
    /// Name "StrategyWorld" set one line earlier; StrategyScreen.cs:429
    /// disposes it from OnExit/OnDispose - i.e. EVERY transition off the
    /// Strategy screen: battle, main menu, save reload, quit).
    ///
    /// Identity check (decompile-verified): only StrategyScreen ever raises
    /// Section.Strategy worlds, but we still require Name == "StrategyWorld"
    /// at bind time AND at query time - a future world reusing the section
    /// can never fake presence.
    ///
    /// On a real off->on / on->off transition the guard calls
    /// <see cref="SessionHost.NotifyStrategyPresence"/>, which sends the
    /// in-session guest a SessionNotice (HostReturned / HostLeftStrategy -
    /// the T2.4 "Host busy" banner already maps those kinds). Transitions
    /// only - a stray re-create or unmatched dispose cannot double-send.
    ///
    /// Join gate: SessionHost.HandleHello refuses Hello with NotOnStrategy
    /// while <see cref="IsOnStrategy"/> is false (main menu, load screen,
    /// in-battle, mid-quit) - joins are accepted only while the Strategy
    /// world is live.
    /// </summary>
    public static class ScreenGuard
    {
        private const string StrategyWorldName = "StrategyWorld";

        private static readonly object Gate = new object();
        private static WeakReference<World> _strategy;

        /// <summary>
        /// True while the bound Strategy-section world resolves to a live,
        /// undisposed World named "StrategyWorld". A broken check must refuse,
        /// not crash - false on any exception.
        /// </summary>
        public static bool IsOnStrategy
        {
            get
            {
                try
                {
                    lock (Gate)
                    {
                        return IsOnStrategyCore();
                    }
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// The live bound Strategy world, or null when <see cref="IsOnStrategy"/>
        /// is false. T3.2 SaveStreamer serializes this instance - same
        /// resolve/name/Disposed checks as the join gate.
        /// </summary>
        public static World StrategyWorld
        {
            get
            {
                try
                {
                    lock (Gate)
                    {
                        return IsOnStrategyCore() ? Resolve() : null;
                    }
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <summary>Lifecycle hook: OnWorldCreate feeds this.</summary>
        public static void Note(IContentPackLifecycle.Section section, WeakReference<World> world)
        {
            try
            {
                if (section != IContentPackLifecycle.Section.Strategy) { return; }
                World w = Resolve(world);
                bool becameOn;
                lock (Gate)
                {
                    bool wasOn = IsOnStrategyCore();
                    if (w != null && w.Name == StrategyWorldName)
                    {
                        // Latest wins: a recreate always follows a dispose in
                        // StrategyScreen, but a defensive replace can never
                        // leave us tracking the dead world.
                        _strategy = new WeakReference<World>(w);
                    }
                    else
                    {
                        Log.Warn("screen-guard: strategy-section world rejected"
                            + " name=" + (w != null ? (w.Name ?? "<null>") : "<dead>"));
                    }
                    becameOn = !wasOn && IsOnStrategyCore();
                }
                Log.Info("screen-guard bound world=" + (w != null ? w.GetHashCode().ToString() : "null")
                    + " name=" + (w != null ? (w.Name ?? "<null>") : "-")
                    + " onStrategy=" + IsOnStrategy);
                if (becameOn)
                {
                    SessionHost.NotifyStrategyPresence(true); // -> HostReturned
                }
            }
            catch (Exception ex)
            {
                Log.Exception("ScreenGuard.Note", ex);
            }
        }

        /// <summary>Lifecycle hook: OnWorldDispose drops the ref when the Strategy world dies.</summary>
        public static void Forget(IContentPackLifecycle.Section section, WeakReference<World> world)
        {
            try
            {
                if (section != IContentPackLifecycle.Section.Strategy) { return; }
                World w = Resolve(world);
                bool becameOff;
                lock (Gate)
                {
                    bool wasOn = IsOnStrategyCore();
                    World cur = Resolve();
                    if (w == null || cur == null || ReferenceEquals(w, cur))
                    {
                        _strategy = null;
                    }
                    becameOff = wasOn && !IsOnStrategyCore();
                }
                Log.Info("screen-guard unbound world=" + (w != null ? w.GetHashCode().ToString() : "null")
                    + " onStrategy=" + IsOnStrategy);
                if (becameOff)
                {
                    SessionHost.NotifyStrategyPresence(false); // -> HostLeftStrategy
                }
            }
            catch (Exception ex)
            {
                Log.Exception("ScreenGuard.Forget", ex);
            }
        }

        /// <summary>Caller holds Gate (or doesn't - Monitor is reentrant).</summary>
        private static bool IsOnStrategyCore()
        {
            World w = Resolve();
            return w != null
                && !w.Disposed
                && w.Name == StrategyWorldName;
        }

        private static World Resolve(WeakReference<World> wr)
        {
            try
            {
                if (wr != null && wr.TryGetTarget(out World w)) { return w; }
            }
            catch
            {
            }
            return null;
        }

        private static World Resolve()
        {
            return Resolve(_strategy);
        }
    }
}
