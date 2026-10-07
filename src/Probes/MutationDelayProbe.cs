#if X2COOP_PROBES
using System;
using System.Reflection;
using System.Threading;
using Artitas;
using HarmonyLib;
using Xenonauts.Strategy.Systems;

namespace X2Coop.Probes
{
    /// <summary>
    /// R10 recon probe - mutation-delay context at the apply seam. Debug only.
    ///
    /// Question (spec r2 C3): when Runner.Update/LateUpdate execute - the future
    /// StateApplier apply points (T5.x) - is the Strategy world mid-mutation
    /// (World.IsMutationDelayed() == true, i.e. inside a DelayedMutationStack
    /// Push/Pop scope) or mid-tick (inside World.Process())? If either is ever
    /// true at the apply point, guest deltas applied there would interleave
    /// with an in-flight mutation cascade and land "delayed" instead of
    /// applied - the apply point would have to move.
    ///
    /// Mechanics:
    ///  - Harmony prefix+postfix on Artitas.World.Process() - non-generic and
    ///    parameterless (World.cs:561; the ONLY member named Process), so it is
    ///    a legal Harmony target via AccessTools.Method. Prefix/postfix
    ///    maintain _procDepth (any world) and _stratProcDepth (StrategyWorld
    ///    only, per-call flag passed via __state so prefix/postfix stay
    ///    symmetric even if the world is disposed mid-call).
    ///  - The depth counters are plain process-wide statics, NOT [ThreadStatic]
    ///    - deliberately: the point is for Runner.Update on the main thread to
    ///    NOTICE World.Process running on a different thread. A thread-local
    ///    flag could never observe that case and would hide the race.
    ///  - The Strategy world binds three ways, whichever comes first: the
    ///    Process prefix (Name=="StrategyWorld" check, rebound on change), and
    ///    X2CoopLifecycle.OnWorldCreate/OnWorldDispose via NoteStrategyWorld/
    ///    ForgetStrategyWorld (WeakReference resolved there - same path the R7
    ///    ProcessStrategy observation used).
    ///  - Runner.Update/LateUpdate call SampleUpdate()/SampleLateUpdate()
    ///    (seam calls added under #if X2COOP_PROBES). Each sample reads
    ///    world.IsMutationDelayed() + the depth flags + the pause state via a
    ///    cached TimeTransitionSystem (IsPaused() - the game's own predicate,
    ///    covers both the pause counter and the not-in-Turn state).
    ///
    /// Log lines (Log.*):
    ///   patch-hit Artitas.World.Process tid=N        - first Process call seen
    ///   r10 strategy-world-bound hash=H tid=N        - world (re)bound
    ///   r10 strategy-world-unbound hash=H            - world disposed/forgotten
    ///   r10 update delayed=false in-process=false n=60 delayedN=0 inprocN=0
    ///        stratInprocN=0 paused=0 unpaused=60 paused?=0 noWorld=0
    ///        disposed=0 err=0 tidP=1 tidU=1
    ///                                                - every ~60 samples; the
    ///    delayed=/in-process= fields are the LAST sample, the *N fields are
    ///    cumulative counters. Same shape for "r10 late" (LateUpdate).
    ///   r10 update TRANSITION delayed=false->true in-process=false->false
    ///                                                - instant log on any
    ///    flip (capped at 40/phase, then summarized in the periodic line).
    ///   r10 process n=.. strat=.. reentry=.. delayedAtEntry=.. delayedAtExit=..
    ///        maxDepth=.. tid=.. otherTid=.. updateTid=..
    ///                                                - every ~600 Process
    ///    postfix calls; covers "during/around a World.Process tick".
    /// </summary>
    internal static class MutationDelayProbe
    {
        private const int LogEverySamples = 60;
        private const int LogEveryProcess = 600;
        private const int MaxTransitionLines = 40;
        private const string StrategyWorldName = "StrategyWorld";

