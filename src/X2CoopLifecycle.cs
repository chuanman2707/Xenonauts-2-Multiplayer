using System;
using System.Collections.Generic;
using Artitas;
using Common.Content;
using Common.Content.DataStructures;
using Common.Content.Lifecycle;
using HarmonyLib;

namespace X2Coop
{
    /// <summary>
    /// X2 Co-op content-pack lifecycle entry point.
    ///
    /// ContentPackState.Patch() loads every dll under &lt;pack&gt;\assembly\common\
    /// via Assembly.Load(byte[]) (so the Assembly has NO Location - never use it,
    /// pack.path is the only filesystem truth), runs Harmony.PatchAll, then
    /// instantiates every non-abstract IContentPackLifecycle and calls Create().
    /// Unpatch() calls Destroy() + harmony.UnpatchAll().
    ///
    /// Every method body is wrapped in try/catch -> own log: exceptions escaping
    /// a lifecycle method are logged by ContentPackState at log4net ERROR, which
    /// feeds PostHog analytics (spec r2 C5). Nothing escapes this class.
    /// </summary>
    public sealed class X2CoopLifecycle : IContentPackLifecycle
    {
        // Stored for later tasks (S1+ applies Harmony patches / reads pack files).
        private string _packPath;   // pack.path - Assembly.Location is empty for byte[]-loaded assemblies
        private string _harmonyId;  // patcher.Id
        private Harmony _patcher;

