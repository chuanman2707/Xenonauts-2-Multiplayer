using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using Artitas;
using Artitas.Components;
using Artitas.Core;
using Artitas.Managers;
using HarmonyLib;
using X2Coop.Mirror;
using X2Coop.Session;

namespace X2Coop.Replication
{
    /// <summary>
    /// T5.1 ReplicationSource (host side, spec section 4 "ReplicationSource" +
    /// section 5 D1): captures every world mutation on the host's live
    /// Strategy world while a co-op session lives, coalesced per rendered
    /// frame, filtered to what save serialization would ever write.
    ///
    /// Postfixes (Harmony chains them; the guest-side MirrorFirewall prefixes
    /// on the same methods can never collide - they are gated on disjoint
    /// world instances):
    ///
    ///   - World.ApplyComponentMutation(int,int,IComponent,IComponent,bool) -
    ///     private non-generic sink every component add/remove/update funnels
    ///     through (immediate via MutateComponentOnEntity, delayed via
    ///     ApplyDelayedMutation; World.cs:213). Records
    ///     (entityID, componentID) -> (added, nextC) into a per-frame
    ///     dictionary, LAST WRITE WINS - the coalescing the spec wants. nextC
    ///     is held by reference, no Copy(): r2 verified components are
    ///     replaced not mutated in place (AddGeoscapeDateTime,
    ///     GeoscapeSchedulerComponent.Copy()->Add,
    ///     GeoscapeMovementSystem.AddPosition), and each record lives only
    ///     until the end-of-frame flush anyway.
    ///
    ///   - World.CreateEntity() + EntityManager.EntityRecyclingFactory
    ///     .Obtain(World) - entity creation. The brief names CreateEntity;
    ///     R5 proved Obtain is the TRUE chokepoint (EntityManager.cs:78):
    ///     CreateEntity delegates to it and the Template factory path bypasses
    ///     CreateEntity entirely (world._entityManager.CreateEntity()). Both
    ///     are patched with id dedup so a CreateEntity-originated create is
    ///     never double-counted - strictly literal coverage plus the R5 gap.
    ///     ForceCreateEntity/Obtain(world,id) (converter resurrection) is NOT
    ///     captured: it only fires inside deserialization, which on the host
    ///     means a save reload - and a mid-session host reload goes through
    ///     host-left-strategy -> resync, not deltas.
    ///     r1 (review I1): the create op alone is HOLLOW for template-spawned
    ///     entities - Template.Create/Create(world,n)/CreateUnsafe install the
    ///     merged set via ComponentManager.AddComponents/CopyInComponents +
    ///     FamilyManager.SetComposition (Template.cs:26416-26489) and
    ///     Entity.CopyTo bulk-loads via AddComponents (Entity.cs:33600) - all
    ///     bypass ApplyComponentMutation. Missions, actors, project-queue
    ///     items and notifications all spawn through Template.Create*, so a
    ///     bare create reaches the guest with ZERO components. Fix (a): at
    ///     DrainFrame each emitted create is enriched with the entity's live
    ///     serializable snapshot (see <see cref="EnrichCreate"/>) - component
    ///     ops flagged <see cref="CapturedOp.FromCreate"/> in the same frame.
    ///
    ///   - World.DeleteEntityImmediately(int) + World.UnsafeDeleteEntity(int) -
    ///     the two delete sinks (World.DeleteEntity's delayed path funnels
    ///     into DeleteEntityImmediately via ApplyDelayedMutation). Recording
    ///     a delete also drops that entity's pending mutations (moot bytes -
    ///     the guest applies the delete either way) and cancels a same-frame
    ///     pending create (created+deleted inside one frame never reached
    ///     the wire). A delete whose id was ALSO re-created this frame (limbo
    ///     pool reuse, R5 EntityRecyclingFactory) keeps both ops - FrameOps
    ///     orders deletes first, then creates, then mutations, so id
    ///     recycling replays correctly on the guest.
    ///
    ///   - NOT patched: generic methods (patch ban), World.Process etc.
    ///
    /// Session gate ("session is Mirroring"): SessionHost.HasGuest - i.e.
    /// accepted handshake through session end, INCLUDING the join-freeze
    /// window. Spec section 6 buffers deltas > baselineSeq until GuestReady;
    /// baselineSeq is snapshot at QueueOnAccept, so capture must already be
    /// live during the freeze or a host command issued mid-join would never
    /// produce a buffered delta. The world term is "the postfix's __instance
    /// IS the live bound StrategyWorld" (ScreenGuard.StrategyWorld - same
    /// WeakReference + Name + Disposed validation the join gate and
    /// SaveStreamer use), so a guest/battle/menu world can never be captured.
    ///
    /// Flush: Runner.LateUpdate -> FlushReplication() -> <see cref="FlushFrame"/>
    /// once per rendered frame, on the Unity main thread (same thread the
    /// world mutates on - spec section 4; R10 proved Update/LateUpdate are
    /// never mid-mutation). The drain applies the EXCLUSION rules mirroring
    /// what WorldState serialization itself writes (WorldState.cs):
    ///   1. type carries [ExcludeFromSerialization]           -> drop (:77)
    ///   2. entity carries ExcludeFromSerializationComponent   -> drop (:131)
    ///   3. component instance: IOptionalSerialization
    ///      .IsSerializable == false                          -> drop (:105)
    ///      (R9's !O bucket - GameObjectComponent is the known case)
    /// Rules 1+2 reuse MirrorFirewall's IsTypeExcluded/EntityExcluded helpers
    /// (promoted to internal) so the two sides can never drift; the dynamic
    /// component-id lookup stays the side-effect-free Lookup.TypeToId read.
    /// Unresolvable componentIDs fail OPEN - counted as `untyped` and kept
    /// (blocking/dropping an op we cannot classify is riskier than
    /// replicating it). Deletes of marker-carrying entities CANNOT be
    /// recognised at flush: the original delete strips the entity's
    /// components before our postfix runs (World.cs:138-165), so a
    /// still-dead id has no marker to read, and a same-frame-recycled id
    /// resolves to the NEW incarnation (r1/M2 - evaluated via
    /// EntityManager.IsAlive, never GetEntity on the dead shell). Deletes
    /// therefore always pass kept - a delete of an entity the guest never
    /// had is an idempotent no-op for T5.3, a wrongly dropped one is a
    /// divergence.
    ///
    /// NOT filtered here (documented T5.2/T5.3 decisions, not T5.1's):
    ///   - guest-local Whitelist types: the whitelist gates what the GUEST
    ///     accepts, not what the host emits (host mutations are all
    ///     authoritative; CampaignPlayTimeComponent/EntityListenerComponent
    ///     replicate fine). Throttling is T5.2's send-layer concern.
    ///   - template-stripped components (WorldState.cs:94 - a component
    ///     Equals() to its template merged value never serializes): needs
    ///     template sparse-merge context per entity.
    ///   - link scrubbing for excluded entities (WorldState.cs:141-184):
    ///     links pointing AT an excluded entity inside kept components are
    ///     host-live references the delta carries as-is; T5.3 maps them.
    ///
    /// Coverage caveats (r1/review I2 - known T5.x limits, also in MODLOG):
    ///   - A component mutated PURELY IN PLACE (a field/property write on a
    ///     live instance with no Add/Remove call through
    ///     ApplyComponentMutation) emits ZERO ops - the sink only sees
    ///     add/remove calls. r2 verified the replicated hot types
    ///     replace-not-mutate, so this is a residual, not a live bug.
    ///   - Direct ComponentManager.AddComponent(s)/RemoveComponent(s)/
    ///     CopyInComponents calls bypass the sink entirely. The replicated
    ///     writers are covered: Template.Create*/CreateUnsafe bulk-install
    ///     is recovered by create enrichment, and any same-key bypass is
    ///     reconciled against live matrix state at flush. REMAINING GAP:
    ///     Entity.CopyTo/Clone-target writes onto an entity that was NOT
    ///     created this frame are still silent (low-risk - observed call
    ///     sites are transient scratch/template usage; a missed overwrite
    ///     heals on the next touching mutation or resync).
    ///
    /// Verify line: while gated, one INFO line per captureLogMs window
    /// (x2coop.captureLogMs, default 1000, 0=off) -
    ///   "capture ops=N muts=M creates=C deletes=D enr=E filt=F untyped=U
    ///    types=A:n,..."
    /// `muts` is the GATED ApplyComponentMutation postfix count (calls on
    /// the bound host world while a session lives - comparable to R9's
    /// probe-window numbers; ungated calls return before counting, so it
    /// is NOT a process-wide raw total). `ops` is the post-filter+coalesce
    /// emitted op count; `enr` is its create-enrichment subset. Paused ->
    /// ops=0 (ESC) or the ~120/s CampaignPlayTimeComponent modal heartbeat;
    /// live speed 1 measured ~873 muts/s, speed 3 ~981-1067 muts/s.
    ///
    /// Fails OPEN everywhere: a throw inside any postfix or the flush logs
    /// once per tag per session (<see cref="Fault"/> - Log.Exception itself
    /// has no rate limiting, and a faulting postfix would otherwise spam
    /// ~1k lines/s) and the mutation already happened (a postfix can't
    /// corrupt game state); a flush fault drops the frame.
    /// Not probe-gated: ships in Release.
    /// </summary>
    public static class ReplicationSource
    {
        /// <summary>Per-second verify line interval (x2coop.captureLogMs, 0 = off).</summary>
        private const int DefaultLogMs = 1000;