        /// <summary>Per-phase (Update / LateUpdate) cumulative counters.</summary>
        private sealed class Acc
        {
            public long N;
            public long NoWorld;      // no strategy world bound (menu, boot)
            public long DisposedN;    // bound world found Disposed
            public long DelayedN;     // IsMutationDelayed() == true
            public long InProcN;      // _procDepth > 0 (ANY world inside Process)
            public long StratInProcN; // _stratProcDepth > 0 (StrategyWorld inside Process)
            public long PausedN;      // TimeTransitionSystem.IsPaused() == true
            public long UnpausedN;
            public long PausedUnknownN; // no TimeTransitionSystem resolved
            public long ErrN;         // sample threw (kept out of every count)
            public long Transitions;  // emitted TRANSITION lines
            public bool HasLast;
            public bool LastDelayed;
            public bool LastInProc;
        }

        private static readonly object Gate = new object();
        private static readonly Acc Upd = new Acc();
        private static readonly Acc Late = new Acc();

        // Process-depth flags: shared statics, NOT [ThreadStatic] - see class doc.
        private static int _procDepth;
        private static int _stratProcDepth;

        private static World _strategyWorld;
        private static TimeTransitionSystem _timeSys;

        private static bool _processHit;
        private static int _processTid = -1;
        private static bool _updateTidSet;
        private static int _updateTid = -1;
        private static long _procTidOther;

        private static long _procCalls;
        private static long _procStratCalls;
        private static long _procReentry;   // Process entered while _stratProcDepth > 0
        private static long _procDelayedEntry;
        private static long _procDelayedExit;
        private static int _procMaxDepth;
        private static long _procSinceLog;

        /// <summary>Patch Artitas.World.Process() prefix+postfix. Called from X2CoopLifecycle.Create.</summary>
        public static void Apply(Harmony patcher)
        {
            if (patcher == null)
            {
                Log.Warn("r10 probes: Apply called with null patcher");
                return;
            }
            const string tag = "Artitas.World.Process";
            try
            {
                // World.cs:561 - "public bool Process()" is non-generic and
                // parameterless (the only Process member): a legal Harmony target.
                MethodInfo original = AccessTools.Method(typeof(World), "Process", Type.EmptyTypes);
                MethodInfo pre = AccessTools.Method(typeof(MutationDelayProbe), nameof(ProcessPrefix));
                MethodInfo post = AccessTools.Method(typeof(MutationDelayProbe), nameof(ProcessPostfix));
                if (original == null || pre == null || post == null)
                {
                    Log.Warn("r10 probes: method/prefix/postfix not found: " + tag);
                    return;
                }
                patcher.Patch(original, prefix: new HarmonyMethod(pre), postfix: new HarmonyMethod(post));
                Log.Info("r10 probes: patch-applied " + tag + " (prefix+postfix)");
            }
            catch (Exception ex)
            {
                Log.Exception("r10 probes patch " + tag, ex);
            }
        }

        /// <summary>
        /// Lifecycle handoff: X2CoopLifecycle.OnWorldCreate resolves the
        /// Strategy-section WeakReference and passes the live World here - the
        /// same path R7 used. Process-prefix binding covers the rest.
        /// </summary>
        public static void NoteStrategyWorld(World w)
        {
            try
            {
                if (w == null)
                {
                    return;
                }
                lock (Gate)
                {
                    if (ReferenceEquals(_strategyWorld, w))
                    {
                        return;
                    }
                    _strategyWorld = w;
                    _timeSys = SafeTimeSys(w);
                    Log.Info("r10 strategy-world-bound hash=" + w.GetHashCode()
                        + " src=lifecycle tid=" + SafeTid());
                }
            }
            catch (Exception ex)
            {
                Log.Exception("r10 NoteStrategyWorld", ex);
            }
        }

        /// <summary>
        /// T2.6 pack teardown: drop the strong refs to game objects (a world
        /// kept past teardown is a stale bind AND a leak). Counters keep
        /// their recon history; the Process-prefix rebinds the world on the
        /// next tick after re-enable.
        /// </summary>
        public static void Reset()
        {
            lock (Gate)
            {
                _strategyWorld = null;
                _timeSys = null;
                _procDepth = 0;
                _stratProcDepth = 0;
            }
        }