        public void Create(ContentPackState pack, Harmony patcher)
        {
            try
            {
                _packPath = pack?.path;
                _patcher = patcher;
                _harmonyId = null;
                try
                {
                    _harmonyId = patcher?.Id;
                }
                catch
                {
                    // Harmony.Id getter should never throw; stay defensive anyway.
                }

                string uid = null;
                try
                {
                    uid = pack?.UID; // resolves the manifest; may return null
                }
                catch
                {
                }

                // asm = runtime hash of THIS loaded copy of the assembly; two
                // byte[]-loaded copies in one process get different values.
                int asm = GetType().Assembly.GetHashCode();
                Log.Info("Create path=" + (_packPath ?? "?")
                    + " uid=" + (uid ?? "?")
                    + " asm=" + asm
                    + " harmony=" + (_harmonyId ?? "?")
                    + " ver=" + ModInfo.ModVersion
                    + " logfile=" + (Log.FilePath ?? "<none>"));

                // T2.1: the DontDestroyOnLoad pump (X2CoopRunner GameObject +
                // NetTransport). Idempotent - a re-enable with the same
                // assembly copy reuses the live instance.
                try
                {
                    Runtime.Runner.EnsureStarted();
                }
                catch (Exception ex)
                {
                    Log.Exception("Runner.EnsureStarted", ex);
                }

                // T3.2: clock freeze - prefix on TimeTransitionSystem.
                // UpdateTimeOnBase gated on MirrorState.HostJoinFreeze
                // (host join window; T3.4 adds the guest MirrorMode half).
                // Ships in Release - NOT probe-gated.
                try
                {
                    Mirror.ClockFreeze.Apply(_patcher);
                }
                catch (Exception ex)
                {
                    Log.Exception("ClockFreeze.Apply", ex);
                }

                // T3.5: guest save suppression - prefixes on SaveGameSystem
                // block every save/load entry point while MirrorMode != Off
                // (SaveReceiver's own mirror load passes via ReceiverScope).
                // Ships in Release - NOT probe-gated.
                try
                {
                    Mirror.SaveSuppress.Apply(_patcher);
                }
                catch (Exception ex)
                {
                    Log.Exception("SaveSuppress.Apply", ex);
                }

                // T4.1: guest mutation firewall - drops guest-local writes
                // into the mirror world's replicated state, taps Unity's
                // logMessageReceived for dropped-mutation casualties, and
                // auto-resyncs after 3 correlated exceptions. Ships in
                // Release - NOT probe-gated.
                try
                {
                    Mirror.MirrorFirewall.Apply(_patcher);
                }
                catch (Exception ex)
                {
                    Log.Exception("MirrorFirewall.Apply", ex);
                }

                // T5.2-r1: disarm guest-local cleanup/convergence passes on
                // the live mirror world (CleanupSystem's unbounded family
                // rescan wedged the guest when the firewall blocked its
                // marker-leave/delete-join mutations; Signpost/Xenopedia
                // resolve loops share the shape). Ships in Release.
                try
                {
                    Mirror.MirrorSystems.Apply(_patcher);
                }
                catch (Exception ex)
                {
                    Log.Exception("MirrorSystems.Apply", ex);
                }

                // T4.2-r1 (review I-1): guest geoscape pick-mode gate -
                // prefix on StrategyUISystem.TriggerEventOnFSM drops
                // SelectTarget (all three geoscape pick states) and
                // Build->GeoscapeBaseBuild trigger commands while
                // MirrorMode != Off, so the orphaned-hover NRE inside the
                // LAUNCH AIRCRAFT pick mode can never be reached.
                // Ships in Release - NOT probe-gated.
                try
                {
                    Mirror.UIPickGate.Apply(_patcher);
                }
                catch (Exception ex)
                {
                    Log.Exception("UIPickGate.Apply", ex);
                }

                // T5.1: host replication source - postfixes on
                // World.ApplyComponentMutation / CreateEntity /
                // EntityRecyclingFactory.Obtain / the two delete sinks,
                // gated on the host StrategyWorld while a session lives.
                // Ships in Release - NOT probe-gated.
                try
                {
                    Replication.ReplicationSource.Apply(_patcher);
                }
                catch (Exception ex)
                {
                    Log.Exception("ReplicationSource.Apply", ex);
                }

#if X2COOP_PROBES
                // T0.7 probe patches - Debug builds only. The game's own
                // PatchAll(assembly) already ran before Create; we patch
                // manually here for explicit applied/failed logging.
                try
                {
                    Probes.ProbePatches.Apply(_patcher);
                }
                catch (Exception ex)
                {
                    Log.Exception("ProbePatches.Apply", ex);
                }

                // T0.6 RECON R3 probes - Debug builds only; logs "recon r3-N"
                // lines for each check (see Probes/ReconProbes.cs).
                try
                {
                    Probes.ReconProbes.Run();
                }
                catch (Exception ex)
                {
                    Log.Exception("ReconProbes.Run", ex);
                }

                // R8 RECON save-path probe - F8 hotkey (polled in a postfix on
                // UpdateTimeOnBase; see Probes/SavePathProbe.cs).
                try
                {
                    Probes.SavePathProbe.Apply(_patcher);
                }
                catch (Exception ex)
                {
                    Log.Exception("SavePathProbe.Apply", ex);
                }

                // R9 RECON replication-volume probe - F7 hotkey iterates time
                // speeds 0..3 x 60 s; see Probes/ReplicationVolumeProbe.cs.
                try
                {
                    Probes.ReplicationVolumeProbe.Apply(_patcher);
                }
                catch (Exception ex)
                {
                    Log.Exception("ReplicationVolumeProbe.Apply", ex);
                }

                // R10 RECON mutation-delay context probe - patches
                // Artitas.World.Process (prefix+postfix); Runner.Update/
                // LateUpdate sample it; see Probes/MutationDelayProbe.cs.
                try
                {
                    Probes.MutationDelayProbe.Apply(_patcher);
                }
                catch (Exception ex)
                {
                    Log.Exception("MutationDelayProbe.Apply", ex);
                }

                // T3.4 mirror-mutation counter - postfix on
                // World.ApplyComponentMutation scoped to the guest mirror
                // world; SaveReceiver's heartbeat reports "mirror-muts n=...".
                try
                {
                    Probes.MirrorMutProbe.Apply(_patcher);
                }
                catch (Exception ex)
                {
                    Log.Exception("MirrorMutProbe.Apply", ex);
                }
#endif
            }
            catch (Exception ex)
            {
                Log.Exception("Create", ex);
            }
        }

        public void Destroy()
        {
            try
            {
                Log.Info("Destroy path=" + (_packPath ?? "?") + " harmony=" + (_harmonyId ?? "?"));
                // T2.6: centralized teardown - stops the NetManager (with the
                // HostEnded flush window), destroys the X2CoopRunner object,
                // clears every static that would otherwise survive into the
                // re-enabled pack (same-assembly copy, R3) and deletes the
                // profile scratch dirs. Harmony UnpatchAll is the game's own
                // call inside ContentPackState.Unpatch - never ours.
                try
                {
                    Runtime.Teardown.Run();
                }
                catch (Exception ex)
                {
                    Log.Exception("Teardown.Run", ex);
                }
                _patcher = null;
            }
            catch (Exception ex)
            {
                Log.Exception("Destroy", ex);
            }
        }

