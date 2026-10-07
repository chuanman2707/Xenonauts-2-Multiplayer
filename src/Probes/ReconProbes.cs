#if X2COOP_PROBES
using System;
using System.Reflection;
using Artitas;
using FullSerializer;
using HarmonyLib;

namespace X2Coop.Probes
{
    /// <summary>
    /// T0.6 RECON R3 probes - side effects of the Assembly.Load(byte[]) "neither"
    /// load context. Debug builds only (X2COOP_PROBES). <see cref="Run"/> is called
    /// once per X2CoopLifecycle.Create; every check logs a tagged "recon r3-N" line:
    ///
    ///   r3-1  Assembly.Location for our byte[]-loaded assembly (expect "").
    ///   r3-2  How many copies of X2Coop are loaded in the AppDomain right now
    ///         (a re-enable loads a SECOND copy; the first copy's statics survive).
    ///   r3-3  [HarmonyPatch] attribute path: the game's own PatchAll
    ///         (ContentPackState.cs:554) applies <see cref="R3AttrProbePatch"/>
    ///         before Create even runs; its postfix logs "patch-hit attr" once.
    ///   r3-4  Game-assembly references resolve with no AssemblyResolve hook
    ///         (game assemblies are already loaded; we log their real Locations).
    ///   r3-5  Deliberate exception -> ex.ToString() must carry .cs:NN line numbers
    ///         (the loader fed our .pdb to Assembly.Load(byte[], pdb[]) via
    ///         AssemblyLoadProcessor.cs:44-50).
    ///   r3-6  fsSerializer round-trip on a DTO defined in THIS assembly, both
    ///         explicit storage type and object-typed ($type name) paths.
    ///
    /// All bodies are guarded like the lifecycle: nothing may escape.
    /// </summary>
    internal static class ReconProbes
    {
        /// <summary>Entry point called from X2CoopLifecycle.Create (probes only).</summary>
        public static void Run()
        {
            try
            {
                CheckLocation();
            }
            catch (Exception ex)
            {
                Log.Exception("recon r3-1", ex);
            }
            try
            {
                CheckCopies();
            }
            catch (Exception ex)
            {
                Log.Exception("recon r3-2", ex);
            }
            try
            {
                CheckGameRefs();
            }
            catch (Exception ex)
            {
                Log.Exception("recon r3-4", ex);
            }
            try
            {
                CheckStackTraceLines();
            }
            catch (Exception ex)
            {
                Log.Exception("recon r3-5", ex);
            }
            try
            {
                CheckFsSerializer();
            }
            catch (Exception ex)
            {
                Log.Exception("recon r3-6", ex);
            }
        }

        // ------------------------------------------------------------------
        // r3-1: typeof(X2CoopLifecycle).Assembly.Location == "" (no Location).
        // ------------------------------------------------------------------
        private static void CheckLocation()
        {
            Assembly self = typeof(X2CoopLifecycle).Assembly;
            Log.Info("recon r3-1 asm-location=\"" + SafeLocation(self) + "\""
                + " name=" + self.GetName().Name
                + " fullname=" + self.FullName);
        }

        // ------------------------------------------------------------------
        // r3-2: count X2Coop copies in the AppDomain. On re-enable the loader
        // Assembly.Load(byte[])s the dll AGAIN -> a second copy with fresh
        // statics; the old copy (and its statics) survives until domain unload.
        // ------------------------------------------------------------------
        private static void CheckCopies()
        {
            Assembly self = typeof(X2CoopLifecycle).Assembly;
            int copies = 0;
            int total = 0;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                total++;
                try
                {
                    if (a.GetName().Name == self.GetName().Name)
                    {
                        copies++;
                    }
                }
                catch
                {
                }
            }
            Log.Info("recon r3-2 x2coop-copies=" + copies
                + " total-assemblies=" + total
                + " this-copy-hash=" + self.GetHashCode());
        }

        // ------------------------------------------------------------------
        // r3-4: cross-assembly references to game types resolve with NO
        // AppDomain.AssemblyResolve hook (none installed by us; game assemblies
        // are already loaded in the domain, so the JIT binds directly).
        // Contrast: the game's own assembly HAS a real Location.
        // ------------------------------------------------------------------
        private static void CheckGameRefs()
        {
            Assembly gameAsm = typeof(World).Assembly;           // Assembly-CSharp
            Assembly firstpassAsm = typeof(fsSerializer).Assembly; // Assembly-CSharp-firstpass
            Assembly harmonyAsm = typeof(Harmony).Assembly;      // 0Harmony
            Log.Info("recon r3-4 game-refs-resolve: "
                + "World=" + typeof(World).FullName + "@" + SafeLocation(gameAsm) + " ; "
                + "fsSerializer=" + typeof(fsSerializer).FullName + "@" + SafeLocation(firstpassAsm) + " ; "
                + "Harmony=" + typeof(Harmony).FullName + "@" + SafeLocation(harmonyAsm)
                + " ; our-assemblyresolve-hook=none-installed");
        }

        // ------------------------------------------------------------------
        // r3-5: throw+catch a deliberate exception; the .pdb beside the dll
        // (deployed to assembly\common\, loaded via Assembly.Load(byte[],pdb))
        // must put "ReconProbes.cs:NN" source lines in the stack trace.
        // ------------------------------------------------------------------
        private static void CheckStackTraceLines()
        {
            try
            {
                ThrowForStackTrace();
            }
            catch (Exception ex)
            {
                Log.Info("recon r3-5 exception-stacktrace (expect ReconProbes.cs:NN frames): " + ex);
            }
        }

        private static void ThrowForStackTrace()
        {
            throw new InvalidOperationException("r3-5 deliberate test exception");
        }

