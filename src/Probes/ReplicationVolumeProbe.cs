#if X2COOP_PROBES
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Artitas;
using Artitas.Components;
using Artitas.Systems;
using FullSerializer;
using HarmonyLib;
using UnityEngine;
using Xenonauts.Strategy.Systems;

namespace X2Coop.Probes
{
    /// <summary>
    /// R9 recon probe - replication volume measurement. Debug builds only.
    ///
    /// Question: what would a co-op HOST emit per frame? Answer: mutations per
    /// rendered frame + the serialized byte size of one sample per mutated
    /// component TYPE, measured at each geoscape time speed (0-3) for 60 s of
    /// wall-clock each, on the live StrategyWorld (host-style session).
    ///
    /// Mechanics:
    ///  - F7 (unmapped in X2: F8=ToggleLogging via CommonDebugSystem, F9=Quick
    ///    Load, F11=bug reporter) starts/aborts a run, polled via Input.GetKey
    ///    inside a postfix on TimeTransitionSystem.UpdateTimeOnBase - the same
    ///    main-thread world-tick context SavePathProbe already uses.
    ///  - A run iterates speeds 0..3: each window opens with
    ///    World.HandleEvent(TimeControlCommand.Resume(true)) +
    ///    SetSpeedTo(i) (the same calls the geoscape UI makes,
    ///    GeoscapeMainElement.cs:755), then accumulates for 60 s.
    ///  - Mutations are counted in a second postfix on
    ///    World.ApplyComponentMutation (Harmony chains postfixes; ProbePatches'
    ///    census postfix keeps working). Frame bucketing uses
    ///    UnityEngine.Time.frameCount - mutations run on the world thread =
    ///    Unity main thread (proven: Input.GetKey works in this context).
    ///  - The FIRST mutation of a type per window snapshots the component
    ///    instance via ICloneableExtensions.Copy() (same call WorldState.cs:108
    ///    makes when snapshotting for save serialization). Snapshot is stored
    ///    and serialized ONCE at window end inside the tick postfix - keeps the
    ///    serializer out of the mid-mutation hot path and out of mid-frame
    ///    reentrancy. Falls back to the raw reference if Copy() throws.
    ///  - Serialization uses the world's own ArtitasSerializationSystem
    ///    .Serializer (fsSerializer with the Artitas converter set: Entity,
    ///    WorldState, ValueComponent, BitArray, ... - SetupSerializer,
    ///    ArtitasSerializationSystem.cs:471). TrySerialize(type, instance,
    ///    out fsData) + fsJsonPrinter.CompressedJson -> UTF8 byte count; this
    ///    matches how ComponentBagConverter serializes each component inside
    ///    WorldState (SimpleComponentBagConverter.cs:18).
    ///  - [ExcludeFromSerialization]-marked types are counted but reported
    ///    separately (raw vs filtered) - matches what WorldState would send
    ///    (WorldState.cs:77). IOptionalSerialization.IsSerializable=false on
    ///    the sampled instance is flagged per type (WorldState.cs:105).
    ///
    /// Log lines (Log.*):
    ///   r9-run-begin / r9-run-abort / r9-run-end
    ///   r9-window-begin speed=N
    ///   r9-window speed=N secs=.. frames=.. muts=.. mutsFilt=.. untyped=..
    ///         ops/f(all)=p50/p95 ops/f(nz)=p50/p95
    ///         B/f(all)=p50/p95 B/f(nz)=p50/p95  B/s=.. B/sFilt=..
    ///         envB/f(nz)=p50/p95 envB/s=..   (payload + 16 B/op envelope)
    ///         types=N sampled=N serfail=N nosample=N
    ///   r9-types speed=N top={Type=bytes x muts[!X][!O][!rem]...} (top 25
    ///         by bytes x count; X=type-excluded, O=IsSerializable false,
    ///         rem=only removal mutations seen)
    ///   r9-summary speed=N ... per window + r9-verdict vs 64 KiB/s budget.
    /// </summary>
    internal static class ReplicationVolumeProbe
    {
        private const double WindowSeconds = 60.0;
        private const int EnvelopeBytes = 16;
        private const int MaxTopTypes = 25;
        private const int SpeedCount = 4; // SetSpeedTo(0..3)
        private static readonly KeyCode TriggerKey = KeyCode.F7;