        /// <summary>Top-N types shown on the verify line.</summary>
        private const int TopTypeCount = 5;

        // ---- captured op record ------------------------------------------

        /// <summary>Op kinds emitted by the per-frame flush (T5.2 packs them into StateDelta).</summary>
        internal enum OpKind : byte
        {
            Mutation = 0,
            Create = 1,
            Delete = 2,
        }

        /// <summary>
        /// One captured op in a flushed frame. For Mutation ops: ComponentId,
        /// Added (true = add/update, false = removal) and Next (the live
        /// component reference; null on removal). For Create/Delete: EntityId
        /// only. Type is resolved at flush for counting; T5.2 re-resolves for
        /// serialization (Lookup.TypeToId is side-effect-free).
        ///
        /// LIFETIME CONTRACT (r1/review I2 - hard rule for consumers):
        /// every reference reachable from <see cref="FrameOps"/> - the list
        /// itself and each Mutation op's <see cref="Next"/> - points at LIVE
        /// world state and is valid only until the next <see cref="FlushFrame"/>
        /// call (which Clear()s the list and may let the world pool, mutate or
        /// replace the component). T5.2 MUST consume the frame SYNCHRONOUSLY
        /// inside the Runner.LateUpdate -> FlushReplication seam, on the main
        /// thread, in the same rendered frame the ops were flushed: never
        /// defer to send/serialization threads, queues or later frames.
        /// </summary>
        internal sealed class CapturedOp
        {
            public OpKind Kind;
            public int EntityId;
            public int ComponentId;
            public bool Added;
            public IComponent Next;
            /// <summary>
            /// r1/I1: true = this Mutation op is part of the create's INITIAL
            /// component set - the entity's serializable snapshot taken at
            /// drain time, emitted immediately after its Create op in
            /// <see cref="FrameOps"/>. Template.Create*/CreateUnsafe/
            /// CopyInComponents bulk-install bypasses ApplyComponentMutation,
            /// so without enrichment the create would carry zero components.
            /// T5.2 serializes it like any add; T5.3 applies it after the
            /// entity exists. A pending same-frame mutation for the same
            /// (entity, component) supersedes the enriched op (deduped -
            /// the standalone op replays last).
            /// </summary>
            public bool FromCreate;
        }

