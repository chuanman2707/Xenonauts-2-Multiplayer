using System;
using System.Collections.Generic;
using System.Reflection;

namespace X2Coop.Replication
{
    /// <summary>
    /// T5.3 component-type resolution for DeltaOp.ComponentType (the wire
    /// carries Type.FullName). Resolution order:
    ///
    ///   1. the world's own registered component types (the guest mirror's
    ///      ComponentLookup, fed in via <see cref="MergeWorldTypes"/>) - a
    ///      live component type is ALWAYS registered on the world that
    ///      carries it, so this is the hit path;
    ///   2. <see cref="Type.GetType(string)"/> - resolves assembly-qualified
    ///      names and, for constructed generics, the
    ///      "Ns.Name`K[[Arg,Asm],...]" FullName form directly;
    ///   3. an AppDomain assembly scan: strip the generic argument list,
    ///      resolve the definition name across loaded assemblies, resolve
    ///      each argument recursively, then MakeGenericType. This is the
    ///      documented hole in plain Type.GetType (a FullName whose arg
    ///      types sit in assemblies the loader cannot probe by name).
    ///
    /// Results are cached both ways (_unresolvable is the negative cache -
    /// a name that fails once is dropped immediately on later ops instead
    /// of re-scanning ~40 assemblies per delta).
    ///
    /// Game-free on purpose: the world-type provider is a delegate and the
    /// scan works on plain System.Type, so the xunit suite drives it with
    /// real generic names. Main-thread only; no locking.
    /// </summary>
    internal sealed class ComponentTypeResolver
    {
        private readonly Dictionary<string, Type> _byName =
            new Dictionary<string, Type>(StringComparer.Ordinal);
        private readonly HashSet<string> _unresolvable =
            new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// Merge the world's currently registered component types into the
        /// name map. Called on every bind and lazily on miss (late dynamic
        /// registrations land via FindComponentId, which can register types
        /// this resolver only found by scan - the re-merge keeps the cheap
        /// path canonical). FullName collisions keep the first type seen
        /// (two same-named types would be ambiguous anyway).
        /// </summary>
        internal void MergeWorldTypes(IEnumerable<Type> types)
        {
            if (types == null) { return; }
            foreach (Type t in types)
            {
                if (t == null || t.FullName == null) { continue; }
                if (!_byName.ContainsKey(t.FullName)) { _byName[t.FullName] = t; }
            }
        }

        /// <summary>Resolve a wire FullName to a Type, or null (negative-cached).</summary>
        internal Type Resolve(string fullName, IEnumerable<Type> worldTypes)
        {
            if (string.IsNullOrEmpty(fullName)) { return null; }
            Type t;
            if (_byName.TryGetValue(fullName, out t)) { return t; }
            if (_unresolvable.Contains(fullName)) { return null; }

            // 1. re-merge the world map - a type registered since the last
            //    merge is the most likely miss (dynamic registrations are
            //    rare but real: MutationDelayedItemComponent`2 instances).
            MergeWorldTypes(worldTypes);
            if (_byName.TryGetValue(fullName, out t)) { return t; }

            // 2. Type.GetType handles plain + assembly-qualified names and
            //    most constructed-generic FullNames.
            try { t = Type.GetType(fullName, throwOnError: false); }
            catch { t = null; }
            // 3. assembly scan with a hand-rolled generic-name parser.
            if (t == null) { t = ScanAssemblies(fullName); }
            if (t == null)
            {
                _unresolvable.Add(fullName);
                return null;
            }
            _byName[fullName] = t;
            return t;
        }

        /// <summary>Current cache size (diagnostics).</summary>
        internal int CachedCount { get { return _byName.Count; } }

        /// <summary>Names that failed every path (diagnostics).</summary>
        internal int UnresolvableCount { get { return _unresolvable.Count; } }

        // ------------------------------------------------------------------
        // assembly scan + generic FullName parsing
        // ------------------------------------------------------------------

        /// <summary>
        /// Split "Ns.Name`K[[Arg1],[Arg2]]" into the definition name and the
        /// top-level argument strings. CLR generic names bracket EVERY
        /// argument ("[[A1,A2],[B1]]" - assembly-qualifier commas live inside
        /// an arg's own brackets), so args are collected where bracket depth
        /// closes back to 1; nested generics count deeper. Returns false when
        /// the name carries no argument list.
        /// </summary>
        internal static bool SplitGeneric(string fullName, out string defName,
            out List<string> argNames)
        {
            defName = fullName;
            argNames = null;
            int open = fullName != null ? fullName.IndexOf('[') : -1;
            if (open < 0) { return false; }
            defName = fullName.Substring(0, open);
            argNames = new List<string>();
            int depth = 0;
            int cur = -1;
            for (int i = open; i < fullName.Length; i++)
            {
                char c = fullName[i];
                if (c == '[')
                {
                    depth++;
                    if (depth == 2) { cur = i + 1; } // start of one bracketed arg
                }
                else if (c == ']')
                {
                    if (depth == 2 && cur >= 0)
                    {
                        string a = fullName.Substring(cur, i - cur).Trim();
                        if (a.Length > 0) { argNames.Add(a); }
                        cur = -1;
                    }
                    depth--;
                }
            }
            return argNames.Count > 0;
        }

        private Type ScanAssemblies(string fullName)
        {
            string defName;
            List<string> argNames;
            SplitGeneric(fullName, out defName, out argNames);
            Assembly[] asms;
            try { asms = AppDomain.CurrentDomain.GetAssemblies(); }
            catch { return null; }
            for (int i = 0; i < asms.Length; i++)
            {
                Type def;
                try { def = asms[i].GetType(defName, throwOnError: false); }
                catch { continue; }
                if (def == null) { continue; }
                if (argNames == null || argNames.Count == 0) { return def; }
                if (!def.IsGenericTypeDefinition
                    || def.GetGenericArguments().Length != argNames.Count)
                {
                    continue;
                }
                Type[] args = new Type[argNames.Count];
                bool ok = true;
                for (int a = 0; a < argNames.Count; a++)
                {
                    args[a] = Resolve(argNames[a], null);
                    if (args[a] == null) { ok = false; break; }
                }
                if (!ok) { continue; }
                try { return def.MakeGenericType(args); }
                catch { /* keep scanning - another assembly may carry it */ }
            }
            return null;
        }
    }
}