        // ------------------------------------------------------------------
        // r3-6: fsSerializer vs types defined in a byte[]-loaded assembly.
        //  a) explicit storage type: TrySerialize(typeof(ReconDto), ...) /
        //     TryDeserialize(data, typeof(ReconDto), ...) - no name lookup at all,
        //     fsMetaType just reflects + Activator.CreateInstance's our type.
        //  b) object storage type: writes "$type":"<Type.FullName>" into the data;
        //     deserialize resolves the name via fsTypeCache, which scans
        //     AppDomain.GetAssemblies() + AssemblyLoad events - byte[]-loaded
        //     assemblies DO appear there. BUT on a duplicate load (r3-2) the scan
        //     finds the FIRST-loaded copy's type, so the deserialized instance may
        //     belong to the OLD assembly copy (logged as resolved-same-copy).
        // ------------------------------------------------------------------
        private static void CheckFsSerializer()
        {
            fsSerializer serializer = new fsSerializer();
            try
            {
                var dto = new ReconDto { IntValue = 42, StrValue = "x2coop-neither", Nums = new[] { 7, 8, 9 } };

                // (a) explicit storage type - no name resolution involved
                fsResult ser = serializer.TrySerialize(typeof(ReconDto), dto, out fsData data);
                string json = ser.Succeeded ? fsJsonPrinter.CompressedJson(data) : "<fail>";
                object back = null;
                fsResult des = ser.Succeeded ? serializer.TryDeserialize(data, typeof(ReconDto), ref back)
                                             : fsResult.Fail("skipped (serialize failed)");
                ReconDto rt = back as ReconDto;
                bool fieldsOk = rt != null && rt.IntValue == 42 && rt.StrValue == "x2coop-neither"
                    && rt.Nums != null && rt.Nums.Length == 3 && rt.Nums[2] == 9;
                Log.Info("recon r3-6a explicit-type roundtrip: serialize=" + (ser.Succeeded ? "ok" : "FAIL " + ser.FormattedMessages)
                    + " json=" + json
                    + " deserialize=" + (des.Succeeded ? "ok" : "FAIL " + des.FormattedMessages)
                    + " fields-match=" + fieldsOk);

                // (b) object storage type - forces "$type" name emit + lookup
                fsResult serObj = serializer.TrySerialize(typeof(object), dto, out fsData dataObj);
                string jsonObj = serObj.Succeeded ? fsJsonPrinter.CompressedJson(dataObj) : "<fail>";
                object backObj = null;
                fsResult desObj = serObj.Succeeded ? serializer.TryDeserialize(dataObj, typeof(object), ref backObj)
                                                   : fsResult.Fail("skipped (serialize failed)");
                Assembly self = typeof(X2CoopLifecycle).Assembly;
                string resolved = backObj == null ? "null" : backObj.GetType().FullName;
                bool sameCopy = backObj != null && backObj.GetType().Assembly == self;
                bool objFieldsOk = false;
                try
                {
                    // Read fields WITHOUT casting to our compile-time ReconDto type -
                    // if the name resolved to the OLD copy, `is ReconDto` is false.
                    if (backObj != null)
                    {
                        Type t = backObj.GetType();
                        object iv = t.GetField("IntValue")?.GetValue(backObj);
                        object sv = t.GetField("StrValue")?.GetValue(backObj);
                        objFieldsOk = (int?)iv == 42 && (sv as string) == "x2coop-neither";
                    }
                }
                catch
                {
                }
                Log.Info("recon r3-6b object-typed roundtrip: serialize=" + (serObj.Succeeded ? "ok" : "FAIL " + serObj.FormattedMessages)
                    + " json=" + jsonObj
                    + " deserialize=" + (desObj.Succeeded ? "ok" : "FAIL " + desObj.FormattedMessages)
                    + " resolved=" + resolved
                    + " resolved-asm-hash=" + (backObj == null ? "n/a" : backObj.GetType().Assembly.GetHashCode().ToString())
                    + " resolved-same-copy=" + sameCopy
                    + " fields-match=" + objFieldsOk);
            }
            finally
            {
                try
                {
                    serializer.Dispose();
                }
                catch
                {
                }
            }
        }

        private static string SafeLocation(Assembly asm)
        {
            try
            {
                return asm.Location;
            }
            catch (Exception ex)
            {
                return "<threw " + ex.GetType().Name + ">";
            }
        }
    }

    /// <summary>
    /// r3-3: attribute-based Harmony patch, picked up by the game's own
    /// Harmony.PatchAll(assembly) inside ContentPackState.Patch (line 554) - which
    /// runs BEFORE Create. No manual patcher.Patch call exists for this class; if
    /// "patch-hit attr" appears in the log, attribute patching on a byte[]-loaded
    /// assembly works end to end. Same private target as ProbePatches
    /// (World.ApplyComponentMutation) so both patch styles can be compared on an
    /// identical target.
    /// </summary>
    [HarmonyPatch(typeof(World), "ApplyComponentMutation")]
    internal static class R3AttrProbePatch
    {
        private static bool _hit;

        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                if (_hit)
                {
                    return;
                }
                _hit = true;
                Log.Info("recon r3-3 patch-hit attr Artitas.World.ApplyComponentMutation (applied by game PatchAll)");
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Trivial serializable DTO for the r3-6 fsSerializer round-trip. Public so the
    /// implicit parameterless ctor is public (fsMetaType/fsSerializer instantiate
    /// via Activator.CreateInstance, which needs a public ctor).
    /// </summary>
    public class ReconDto
    {
        // ReSharper disable UnassignedField.Global - fsSerializer populates by reflection
        public int IntValue;
        public string StrValue;
        public int[] Nums;
        // ReSharper restore UnassignedField.Global
    }
}
#endif