        /// <summary>Pending per-frame mutation record (dict value).</summary>
        private sealed class MutRec
        {
            public int EntityId;
            public int ComponentId;
            public bool Added;
            public IComponent Next;   // null = removal
        }

        // ---- session-scoped counters (Reset on session end) ---------------
        /// <summary>ApplyComponentMutation postfix hits recorded (gated call count on the bound host world - the R9 probe-window-comparable number; ungated calls return before counting).</summary>
        public static long Mutations;
        /// <summary>Create postfix hits (deduped per id).</summary>
        public static long Creates;
        /// <summary>Delete postfix hits.</summary>
        public static long Deletes;
        /// <summary>Ops that passed the flush filter into <see cref="FrameOps"/> (includes the FromCreate enrichment ops).</summary>
        public static long Emitted;
        /// <summary>Ops dropped by [ExcludeFromSerialization] on the type.</summary>
        public static long FilteredType;
        /// <summary>Ops dropped by ExcludeFromSerializationComponent on the entity.</summary>
        public static long FilteredEntity;
        /// <summary>Ops dropped by IOptionalSerialization.IsSerializable == false on the instance.</summary>
        public static long FilteredOptOut;
        /// <summary>Mutations dropped because their entity was deleted in the same frame.</summary>
        public static long DroppedDead;
        /// <summary>Mutations whose componentID did not resolve to a Type (kept - cannot classify).</summary>
        public static long Untyped;
        /// <summary>Component ops emitted as a create's initial serializable snapshot (r1/I1 enrichment).</summary>
        public static long EnrichedComponents;
        /// <summary>Deletes whose entity id was alive again at flush - i.e. re-issued from the limbo pool inside the same frame (the matching create op is also in the frame; deletes emit first).</summary>
        public static long RecycledDeletes;
        /// <summary>Mutation ops whose recorded intent was reconciled against live matrix state at flush (bypass writes made the record stale).</summary>
        public static long Reconciled;