        /// <summary>Lifecycle handoff: OnWorldDispose - drop the ref if it is this world.</summary>
        public static void ForgetStrategyWorld(World w)
        {
            try
            {
                lock (Gate)
                {
                    if (w != null && ReferenceEquals(_strategyWorld, w))
                    {
                        _strategyWorld = null;
                        _timeSys = null;
                        Log.Info("r10 strategy-world-unbound hash=" + w.GetHashCode());
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("r10 ForgetStrategyWorld", ex);
            }
        }

        /// <summary>Runner.Update seam call - one sample per frame.</summary>
        public static void SampleUpdate()
        {
            if (!_updateTidSet)
            {
                _updateTidSet = true;
                _updateTid = SafeTid();
            }
            Sample(Upd, "update");
        }

        /// <summary>Runner.LateUpdate seam call - one sample per frame.</summary>
        public static void SampleLateUpdate()
        {
            Sample(Late, "late");
        }

        /// <summary>
        /// Harmony prefix on World.Process(): bump depths, bind the Strategy
        /// world, sample IsMutationDelayed() at tick entry, first-hit log.
        /// __state carries "this call counted as strat" to the postfix so the
        /// two stay symmetric even if the world is disposed mid-Process.
        /// </summary>
        private static void ProcessPrefix(World __instance, out bool __state)
        {
            __state = false;
            try
            {
                lock (Gate)
                {
                    _procDepth++;
                    if (_procDepth > _procMaxDepth)
                    {
                        _procMaxDepth = _procDepth;
                    }
                    int tid = SafeTid();
                    if (!_processHit)
                    {
                        _processHit = true;
                        _processTid = tid;
                        Log.Info("patch-hit Artitas.World.Process tid=" + tid);
                    }
                    else if (tid != _processTid)
                    {
                        _procTidOther++; // Process observed on a second thread
                    }

                    bool isStrat = false;
                    try
                    {
                        isStrat = __instance != null
                            && __instance.Name == StrategyWorldName
                            && !__instance.Disposed;
                    }
                    catch
                    {
                    }
                    if (isStrat)
                    {
                        __state = true;
                        _procStratCalls++;
                        if (_stratProcDepth > 0)
                        {
                            _procReentry++; // reentrant Process inside event dispatch
                        }
                        _stratProcDepth++;
                        if (!ReferenceEquals(__instance, _strategyWorld))
                        {
                            _strategyWorld = __instance;
                            _timeSys = SafeTimeSys(__instance);
                            Log.Info("r10 strategy-world-bound hash=" + __instance.GetHashCode()
                                + " src=process tid=" + tid);
                        }
                        try
                        {
                            if (__instance.IsMutationDelayed())
                            {
                                _procDelayedEntry++;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("r10 ProcessPrefix", ex);
            }
        }

        /// <summary>Harmony postfix on World.Process(): exit sample + counters.</summary>
        private static void ProcessPostfix(World __instance, bool __state)
        {
            try
            {
                lock (Gate)
                {
                    _procCalls++;
                    if (__state)
                    {
                        try
                        {
                            if (__instance != null && __instance.IsMutationDelayed())
                            {
                                _procDelayedExit++;
                            }
                        }
                        catch
                        {
                        }
                        if (_stratProcDepth > 0)
                        {
                            _stratProcDepth--;
                        }
                    }
                    if (_procDepth > 0)
                    {
                        _procDepth--;
                    }
                    if (++_procSinceLog >= LogEveryProcess)
                    {
                        _procSinceLog = 0;
                        Log.Info("r10 process n=" + _procCalls
                            + " strat=" + _procStratCalls
                            + " reentry=" + _procReentry
                            + " delayedAtEntry=" + _procDelayedEntry
                            + " delayedAtExit=" + _procDelayedExit
                            + " maxDepth=" + _procMaxDepth
                            + " tid=" + _processTid
                            + " otherTid=" + _procTidOther
                            + " updateTid=" + (_updateTidSet ? _updateTid.ToString() : "?"));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("r10 ProcessPostfix", ex);
            }
        }

        /// <summary>
        /// One Update/LateUpdate sample on the bound Strategy world: reads
        /// IsMutationDelayed() + the Process-depth flags, classifies pause via
        /// the cached TimeTransitionSystem, accumulates, and emits the periodic
        /// aggregate line (~60 samples) plus TRANSITION lines on any flip of
        /// delayed/in-process.
        /// </summary>
        private static void Sample(Acc a, string tag)
        {
            try
            {
                World w;
                bool inProc;
                bool stratInProc;
                lock (Gate)
                {
                    w = _strategyWorld;
                    inProc = _procDepth > 0;
                    stratInProc = _stratProcDepth > 0;
                }

                bool noWorld = w == null;
                bool disposed = false;
                bool delayed = false;
                bool paused = false;
                bool pausedKnown = false;
                bool err = false;
                if (!noWorld)
                {
                    try
                    {
                        if (w.Disposed)
                        {
                            disposed = true;
                        }
                        else
                        {
                            delayed = w.IsMutationDelayed();
                            TimeTransitionSystem tts;
                            lock (Gate)
                            {
                                tts = _timeSys;
                            }
                            if (tts == null)
                            {
                                // Systems can register AFTER OnWorldCreate
                                // (World.RegisterSystem -> _systemsLookup),
                                // so the bind-time SafeTimeSys may return null.
                                // Re-resolve lazily until it sticks.
                                tts = SafeTimeSys(w);
                                if (tts != null)
                                {
                                    lock (Gate)
                                    {
                                        _timeSys = tts;
                                    }
                                    Log.Info("r10 time-sys-resolved hash=" + w.GetHashCode());
                                }
                            }
                            if (tts != null)
                            {
                                paused = tts.IsPaused();
                                pausedKnown = true;
                            }
                        }
                    }
                    catch
                    {
                        err = true;
                    }
                }

                lock (Gate)
                {
                    a.N++;
                    if (noWorld)
                    {
                        a.NoWorld++;
                    }
                    if (disposed)
                    {
                        a.DisposedN++;
                    }
                    if (delayed)
                    {
                        a.DelayedN++;
                    }
                    if (inProc)
                    {
                        a.InProcN++;
                    }
                    if (stratInProc)
                    {
                        a.StratInProcN++;
                    }
                    if (err)
                    {
                        a.ErrN++;
                    }
                    if (pausedKnown)
                    {
                        if (paused)
                        {
                            a.PausedN++;
                        }
                        else
                        {
                            a.UnpausedN++;
                        }
                    }
                    else
                    {
                        a.PausedUnknownN++;
                    }

                    if (a.HasLast && (delayed != a.LastDelayed || inProc != a.LastInProc))
                    {
                        a.Transitions++;
                        if (a.Transitions <= MaxTransitionLines)
                        {
                            Log.Info("r10 " + tag + " TRANSITION delayed=" + a.LastDelayed
                                + "->" + delayed + " in-process=" + a.LastInProc + "->" + inProc);
                        }
                    }
                    a.LastDelayed = delayed;
                    a.LastInProc = inProc;
                    a.HasLast = true;

                    if (a.N % LogEverySamples == 0)
                    {
                        Log.Info("r10 " + tag
                            + " delayed=" + delayed
                            + " in-process=" + inProc
                            + " n=" + a.N
                            + " delayedN=" + a.DelayedN
                            + " inprocN=" + a.InProcN
                            + " stratInprocN=" + a.StratInProcN
                            + " paused=" + a.PausedN
                            + " unpaused=" + a.UnpausedN
                            + " paused?=" + a.PausedUnknownN
                            + " noWorld=" + a.NoWorld
                            + " disposed=" + a.DisposedN
                            + " err=" + a.ErrN
                            + " trans=" + a.Transitions
                            + " tidP=" + _processTid
                            + " tidU=" + (_updateTidSet ? _updateTid.ToString() : "?"));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Exception("r10 sample " + tag, ex);
            }
        }

        private static TimeTransitionSystem SafeTimeSys(World w)
        {
            try
            {
                return w?.GetSystem<TimeTransitionSystem>();
            }
            catch
            {
                return null;
            }
        }

        private static int SafeTid()
        {
            try
            {
                return Thread.CurrentThread.ManagedThreadId;
            }
            catch
            {
                return -1;
            }
        }
    }
}
#endif