        private enum RunState { Idle, Running }

        private sealed class TypeRec
        {
            public Type Type;
            public long Muts;
            public bool Excluded;      // [ExcludeFromSerialization] on the type
            public bool OptOut;        // sampled instance: IOptionalSerialization.IsSerializable == false
            public bool SawNext;       // at least one mutation carried nextC != null (add/update)
            public bool RemOnly => !SawNext; // only removal-style mutations seen
            public IComponent Sample;  // snapshot (Copy) or raw ref fallback
            public bool SampleIsCopy;
            public int Bytes = -1;     // compressed-json UTF8 bytes; -1 nosample/fail
        }

        private sealed class FrameRec
        {
            public int Muts;
            public Dictionary<TypeRec, int> Counts; // per-type mutation counts this frame
        }

        private sealed class WindowSummary
        {
            public int Speed;
            public double Secs;
            public long Frames;
            public long PausedTicks;
            public long Muts;
            public long MutsFilt;
            public double OpsP50All, OpsP95All, OpsP50Nz, OpsP95Nz;
            public double ByteP50All, ByteP95All, ByteP50Nz, ByteP95Nz;
            public double Bps, BpsFilt, EnvBps;
        }

        private static readonly object Gate = new object();
        private static readonly Stopwatch WindowWatch = new Stopwatch();

        private static RunState _state = RunState.Idle;
        private static World _runWorld;
        private static int _speedIdx;
        private static bool _wasDown;

        // --- per-window accumulators (reset in BeginWindowLocked) -----------
        private static long _framesPolled;
        private static long _pausedTicks;
        private static readonly HashSet<int> FrameSet = new HashSet<int>();
        private static int _curFrame = int.MinValue;
        private static int _curFrameMuts;
        private static Dictionary<TypeRec, int> _curFrameCounts = new Dictionary<TypeRec, int>();
        private static readonly List<FrameRec> FrameRecs = new List<FrameRec>();
        private static readonly Dictionary<Type, TypeRec> TypeRecs = new Dictionary<Type, TypeRec>();
        private static long _untypedMuts;
        private static fsSerializer _serializer;
        private static readonly List<WindowSummary> Summaries = new List<WindowSummary>();

        /// <summary>
        /// T2.6 pack teardown: abort any run in flight (the world it was
        /// bound to may be disposed while the pack is off) and drop the
        /// strong refs. TypeRecs holds IComponent samples (Copy() snapshots or
        /// raw-ref fallbacks), FrameRecs/_curFrameCounts hold TypeRec keys and
        /// FrameSet holds frame ids - all cleared here rather than waiting
        /// for the next BeginWindowLocked purge. Accumulated window Summaries
        /// (plain numbers) keep their history.
        /// </summary>
        public static void Reset()
        {
            lock (Gate)
            {
                if (_state == RunState.Running)
                {
                    AbortRunLocked("pack teardown");
                }
                _runWorld = null;
                _serializer = null;
                _wasDown = false;
                TypeRecs.Clear();
                FrameRecs.Clear();
                FrameSet.Clear();
                _curFrameCounts = new Dictionary<TypeRec, int>();
                _curFrame = int.MinValue;
                _curFrameMuts = 0;
            }
        }

        /// <summary>Patch both targets. Called from X2CoopLifecycle.Create.</summary>
        public static void Apply(Harmony patcher)
        {
            if (patcher == null)
            {
                Log.Warn("r9 probes: Apply called with null patcher");
                return;
            }
            Patch(patcher, typeof(World), "ApplyComponentMutation",
                new[] { typeof(int), typeof(int), typeof(IComponent), typeof(IComponent), typeof(bool) },
                nameof(MutPostfix));
            Patch(patcher, typeof(TimeTransitionSystem), "UpdateTimeOnBase", null, nameof(TickPostfix));
        }