        // ---- per-frame pending state (main thread only) -------------------
        private static readonly Dictionary<long, MutRec> _muts = new Dictionary<long, MutRec>();
        private static readonly List<int> _creates = new List<int>();
        private static readonly List<int> _deletes = new List<int>();
        private static readonly HashSet<int> _createIds = new HashSet<int>();
        private static readonly List<long> _prune = new List<long>();
        private static readonly List<CapturedOp> _frameOps = new List<CapturedOp>();
        private static WeakReference<World> _hostWorld;   // fast-path cache; weak - never pins a dead world
        // r1/M1: GetHashCode() CAN be negative (live-observed -408538096), so
        // the old `_loggedHash >= 0` sentinel was dead code - "released" never
        // logged and HasSessionState never saw the bound flag. Bound state is
        // a real bool now; _loggedHash only de-dupes the bound log line.
        private static bool _boundLogged;
        private static int _loggedHash;

        /// <summary>Once-per-tag-per-session postfix/flush fault log guard (r1/M3).</summary>
        private static readonly HashSet<string> _faultLogged =
            new HashSet<string>(StringComparer.Ordinal);

        // ---- per-second accumulator ---------------------------------------
        private static readonly Stopwatch _secWatch = new Stopwatch();
        private static long _secMutCalls;
        private static long _secOps;
        private static long _secCreates;
        private static long _secDeletes;
        private static long _secEnr;
        private static long _secFilt;
        private static long _secUntyped;
        private static readonly Dictionary<string, int> _secTypes =
            new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>
        /// The flushed ops of the most recent frame (T5.2 consume seam):
        /// deletes, then for each create the Create op followed by its
        /// FromCreate initial-component Mutation ops, then the remaining
        /// mutations (id-recycle safe order). Cleared at the next
        /// <see cref="FlushFrame"/>. Exposed for T5.2 - the list and its
        /// Next references die at session end via <see cref="Reset"/>.
        /// LIFETIME: live only until the next FlushFrame - consume
        /// synchronously inside the FlushReplication seam (see CapturedOp).
        /// </summary>
        internal static List<CapturedOp> FrameOps => _frameOps;

        /// <summary>Resolve + patch the four capture points. Called from X2CoopLifecycle.Create.</summary>
        public static void Apply(Harmony patcher)
        {
            if (patcher == null)
            {
                Log.Warn("capture: Apply called with null patcher");
                return;
            }
            PatchOn(patcher, typeof(World), "ApplyComponentMutation",
                new[] { typeof(int), typeof(int), typeof(IComponent), typeof(IComponent), typeof(bool) },
                nameof(MutPostfix));
            PatchOn(patcher, typeof(World), "CreateEntity", null,
                nameof(CreatePostfix));
            PatchOn(patcher, typeof(EntityManager.EntityRecyclingFactory), "Obtain",
                new[] { typeof(World) }, nameof(ObtainPostfix));
            PatchOn(patcher, typeof(World), "DeleteEntityImmediately", new[] { typeof(int) },
                nameof(DeletePostfix));
            PatchOn(patcher, typeof(World), "UnsafeDeleteEntity", new[] { typeof(int) },
                nameof(DeletePostfix));
        }

        private static void PatchOn(Harmony patcher, Type targetType, string name,
            Type[] args, string postfixName)
        {
            string tag = targetType.FullName + "." + name;
            try
            {
                MethodInfo original = args != null
                    ? AccessTools.Method(targetType, name, args)
                    : AccessTools.Method(targetType, name);
                if (original == null && args != null)
                {
                    original = AccessTools.Method(targetType, name);
                }
                MethodInfo postfix = AccessTools.Method(typeof(ReplicationSource), postfixName);
                if (original == null || postfix == null)
                {
                    Log.Warn("capture: method/postfix not found: " + tag + " / " + postfixName);
                    return;
                }
                patcher.Patch(original, postfix: new HarmonyMethod(postfix));
                Log.Info("capture: patch-applied " + tag + " (postfix)");
            }
            catch (Exception ex)
            {
                Log.Exception("capture patch " + tag, ex);
            }
        }