        public void OnWorldCreate(IContentPackLifecycle.Section section, WeakReference<World> world)
        {
            try
            {
                Log.Info("OnWorldCreate section=" + section + " world=" + WorldTag(world));
                // T3.1: ScreenGuard tracks the Strategy world and pushes the
                // HostReturned notice to an in-session guest on the off->on
                // transition.
                try
                {
                    Session.ScreenGuard.Note(section, world);
                }
                catch (Exception ex)
                {
                    Log.Exception("ScreenGuard.Note", ex);
                }
                // T3.3: guest mirror-load tracking - while MirrorMode is
                // PendingActivate the next Strategy world is the one our
                // LoadGame spawned (guest-local loads are Mode=Off and are
                // ignored there).
                try
                {
                    Session.SaveReceiver.OnWorldCreate(section, world);
                }
                catch (Exception ex)
                {
                    Log.Exception("SaveReceiver.OnWorldCreate", ex);
                }
#if X2COOP_PROBES
                // R7 recon: record the concrete ProcessStrategy the Strategy world
                // was built with. Decompile: StrategyScreen.cs:209 sets
                // DebugProcessStrategy only when Artitas.Event.ENABLE_PROFILING is
                // true, else DefaultProcessStrategy (WorldConfiguration.cs:110
                // default is the same). Recorded for S3; S1 does not patch it.
                if (section == IContentPackLifecycle.Section.Strategy
                    && world != null
                    && world.TryGetTarget(out World w)
                    && w != null)
                {
                    Log.Info("strategy-process type="
                        + (w.ProcessStrategy == null ? "null" : w.ProcessStrategy.GetType().Name));
                    // R10: hand the live Strategy world to the mutation-delay
                    // probe (earliest possible bind; Process-prefix rebinds it).
                    Probes.MutationDelayProbe.NoteStrategyWorld(w);
                }
#endif
            }
            catch (Exception ex)
            {
                Log.Exception("OnWorldCreate", ex);
            }
        }

        public IEnumerable<Descriptor> GetRequiredAssets(IContentPackLifecycle.Section section)
        {
            try
            {
                return new Descriptor[0];
            }
            catch (Exception ex)
            {
                Log.Exception("GetRequiredAssets", ex);
                return new Descriptor[0];
            }
        }

        public void OnWorldDispose(IContentPackLifecycle.Section section, WeakReference<World> world)
        {
            try
            {
                Log.Info("OnWorldDispose section=" + section + " world=" + WorldTag(world));
                // T3.1: ScreenGuard drops the ref and pushes HostLeftStrategy
                // to an in-session guest on the on->off transition.
                try
                {
                    Session.ScreenGuard.Forget(section, world);
                }
                catch (Exception ex)
                {
                    Log.Exception("ScreenGuard.Forget", ex);
                }
                // T3.6: guest mirror-death tracking - the ACTIVE mirror world
                // dying mid-session detaches the receiver (Mode=Detached) and
                // notifies the host (GuestMirrorLost). Harmless host-side:
                // Mode is always Off there.
                try
                {
                    Session.SaveReceiver.OnWorldDispose(section, world);
                }
                catch (Exception ex)
                {
                    Log.Exception("SaveReceiver.OnWorldDispose", ex);
                }
#if X2COOP_PROBES
                // R10: drop the probe's Strategy-world ref on dispose.
                if (section == IContentPackLifecycle.Section.Strategy
                    && world != null
                    && world.TryGetTarget(out World dw)
                    && dw != null)
                {
                    Probes.MutationDelayProbe.ForgetStrategyWorld(dw);
                }
#endif
            }
            catch (Exception ex)
            {
                Log.Exception("OnWorldDispose", ex);
            }
        }

        private static string WorldTag(WeakReference<World> world)
        {
            try
            {
                if (world != null && world.TryGetTarget(out World w) && w != null)
                {
                    return w.GetHashCode().ToString();
                }
            }
            catch
            {
            }
            return "null";
        }
    }
}