        private static void Patch(Harmony patcher, Type targetType, string methodName,
            Type[] argTypes, string postfixName)
        {
            string tag = targetType.FullName + "." + methodName;
            try
            {
                MethodInfo original = argTypes == null
                    ? AccessTools.Method(targetType, methodName)
                    : AccessTools.Method(targetType, methodName, argTypes);
                MethodInfo postfix = AccessTools.Method(typeof(ReplicationVolumeProbe), postfixName);
                if (original == null || postfix == null)
                {
                    Log.Warn("r9 probes: method/postfix not found: " + tag + " / " + postfixName);
                    return;
                }
                patcher.Patch(original, postfix: new HarmonyMethod(postfix));
                Log.Info("r9 probes: patch-applied " + tag + " -> " + postfixName);
            }
            catch (Exception ex)
            {
                Log.Exception("r9 probes patch " + tag, ex);
            }
        }

        /// <summary>
        /// Postfix: World.ApplyComponentMutation(int,int,IComponent,IComponent,bool).
        /// Only records while a run is active AND on the run world; the early-out
        /// keeps normal play ~free.
        /// </summary>
        private static void MutPostfix(World __instance, int componentID, IComponent previousC, IComponent nextC)
        {
            try
            {
                lock (Gate)
                {
                    if (_state != RunState.Running
                        || __instance == null
                        || !ReferenceEquals(__instance, _runWorld))
                    {
                        return;
                    }
                    int f = SafeFrame();
                    if (f != _curFrame)
                    {
                        FlushFrame();
                        _curFrame = f;
                    }
                    _curFrameMuts++;

                    Type t = SafeFindType(__instance, componentID);
                    if (t == null)
                    {
                        _untypedMuts++;
                        return;
                    }
                    if (!TypeRecs.TryGetValue(t, out TypeRec tr))
                    {
                        tr = new TypeRec { Type = t };
                        try
                        {
                            tr.Excluded = t.GetCustomAttributes(
                                typeof(ExcludeFromSerializationAttribute), false).Length > 0;
                        }
                        catch
                        {
                        }
                        TypeRecs[t] = tr;
                    }
                    tr.Muts++;
                    if (nextC != null)
                    {
                        tr.SawNext = true;
                    }
                    if (tr.Sample == null)
                    {
                        IComponent src = nextC != null ? nextC : previousC;
                        if (src != null)
                        {
                            IComponent copy = SafeCopy(src);
                            if (copy != null)
                            {
                                tr.Sample = copy;
                                tr.SampleIsCopy = true;
                            }
                            else
                            {
                                tr.Sample = src; // raw-ref fallback; may drift if pooled
                            }
                            try
                            {
                                tr.OptOut = src is IOptionalSerialization os && !os.IsSerializable;
                            }
                            catch
                            {
                            }
                        }
                    }
                    _curFrameCounts.TryGetValue(tr, out int c);
                    _curFrameCounts[tr] = c + 1;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("r9 MutPostfix", ex);
            }
        }

        /// <summary>
        /// Postfix: TimeTransitionSystem.UpdateTimeOnBase - runs once per
        /// DeltaTimeEvent on the world thread. Counts the frame, polls F7,
        /// advances the speed-window state machine.
        /// </summary>
        private static void TickPostfix(TimeTransitionSystem __instance)
        {
            try
            {
                World w = null;
                try
                {
                    w = __instance?.World;
                }
                catch
                {
                }
                bool down = SafeKey(TriggerKey);
                lock (Gate)
                {
                    if (_state == RunState.Running)
                    {
                        _framesPolled++;
                        // Track pause coverage so modal-driven pauses show up
                        // as a number on the window line, not silent corruption.
                        if (SafePaused(__instance))
                        {
                            _pausedTicks++;
                        }
                        int f = SafeFrame();
                        if (f >= 0)
                        {
                            FrameSet.Add(f);
                        }
                        if (!ReferenceEquals(w, _runWorld))
                        {
                            AbortRunLocked("world-changed");
                        }
                        else if (WindowWatch.Elapsed.TotalSeconds >= WindowSeconds)
                        {
                            FinishWindowLocked(false);
                            _speedIdx++;
                            if (_speedIdx >= SpeedCount)
                            {
                                EndRunLocked();
                            }
                            else
                            {
                                BeginWindowLocked(_runWorld, _speedIdx);
                            }
                        }
                    }
                    if (down && !_wasDown)
                    {
                        if (_state == RunState.Running)
                        {
                            AbortRunLocked("F7");
                        }
                        else
                        {
                            StartRunLocked(w);
                        }
                    }
                    _wasDown = down;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("r9 TickPostfix", ex);
            }
        }

        private static void StartRunLocked(World w)
        {
            string name = null;
            try
            {
                name = w?.Name;
            }
            catch
            {
            }
            if (w == null || name != "StrategyWorld")
            {
                Log.Warn("r9: F7 ignored - not on StrategyWorld (world=" + (name ?? "null") + ")");
                return;
            }
            _runWorld = w;
            _state = RunState.Running;
            _speedIdx = 0;
            Summaries.Clear();
            Log.Info("r9-run-begin world=" + name + " hash=" + w.GetHashCode()
                + " windows=" + SpeedCount + "x" + WindowSeconds + "s speeds=0..3");
            BeginWindowLocked(w, 0);
        }

        private static void BeginWindowLocked(World w, int speed)
        {
            ResetWindowAccumulators();
            _framesPolled = 0;
            _pausedTicks = 0;
            _untypedMuts = 0;
            _serializer = SafeSerializer(w);
            WindowWatch.Restart();
            // Same calls the geoscape UI makes: clear pause, set speed index.
            SafeHandle(w, TimeControlCommand.Resume(forceResume: true));
            SafeHandle(w, TimeControlCommand.SetSpeedTo(speed));
            Log.Info("r9-window-begin speed=" + speed
                + " serializer=" + (_serializer == null ? "NULL" : "ok"));
        }

        private static void ResetWindowAccumulators()
        {
            FlushFrame();
            _curFrame = int.MinValue;
            FrameRecs.Clear();
            TypeRecs.Clear();
            FrameSet.Clear();
        }

        /// <summary>Push the in-flight frame bucket into the list. Caller holds Gate.</summary>
        private static void FlushFrame()
        {
            if (_curFrameMuts > 0)
            {
                FrameRecs.Add(new FrameRec { Muts = _curFrameMuts, Counts = _curFrameCounts });
            }
            _curFrameMuts = 0;
            _curFrameCounts = new Dictionary<TypeRec, int>();
        }

        /// <summary>Serialize pending samples, compute stats, log the window. Caller holds Gate.</summary>
        private static void FinishWindowLocked(bool aborted)
        {
            FlushFrame();
            double secs = WindowWatch.Elapsed.TotalSeconds;

            // --- serialize one sample per type (deferred from mutation time) --
            int sampled = 0, serFail = 0, noSample = 0;
            var serSw = Stopwatch.StartNew();
            foreach (TypeRec tr in TypeRecs.Values)
            {
                if (tr.Sample == null)
                {
                    noSample++;
                    continue;
                }
                if (_serializer == null)
                {
                    serFail++;
                    continue;
                }
                try
                {
                    fsResult r = _serializer.TrySerialize(tr.Type, tr.Sample, out fsData data);
                    if (r.Failed || data == null)
                    {
                        serFail++;
                        continue;
                    }
                    string json = fsJsonPrinter.CompressedJson(data);
                    tr.Bytes = json == null ? 0 : Encoding.UTF8.GetByteCount(json);
                    sampled++;
                }
                catch
                {
                    serFail++;
                }
            }
            serSw.Stop();

            // --- aggregate ---------------------------------------------------
            long muts = _untypedMuts;
            long mutsFilt = _untypedMuts; // untyped counted in both (can't classify)
            foreach (TypeRec tr in TypeRecs.Values)
            {
                muts += tr.Muts;
                if (!tr.Excluded)
                {
                    mutsFilt += tr.Muts;
                }
            }
            // Rendered frames observed: distinct Time.frameCount values seen by
            // the tick postfix; fall back to tick count if Time API was dead.
            long frames = FrameSet.Count > 0 ? FrameSet.Count : _framesPolled;
            if (frames <= 0)
            {
                frames = 1;
            }

            // per-frame ops + bytes (payload and +16B envelope), raw + filtered
            int n = FrameRecs.Count;
            var ops = new long[n];
            var bytes = new long[n];
            var bytesF = new long[n];
            var envBytes = new long[n];
            long totBytes = 0, totBytesF = 0, totEnv = 0;
            for (int i = 0; i < n; i++)
            {
                FrameRec fr = FrameRecs[i];
                ops[i] = fr.Muts;
                long b = 0, bf = 0;
                foreach (KeyValuePair<TypeRec, int> kv in fr.Counts)
                {
                    int sz = kv.Key.Bytes > 0 ? kv.Key.Bytes : 0;
                    b += (long)sz * kv.Value;
                    if (!kv.Key.Excluded)
                    {
                        bf += (long)sz * kv.Value;
                    }
                }
                bytes[i] = b;
                bytesF[i] = bf;
                envBytes[i] = b + (long)EnvelopeBytes * fr.Muts;
                totBytes += b;
                totBytesF += bf;
                totEnv += envBytes[i];
            }

            long zeroFrames = Math.Max(0, frames - n);
            var ws = new WindowSummary
            {
                Speed = _speedIdx,
                Secs = secs,
                Frames = frames,
                PausedTicks = _pausedTicks,
                Muts = muts,
                MutsFilt = mutsFilt,
                OpsP50All = PercentileAll(ops, zeroFrames, 50),
                OpsP95All = PercentileAll(ops, zeroFrames, 95),
                OpsP50Nz = PercentileNz(ops, 50),
                OpsP95Nz = PercentileNz(ops, 95),
                ByteP50All = PercentileAll(bytes, zeroFrames, 50),
                ByteP95All = PercentileAll(bytes, zeroFrames, 95),
                ByteP50Nz = PercentileNz(bytes, 50),
                ByteP95Nz = PercentileNz(bytes, 95),
                Bps = secs > 0 ? totBytes / secs : 0,
                BpsFilt = secs > 0 ? totBytesF / secs : 0,
                EnvBps = secs > 0 ? totEnv / secs : 0,
            };
            Summaries.Add(ws);

            Log.Info("r9-window speed=" + _speedIdx
                + (aborted ? " ABORTED" : string.Empty)
                + " secs=" + secs.ToString("F1")
                + " frames=" + frames
                + " framesNz=" + n
                + " pausedTicks=" + _pausedTicks
                + " muts=" + muts
                + " mutsFilt=" + mutsFilt
                + " untyped=" + _untypedMuts
                + " ops/f(all)=" + F(ws.OpsP50All) + "/" + F(ws.OpsP95All)
                + " ops/f(nz)=" + F(ws.OpsP50Nz) + "/" + F(ws.OpsP95Nz)
                + " B/f(all)=" + F(ws.ByteP50All) + "/" + F(ws.ByteP95All)
                + " B/f(nz)=" + F(ws.ByteP50Nz) + "/" + F(ws.ByteP95Nz)
                + " B/s=" + F(ws.Bps)
                + " B/sFilt=" + F(ws.BpsFilt)
                + " envB/s=" + F(ws.EnvBps)
                + " types=" + TypeRecs.Count
                + " sampled=" + sampled
                + " serfail=" + serFail
                + " nosample=" + noSample
                + " serMs=" + serSw.ElapsedMilliseconds);
            EmitTopTypes();
        }

        /// <summary>Top-25 types by estimated byte contribution this window.</summary>
        private static void EmitTopTypes()
        {
            var list = new List<TypeRec>(TypeRecs.Values);
            list.Sort(delegate (TypeRec a, TypeRec b)
            {
                long ba = (a.Bytes > 0 ? a.Bytes : 0) * a.Muts;
                long bb = (b.Bytes > 0 ? b.Bytes : 0) * b.Muts;
                int c = bb.CompareTo(ba);
                return c != 0 ? c : string.CompareOrdinal(a.Type.FullName, b.Type.FullName);
            });
            var parts = new List<string>();
            int shown = Math.Min(MaxTopTypes, list.Count);
            for (int i = 0; i < shown; i++)
            {
                TypeRec tr = list[i];
                string nm = tr.Type.FullName ?? tr.Type.Name;
                string flags = (tr.Excluded ? "!X" : string.Empty)
                    + (tr.OptOut ? "!O" : string.Empty)
                    + (tr.RemOnly ? "!rem" : string.Empty)
                    + (tr.Sample != null && !tr.SampleIsCopy ? "!rawref" : string.Empty);
                parts.Add(nm + "=" + (tr.Bytes >= 0 ? tr.Bytes.ToString() : "?")
                    + "x" + tr.Muts + flags);
            }
            if (list.Count > shown)
            {
                parts.Add("...(" + (list.Count - shown) + " more)");
            }
            Log.Info("r9-types speed=" + _speedIdx + " top={" + string.Join(",", parts.ToArray()) + "}");
        }

        private static void AbortRunLocked(string why)
        {
            Log.Warn("r9-run-abort why=" + why + " speedIdx=" + _speedIdx
                + " elapsed=" + WindowWatch.Elapsed.TotalSeconds.ToString("F1") + "s");
            FinishWindowLocked(true);
            EndRunLocked();
        }

        private static void EndRunLocked()
        {
            _state = RunState.Idle;
            _runWorld = null;
            double maxBps = 0, maxEnvBps = 0;
            foreach (WindowSummary s in Summaries)
            {
                if (s.Bps > maxBps) { maxBps = s.Bps; }
                if (s.EnvBps > maxEnvBps) { maxEnvBps = s.EnvBps; }
                Log.Info("r9-summary speed=" + s.Speed
                    + " secs=" + s.Secs.ToString("F1")
                    + " muts=" + s.Muts
                    + " pausedTicks=" + s.PausedTicks
                    + " ops/f(nz)p95=" + F(s.OpsP95Nz)
                    + " B/f(nz)p95=" + F(s.ByteP95Nz)
                    + " B/s=" + F(s.Bps)
                    + " envB/s=" + F(s.EnvBps));
            }
            const double budget = 64.0 * 1024.0;
            Log.Info("r9-run-end windows=" + Summaries.Count
                + " maxB/s=" + F(maxBps)
                + " maxEnvB/s=" + F(maxEnvBps)
                + " budget=" + ((int)budget)
                + " verdict=" + (maxEnvBps <= budget ? "WITHIN" : "EXCEEDED"));
        }

        // --- helpers ---------------------------------------------------------

        /// <summary>Percentile over nonzero-frame values (sorted copy).</summary>
        private static double PercentileNz(long[] vals, int p)
        {
            if (vals.Length == 0)
            {
                return 0;
            }
            var s = (long[])vals.Clone();
            Array.Sort(s);
            int idx = (int)Math.Ceiling(p / 100.0 * s.Length) - 1;
            if (idx < 0) { idx = 0; }
            return s[idx];
        }

        /// <summary>Percentile over ALL frames; zeroFrames = frames with no mutations (value 0).</summary>
        private static double PercentileAll(long[] nzVals, long zeroFrames, int p)
        {
            long total = nzVals.Length + zeroFrames;
            if (total <= 0)
            {
                return 0;
            }
            long rank = (long)Math.Ceiling(p / 100.0 * total); // nearest-rank, 1-based
            if (rank <= zeroFrames)
            {
                return 0;
            }
            var s = (long[])nzVals.Clone();
            Array.Sort(s);
            long idx = rank - zeroFrames - 1;
            if (idx >= s.Length) { idx = s.Length - 1; }
            return s[idx];
        }

        private static int SafeFrame()
        {
            try
            {
                return Time.frameCount;
            }
            catch
            {
                return -1;
            }
        }

        private static bool SafeKey(KeyCode k)
        {
            try
            {
                return Input.GetKey(k);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The game's own pause predicate on the ticked system; false when unknown.</summary>
        private static bool SafePaused(TimeTransitionSystem sys)
        {
            try
            {
                return sys != null && sys.IsPaused();
            }
            catch
            {
                return false;
            }
        }

        private static Type SafeFindType(World w, int componentID)
        {
            try
            {
                return w.ComponentManager?.FindComponentType(componentID);
            }
            catch
            {
                return null;
            }
        }

        private static IComponent SafeCopy(IComponent c)
        {
            try
            {
                return c.Copy();
            }
            catch
            {
                return null;
            }
        }

        private static fsSerializer SafeSerializer(World w)
        {
            try
            {
                return w.GetSystem<ArtitasSerializationSystem>()?.Serializer;
            }
            catch (Exception ex)
            {
                Log.Exception("r9 GetSystem<ArtitasSerializationSystem>", ex);
                return null;
            }
        }

        private static void SafeHandle(World w, Artitas.Event ev)
        {
            try
            {
                w?.HandleEvent(ev);
            }
            catch (Exception ex)
            {
                Log.Exception("r9 HandleEvent " + ev?.GetType().Name, ex);
            }
        }

        private static string F(double v)
        {
            return v.ToString("F1");
        }
    }
}
#endif