        /// <summary>
        /// Per-session + pack-teardown reset (SessionHost.ClearGuest,
        /// Teardown.Run, and the lazy session-end path in FlushFrame): drops
        /// every pending op, the bound-world cache and all counters. No live
        /// component (Next) or world reference may survive session end -
        /// same-assembly re-enable keeps these statics (R3).
        /// </summary>
        public static void Reset()
        {
            if (_boundLogged)
            {
                Log.Info("capture: released host StrategyWorld (emitted=" + Emitted + ")");
            }
            _muts.Clear();
            _creates.Clear();
            _deletes.Clear();
            _createIds.Clear();
            _frameOps.Clear();
            _hostWorld = null;
            _boundLogged = false;
            _loggedHash = 0;
            _faultLogged.Clear();
            Mutations = 0;
            Creates = 0;
            Deletes = 0;
            Emitted = 0;
            FilteredType = 0;
            FilteredEntity = 0;
            FilteredOptOut = 0;
            DroppedDead = 0;
            Untyped = 0;
            EnrichedComponents = 0;
            RecycledDeletes = 0;
            Reconciled = 0;
            _secWatch.Reset();
            ClearSecond();
        }

        private static void ClearSecond()
        {
            _secMutCalls = 0;
            _secOps = 0;
            _secCreates = 0;
            _secDeletes = 0;
            _secEnr = 0;
            _secFilt = 0;
            _secUntyped = 0;
            _secTypes.Clear();
        }

        /// <summary>
        /// r1/M3: Log.Exception has NO rate limiting - a faulting postfix or
        /// flush would emit one ERROR line per mutation/frame (~1k/s). Log
        /// the first occurrence per tag per session; <see cref="Reset"/>
        /// clears the set so a fault that persists into the next session is
        /// reported once again.
        /// </summary>
        private static void Fault(string tag, Exception ex)
        {
            if (_faultLogged.Add(tag))
            {
                Log.Exception("capture " + tag, ex);
            }
        }

        // ------------------------------------------------------------------
        // gate + postfixes - all fail OPEN
        // ------------------------------------------------------------------

        /// <summary>
        /// The capture gate: true only while a guest holds the session slot
        /// AND __instance is the live bound host StrategyWorld. Fast path is
        /// one bool + one weak-ref compare; the slow path re-resolves through
        /// ScreenGuard (which re-validates Name and Disposed) so a
        /// dispose/recreate transition re-binds automatically.
        /// </summary>
        private static bool Gated(World w)
        {
            if (w == null || !SessionHost.HasGuest) { return false; }
            World cached = null;
            WeakReference<World> wr = _hostWorld;
            if (wr != null)
            {
                try { wr.TryGetTarget(out cached); } catch { }
            }
            if (cached != null && ReferenceEquals(w, cached))
            {
                return !w.Disposed;
            }
            World host = ScreenGuard.StrategyWorld;
            if (host == null || !ReferenceEquals(w, host)) { return false; }
            _hostWorld = new WeakReference<World>(host);
            int h = host.GetHashCode();
            // r1/M1: bound state is _boundLogged (bool) - GetHashCode can be
            // negative, so `hash != sentinel` cannot carry the flag.
            if (!_boundLogged || h != _loggedHash)
            {
                _boundLogged = true;
                _loggedHash = h;
                Log.Info("capture: bound host StrategyWorld hash=" + h);
            }
            return true;
        }

        /// <summary>World.ApplyComponentMutation - the component add/remove/update sink.</summary>
        private static void MutPostfix(World __instance, int entityID,
            int componentID, IComponent nextC, bool added)
        {
            try
            {
                if (!Gated(__instance)) { return; }
                Mutations++;
                _secMutCalls++;
                long key = ((long)entityID << 32) | (uint)componentID;
                MutRec r;
                if (_muts.TryGetValue(key, out r))
                {
                    // last write wins: keep the newest (added, nextC) pair.
                    r.Added = added;
                    r.Next = nextC;
                    return;
                }
                _muts[key] = new MutRec
                {
                    EntityId = entityID,
                    ComponentId = componentID,
                    Added = added,
                    Next = nextC,
                };
            }
            catch (Exception ex)
            {
                Fault("mut-postfix", ex);
            }
        }

        /// <summary>World.CreateEntity postfix - return-value id. Chokepoint is Obtain (also patched).</summary>
        private static void CreatePostfix(World __instance, Entity __result)
        {
            try
            {
                RecordCreate(__instance, __result);
            }
            catch (Exception ex)
            {
                Fault("create-postfix", ex);
            }
        }

        /// <summary>
        /// EntityRecyclingFactory.Obtain(World) postfix - R5's true create
        /// chokepoint (covers CreateEntity + the Template factory bypass).
        /// __0 is the world arg; __result is the fresh entity.
        /// </summary>
        private static void ObtainPostfix(World __0, Entity __result)
        {
            try
            {
                RecordCreate(__0, __result);
            }
            catch (Exception ex)
            {
                Fault("obtain-postfix", ex);
            }
        }

        /// <summary>Id-deduped create record shared by both create patches.</summary>
        private static void RecordCreate(World world, Entity entity)
        {
            if (entity == null || !Gated(world)) { return; }
            if (!_createIds.Add(entity.ID)) { return; }
            Creates++;
            _creates.Add(entity.ID);
        }

        /// <summary>DeleteEntityImmediately / UnsafeDeleteEntity postfix.</summary>
        private static void DeletePostfix(World __instance, int entityID)
        {
            try
            {
                if (!Gated(__instance)) { return; }
                if (_creates.Remove(entityID))
                {
                    // Created and deleted inside the same frame: the entity
                    // never reached the wire - cancel the pair entirely.
                    _createIds.Remove(entityID);
                    Creates--; // balance the raw count (the op is discarded)
                }
                else
                {
                    Deletes++;
                    _deletes.Add(entityID);
                }
                // Pending mutations of a now-dead entity are moot - the guest
                // applies the delete either way. Drop them (also keeps a
                // recycled id's later mutations from merging into the dead
                // entity's record via the same dict key).
                if (_muts.Count > 0)
                {
                    _prune.Clear();
                    foreach (KeyValuePair<long, MutRec> kv in _muts)
                    {
                        if (kv.Value.EntityId == entityID) { _prune.Add(kv.Key); }
                    }
                    for (int i = 0; i < _prune.Count; i++)
                    {
                        _muts.Remove(_prune[i]);
                        DroppedDead++;
                    }
                }
            }
            catch (Exception ex)
            {
                Fault("delete-postfix", ex);
            }
        }

        // ------------------------------------------------------------------
        // per-frame flush (Runner.LateUpdate seam) + per-second verify line
        // ------------------------------------------------------------------

        /// <summary>
        /// Runner.LateUpdate -> FlushReplication(): drain the frame's pending
        /// ops through the exclusion filter into <see cref="FrameOps"/> and
        /// pump the per-second verify line. Also the lazy session-end reset:
        /// when the session slot frees or the host leaves Strategy, pending
        /// component refs are dropped within a frame instead of surviving to
        /// teardown.
        /// </summary>
        internal static void FlushFrame()
        {
            try
            {
                if (!SessionHost.HasGuest)
                {
                    if (HasSessionState()) { Reset(); }
                    return;
                }
                World w = HostWorld();
                if (w == null)
                {
                    // Host left Strategy mid-session: no world, nothing can
                    // accumulate - but drop any stragglers immediately.
                    if (HasSessionState()) { Reset(); }
                    return;
                }
                DrainFrame(w);
                PumpSecondLog();
            }
            catch (Exception ex)
            {
                Fault("flush", ex);
            }
        }

        private static bool HasSessionState()
        {
            return _boundLogged || _muts.Count > 0 || _creates.Count > 0
                || _deletes.Count > 0 || _frameOps.Count > 0 || _hostWorld != null;
        }

        private static World HostWorld()
        {
            try { return ScreenGuard.StrategyWorld; }
            catch { return null; }
        }

        /// <summary>
        /// Apply the exclusion filter to the pending frame and emit the kept
        /// ops into <see cref="_frameOps"/> (deletes, then creates - each
        /// followed by its FromCreate enrichment ops - then mutations).
        /// Caller holds a live gated world.
        /// </summary>
        private static void DrainFrame(World w)
        {
            _frameOps.Clear();

            for (int i = 0; i < _deletes.Count; i++)
            {
                int eid = _deletes[i];
                // r1/M2: evaluate the delete against LIVENESS, never the
                // entity marker. The original delete stripped the entity's
                // components before our postfix ran (World.cs:138-165), so a
                // still-dead id resolves to a bare shell (composition 0 -
                // the marker is already gone) and a same-frame-recycled id
                // resolves to the NEW incarnation - whose marker says
                // nothing about the DELETED one. Neither can answer "was
                // the deleted entity excluded?", so deletes always pass
                // kept: a delete of an entity the guest never had is an
                // idempotent no-op for T5.3, while a wrongly dropped delete
                // of a replicated entity is a divergence.
                try
                {
                    if (w.EntityManager != null && w.EntityManager.IsAlive(eid))
                    {
                        // The id was re-issued from the limbo pool inside
                        // the same frame - the matching create op is also
                        // in this frame (Obtain postfix) and replays right
                        // after this delete. Counted for visibility.
                        RecycledDeletes++;
                    }
                }
                catch { }
                Emitted++;
                _secOps++;
                _secDeletes++;
                _frameOps.Add(new CapturedOp { Kind = OpKind.Delete, EntityId = eid });
            }

            for (int i = 0; i < _creates.Count; i++)
            {
                int eid = _creates[i];
                // The entity marker IS evaluable here - the entity is live
                // at flush, so a same-frame ExcludeFromSerializationComponent
                // add drops the create AND its enrichment set together.
                if (MirrorFirewall.EntityExcluded(w, eid))
                {
                    FilteredEntity++;
                    _secFilt++;
                    continue;
                }
                Emitted++;
                _secOps++;
                _secCreates++;
                _frameOps.Add(new CapturedOp { Kind = OpKind.Create, EntityId = eid });
                EnrichCreate(w, eid);
            }

            foreach (MutRec r in _muts.Values)
            {
                Type type = null;
                try { type = w.ComponentManager?.FindComponentType(r.ComponentId); }
                catch { }
                if (type == null)
                {
                    // Unclassifiable - fail open: kept and counted (R9 saw ~0).
                    Untyped++;
                    _secUntyped++;
                }
                else if (MirrorFirewall.IsTypeExcluded(type))
                {
                    FilteredType++;
                    _secFilt++;
                    continue;
                }
                else if (MirrorFirewall.EntityExcluded(w, r.EntityId))
                {
                    FilteredEntity++;
                    _secFilt++;
                    continue;
                }
                // r1: reconcile the recorded intent against LIVE matrix
                // state before emitting. ComponentManager.AddComponents/
                // RemoveComponent/CopyInComponents writes bypass the sink
                // (Template.Create*/Entity.CopyTo - the same gap I1
                // enriches creates for), so a recorded add may have been
                // removed again (or vice versa) without ever re-touching
                // the postfix. Emitting what the matrix actually holds at
                // flush keeps the frame a true snapshot of reality; a bad
                // index lookup falls back to the recorded (added, nextC).
                IComponent live = r.Next;
                try
                {
                    if (w.ComponentManager != null)
                    {
                        live = w.ComponentManager.GetComponent(r.EntityId, r.ComponentId);
                    }
                }
                catch { }
                if (live == null ? r.Added : !r.Added || !ReferenceEquals(live, r.Next))
                {
                    // added flip, or a newer instance than the recorded one -
                    // the sink's record was superseded by a bypassing write.
                    Reconciled++;
                }
                if (live is IOptionalSerialization opt && !opt.IsSerializable)
                {
                    // WorldState.cs:105 - the per-instance serialization
                    // opt-out (R9's !O bucket; GameObjectComponent). Checked
                    // on the LIVE instance; a removal (live == null) never
                    // matches `is`, so it skips this check naturally.
                    FilteredOptOut++;
                    _secFilt++;
                    continue;
                }
                Emitted++;
                _secOps++;
                if (type != null)
                {
                    string tn = type.Name;
                    _secTypes.TryGetValue(tn, out int c);
                    _secTypes[tn] = c + 1;
                }
                _frameOps.Add(new CapturedOp
                {
                    Kind = OpKind.Mutation,
                    EntityId = r.EntityId,
                    ComponentId = r.ComponentId,
                    Added = live != null,
                    Next = live,
                });
            }

            _muts.Clear();
            _creates.Clear();
            _deletes.Clear();
            _createIds.Clear();
            // NOTE for T5.2: _frameOps now holds this frame's coalesced delta
            // (live until the next FlushFrame - consume synchronously).
        }

        /// <summary>
        /// r1/I1: snapshot the created entity's CURRENT serializable
        /// component set into <see cref="_frameOps"/> as Mutation ops flagged
        /// <see cref="CapturedOp.FromCreate"/>, emitted immediately after its
        /// Create op. Template.Create/Create(world,n)/CreateUnsafe install
        /// the merged set via ComponentManager.AddComponents/CopyInComponents
        /// + FamilyManager.SetComposition (Template.cs:26416-26489) -
        /// bypassing ApplyComponentMutation - so without this the create op
        /// is hollow (missions, actors, project-queue items, notifications).
        ///
        /// Enumeration mirrors what serialization/CopyTo read: the family
        /// composition bit set (World.FamilyManager.GetRawComposition ->
        /// Entity.ComponentBits source) + the live matrix slot per
        /// component id (ComponentManager.GetComponent). The snapshot is
        /// taken at FLUSH time, so it also captures same-frame post-create
        /// writes that never hit _muts; a (entity,component) pair already
        /// pending in _muts is skipped here - the standalone op replays
        /// last in the same frame and wins. A dead-at-flush id resolves to
        /// composition 0 (empty) - the create then stays hollow, which is
        /// still the correct replay. Same exclusion rules as mutations:
        /// [ExcludeFromSerialization] type and IOptionalSerialization
        /// opt-out are dropped (the entity-level marker check already ran
        /// on the create itself).
        /// </summary>
        private static void EnrichCreate(World w, int eid)
        {
            try
            {
                BitSet bits = null;
                try { bits = w.FamilyManager?.GetRawComposition(eid); }
                catch { }
                if (bits == null) { return; }
                bool dump = DebugSettings.GetInt("captureCreateDump", 0) != 0;
                int emitted = 0;
                List<string> names = dump ? new List<string>(12) : null;
                foreach (int compId in bits.IterateIndices())
                {
                    try
                    {
                        // A pending standalone mutation for the same
                        // (entity, component) supersedes the initial value -
                        // it emits in the mutations section and replays
                        // after this create's ops in the same frame.
                        if (_muts.ContainsKey(((long)eid << 32) | (uint)compId))
                        {
                            continue;
                        }
                        Type type = null;
                        try { type = w.ComponentManager?.FindComponentType(compId); }
                        catch { }
                        if (type == null)
                        {
                            // Unresolvable id - fail open like the muts path.
                            Untyped++;
                            _secUntyped++;
                        }
                        else if (MirrorFirewall.IsTypeExcluded(type))
                        {
                            FilteredType++;
                            _secFilt++;
                            continue;
                        }
                        IComponent comp = null;
                        try { comp = w.ComponentManager?.GetComponent(eid, compId); }
                        catch { }
                        if (comp == null) { continue; } // bit set but slot empty - nothing live to send
                        if (comp is IOptionalSerialization opt && !opt.IsSerializable)
                        {
                            FilteredOptOut++;
                            _secFilt++;
                            continue;
                        }
                        Emitted++;
                        _secOps++;
                        _secEnr++;
                        EnrichedComponents++;
                        emitted++;
                        if (type != null)
                        {
                            string tn = type.Name;
                            _secTypes.TryGetValue(tn, out int c);
                            _secTypes[tn] = c + 1;
                            if (dump && names.Count < 12) { names.Add(tn); }
                        }
                        _frameOps.Add(new CapturedOp
                        {
                            Kind = OpKind.Mutation,
                            EntityId = eid,
                            ComponentId = compId,
                            Added = true,
                            Next = comp,
                            FromCreate = true,
                        });
                    }
                    catch (Exception ex)
                    {
                        Fault("create-enrich-comp", ex);
                    }
                }
                if (dump)
                {
                    Log.Info("capture: create+enrich eid=" + eid + " comps=" + emitted
                        + (names != null && names.Count > 0
                            ? " [" + string.Join(",", names.ToArray())
                              + (emitted > names.Count ? ",..." : string.Empty) + "]"
                            : string.Empty));
                }
            }
            catch (Exception ex)
            {
                Fault("create-enrich", ex);
            }
        }

        /// <summary>
        /// The per-second verify accumulator - emits the "capture ops=..."
        /// line every captureLogMs while gated (even when ops=0: the paused
        /// verify needs the explicit zero line).
        /// </summary>
        private static void PumpSecondLog()
        {
            int ms = DebugSettings.GetInt("captureLogMs", DefaultLogMs);
            if (ms <= 0)
            {
                if (_secWatch.IsRunning) { _secWatch.Reset(); }
                ClearSecond();
                return;
            }
            if (!_secWatch.IsRunning)
            {
                _secWatch.Restart();
                return;
            }
            if (_secWatch.ElapsedMilliseconds < ms) { return; }
            _secWatch.Restart();
            Log.Info("capture ops=" + _secOps
                + " muts=" + _secMutCalls
                + " creates=" + _secCreates
                + " deletes=" + _secDeletes
                + " enr=" + _secEnr
                + " filt=" + _secFilt
                + " untyped=" + _secUntyped
                + " types=" + TopTypes());
            ClearSecond();
        }

        /// <summary>"A:n,B:n,..." top-5 kept-mutation types by op count this window ("-" when empty).</summary>
        private static string TopTypes()
        {
            if (_secTypes.Count == 0) { return "-"; }
            var sb = new StringBuilder();
            foreach (KeyValuePair<string, int> kv in _secTypes
                .OrderByDescending(kv => kv.Value).Take(TopTypeCount))
            {
                if (sb.Length > 0) { sb.Append(','); }
                sb.Append(kv.Key).Append(':').Append(kv.Value);
            }
            return sb.ToString();
        }
    }
}
