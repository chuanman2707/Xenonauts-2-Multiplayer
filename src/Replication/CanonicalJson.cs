using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FullSerializer;

namespace X2Coop.Replication
{
    /// <summary>
    /// One hashed serialization record: a single serializable component on a
    /// single (host-space) entity, canonicalized to a byte-stable string.
    /// <see cref="EntityId"/> is always the HOST id (the guest side maps its
    /// entities through EntityIdMap before hashing - spec T5.5).
    /// </summary>
    internal sealed class HashItem
    {
        internal int EntityId;
        internal string TypeName;   // registered component type FullName
        internal string Canon;      // canonical serialization (CanonicalJson.Write)
        internal ulong Fingerprint; // FNV-1a-64 over the canonical record line
    }

    /// <summary>
    /// T5.5 canonical serialization writer for FullSerializer fsData trees.
    ///
    /// Why not fsJsonPrinter: CompressedJson emits dictionary members in
    /// Dictionary&lt;string,fsData&gt; enumeration order (insertion order in
    /// practice, but an implementation detail - and the guest's tree is built
    /// by the deserializer, whose member order need not match the host's live
    /// object). Hashing byte output that depends on member order would flag
    /// order-only differences as desyncs. The canonical form therefore:
    ///
    ///   - sorts every object's members by key (StringComparer.Ordinal);
    ///   - tags scalars by fsData kind so "1" (string) != 1 (int) != 1.0
    ///     (double);
    ///   - normalizes numbers through the WIRE representation so values
    ///     compare equal iff the wire carries them equal:
    ///       int64:  fsJsonParser keeps integers lossless (digit fast-path +
    ///               long.TryParse) - emit the raw value.
    ///       double: printed as ConvertDoubleToString (= d.ToString) then
    ///               re-parsed with NumberStyles.Float + InvariantCulture -
    ///               the exact value the deserializer reconstructs on the
    ///               guest. NaN/Infinity get fixed tokens (they cannot round
    ///               trip through fsJsonParser unless the parser config
    ///               allows specials - treated as opaque but deterministic).
    ///       float:  same treatment through ToString/Parse, then cast back
    ///               to float before hashing the IEEE-754 bits (the guest's
    ///               deserialized float went through exactly this detour).
    ///   - escapes strings deterministically: '"' and '\' escaped, control
    ///     chars and non-ASCII as \uXXXX (UTF-16 code units), everything else
    ///     literal ASCII.
    ///   - arrays keep their order (order IS semantic).
    ///
    /// Canonical alphabet (first char is the tag):
    ///   N             null
    ///   B0 / B1       false / true
    ///   I&lt;dec&gt;    Int64
    ///   D&lt;hex16&gt;  Double bits after wire normalization (D!N / D!+I / D!-I
    ///                 for NaN / +Infinity / -Infinity)
    ///   F&lt;hex8&gt;   Float bits after wire normalization (F!N / F!+I / F!-I)
    ///   "…"           string (escaped)
    ///   [v,v,…]       array
    ///   {"k":v,…}     object, keys ordinal-sorted
    ///
    /// The world digest is SHA-256 over "&lt;entityId&gt;|&lt;typeName&gt;|&lt;canon&gt;\n"
    /// lines for every item sorted by (EntityId, TypeName ordinal). Each item
    /// also carries an FNV-1a-64 fingerprint of the same line - cheap per-item
    /// identity for dump-file diffing without shipping full canonical text.
    ///
    /// Game-free: only FullSerializer.fsData is touched, so the xunit suite
    /// links this file directly (assembly Assembly-CSharp-firstpass, no
    /// UnityEngine / Artitas dependency).
    /// </summary>
    internal static class CanonicalJson
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);

        // ------------------------------------------------------------------
        // canonical writer
        // ------------------------------------------------------------------

        /// <summary>Canonical string for one fsData tree.</summary>
        internal static string Write(fsData data)
        {
            var sb = new StringBuilder(256);
            Write(data, sb);
            return sb.ToString();
        }

        /// <summary>Append the canonical form of <paramref name="data"/>.</summary>
        internal static void Write(fsData data, StringBuilder sb)
        {
            if (data == null || sb == null) { return; }
            switch (data.Type)
            {
                case fsDataType.Null:
                    sb.Append('N');
                    return;
                case fsDataType.Boolean:
                    sb.Append(data.AsBool ? "B1" : "B0");
                    return;
                case fsDataType.Int64:
                    // fsJsonParser is lossless for Int64 (digit fast-path /
                    // long.TryParse) - the raw value IS the wire value.
                    sb.Append('I').Append(data.AsInt64.ToString(CultureInfo.InvariantCulture));
                    return;
                case fsDataType.Double:
                    WriteDouble(sb, NormDouble(data.AsDouble));
                    return;
                case fsDataType.Float:
                    WriteFloat(sb, NormFloat(data.AsFloat));
                    return;
                case fsDataType.String:
                    WriteString(sb, data.AsString);
                    return;
                case fsDataType.Array:
                {
                    List<fsData> list = data.AsList;
                    sb.Append('[');
                    if (list != null)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            if (i > 0) { sb.Append(','); }
                            Write(list[i], sb);
                        }
                    }
                    sb.Append(']');
                    return;
                }
                case fsDataType.Object:
                {
                    Dictionary<string, fsData> dict = data.AsDictionary;
                    sb.Append('{');
                    if (dict != null && dict.Count > 0)
                    {
                        var keys = new List<string>(dict.Keys);
                        keys.Sort(StringComparer.Ordinal);
                        for (int i = 0; i < keys.Count; i++)
                        {
                            if (i > 0) { sb.Append(','); }
                            WriteString(sb, keys[i]);
                            sb.Append(':');
                            Write(dict[keys[i]], sb);
                        }
                    }
                    sb.Append('}');
                    return;
                }
                default:
                    sb.Append('N');
                    return;
            }
        }

        /// <summary>
        /// Normalize a double through the exact wire representation:
        /// fsJsonPrinter writes d.ToString(InvariantCulture) (a trailing ".0"
        /// is appended for integral values - parse-neutral), the guest parses
        /// it back with NumberStyles.Float/InvariantCulture. Whatever the wire
        /// can express is what both sides end up with, so hashing the
        /// RE-PARSED value makes "wire-equal" exactly "hash-equal".
        /// </summary>
        private static double NormDouble(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) { return v; }
            double r;
            if (double.TryParse(v.ToString(CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out r))
            {
                return r;
            }
            return v; // never expected; keep raw rather than corrupt
        }

        /// <summary>Same wire normalization for floats (double detour, cast back).</summary>
        private static float NormFloat(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) { return v; }
            double r;
            if (double.TryParse(v.ToString(CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out r))
            {
                return (float)r;
            }
            return v;
        }

        private static void WriteDouble(StringBuilder sb, double v)
        {
            if (double.IsNaN(v)) { sb.Append("D!N"); return; }
            if (double.IsPositiveInfinity(v)) { sb.Append("D!+I"); return; }
            if (double.IsNegativeInfinity(v)) { sb.Append("D!-I"); return; }
            sb.Append('D');
            sb.Append(BitConverter.DoubleToInt64Bits(v).ToString("x16", CultureInfo.InvariantCulture));
        }

        private static void WriteFloat(StringBuilder sb, float v)
        {
            if (float.IsNaN(v)) { sb.Append("F!N"); return; }
            if (float.IsPositiveInfinity(v)) { sb.Append("F!+I"); return; }
            if (float.IsNegativeInfinity(v)) { sb.Append("F!-I"); return; }
            sb.Append('F');
            sb.Append(FloatToBits(v).ToString("x8", CultureInfo.InvariantCulture));
        }

        /// <summary>IEEE-754 bits of a float (endianness-safe BitConverter path).</summary>
        private static uint FloatToBits(float v)
        {
            return BitConverter.ToUInt32(BitConverter.GetBytes(v), 0);
        }

        /// <summary>
        /// Canonical string literal: '"' and '\' escaped, controls and
        /// non-ASCII as \uXXXX (UTF-16 code units - a surrogate pair becomes
        /// two escapes; the reader below reassembles the code units, so a
        /// round trip preserves any well-formed string).
        /// </summary>
        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    if (c == '"' || c == '\\')
                    {
                        sb.Append('\\').Append(c);
                    }
                    else if (c < 0x20 || c > 0x7E)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
            }
            sb.Append('"');
        }

        // ------------------------------------------------------------------
        // items: record line, sort, fingerprints, digest
        // ------------------------------------------------------------------

        /// <summary>Canonical hash-input line for one item.</summary>
        internal static string RecordLine(int entityId, string typeName, string canon)
        {
            return entityId.ToString(CultureInfo.InvariantCulture) + "|"
                + (typeName ?? string.Empty) + "|" + (canon ?? string.Empty) + "\n";
        }

        /// <summary>Sort by (host entity id, type FullName ordinal).</summary>
        internal static void Sort(List<HashItem> items)
        {
            if (items == null || items.Count < 2) { return; }
            items.Sort((a, b) => a.EntityId != b.EntityId
                ? a.EntityId.CompareTo(b.EntityId)
                : string.CompareOrdinal(a.TypeName, b.TypeName));
        }

        /// <summary>FNV-1a 64-bit over the UTF-8 bytes of the record line.</summary>
        internal static ulong Fingerprint(int entityId, string typeName, string canon)
        {
            return Fnv1a64(RecordLine(entityId, typeName, canon));
        }

        /// <summary>FNV-1a 64-bit over UTF-8 bytes (deterministic, dependency-free).</summary>
        internal static ulong Fnv1a64(string s)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            byte[] bytes = Utf8.GetBytes(s ?? string.Empty);
            ulong h = offset;
            for (int i = 0; i < bytes.Length; i++)
            {
                h ^= bytes[i];
                h *= prime;
            }
            return h;
        }

        /// <summary>Per-item fingerprints, then SHA-256 over the sorted record lines.</summary>
        internal static byte[] Digest(List<HashItem> items)
        {
            for (int i = 0; i < items.Count; i++)
            {
                HashItem it = items[i];
                it.Fingerprint = Fingerprint(it.EntityId, it.TypeName, it.Canon);
            }
            using (SHA256 sha = SHA256.Create())
            {
                for (int i = 0; i < items.Count; i++)
                {
                    byte[] bytes = Utf8.GetBytes(RecordLine(
                        items[i].EntityId, items[i].TypeName, items[i].Canon));
                    sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return sha.Hash;
            }
        }

        /// <summary>Lowercase hex (no separators).</summary>
        internal static string Hex(byte[] bytes)
        {
            if (bytes == null) { return string.Empty; }
            var sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // first-difference diagnostics (field-level "path" into the tree)
        // ------------------------------------------------------------------

        /// <summary>
        /// First difference between two canonical strings, expressed as a
        /// dotted member path plus the differing lexemes, or null when equal.
        /// Both inputs must be canonical (the writer above produces them);
        /// anything unparseable degrades to a length/position note, never a
        /// throw.
        /// </summary>
        internal static string Diff(string hostCanon, string guestCanon)
        {
            if (string.Equals(hostCanon, guestCanon, StringComparison.Ordinal))
            {
                return null;
            }
            Node a = Parse(hostCanon);
            Node b = Parse(guestCanon);
            if (a == null || b == null)
            {
                int n = Math.Min(hostCanon != null ? hostCanon.Length : 0,
                    guestCanon != null ? guestCanon.Length : 0);
                int pos = 0;
                while (pos < n && hostCanon[pos] == guestCanon[pos]) { pos++; }
                return "(unparseable) first-diff at char " + pos
                    + " of " + (hostCanon != null ? hostCanon.Length : -1)
                    + "/" + (guestCanon != null ? guestCanon.Length : -1);
            }
            var path = new StringBuilder(64);
            var detail = new StringBuilder(96);
            DiffNode(a, b, path, detail);
            return detail.Length > 0 ? detail.ToString() : "equal";
        }

        private static bool DiffNode(Node a, Node b, StringBuilder path, StringBuilder detail)
        {
            if (a.Kind != b.Kind)
            {
                detail.Append(path).Append(": kind " + KindName(a.Kind)
                    + " != " + KindName(b.Kind));
                return true;
            }
            if (a.Kind == 0)
            {
                if (!string.Equals(a.Scalar, b.Scalar, StringComparison.Ordinal))
                {
                    detail.Append(path).Append(": " + Snip(a.Scalar)
                        + " != " + Snip(b.Scalar));
                    return true;
                }
                return false;
            }
            if (a.Kind == 1)
            {
                int n = Math.Min(a.Items.Count, b.Items.Count);
                int plen = path.Length;
                for (int i = 0; i < n; i++)
                {
                    path.Length = plen;
                    path.Append('[').Append(i).Append(']');
                    if (DiffNode(a.Items[i], b.Items[i], path, detail)) { return true; }
                }
                if (a.Items.Count != b.Items.Count)
                {
                    detail.Append(path).Append(": array len " + a.Items.Count
                        + " != " + b.Items.Count);
                    return true;
                }
                return false;
            }
            // object: member lists are already ordinal-sorted on both sides
            int m = Math.Min(a.Members.Count, b.Members.Count);
            int baseLen = path.Length;
            for (int i = 0; i < m; i++)
            {
                string ka = a.Members[i].Key;
                string kb = b.Members[i].Key;
                if (!string.Equals(ka, kb, StringComparison.Ordinal))
                {
                    detail.Append(path).Append(": member set differs - \""
                        + ka + "\" vs \"" + kb + "\"");
                    return true;
                }
                path.Length = baseLen;
                path.Append('.').Append(ka);
                if (DiffNode(a.Members[i].Value, b.Members[i].Value, path, detail))
                {
                    return true;
                }
            }
            if (a.Members.Count != b.Members.Count)
            {
                List<KeyValuePair<string, Node>> extra =
                    a.Members.Count > b.Members.Count ? a.Members : b.Members;
                detail.Append(path).Append(": member count " + a.Members.Count
                    + " != " + b.Members.Count + " (e.g. extra \""
                    + extra[m].Key + "\")");
                return true;
            }
            return false;
        }

        private static string Snip(string s)
        {
            if (s == null) { return "null"; }
            return s.Length <= 48 ? s : s.Substring(0, 45) + "...";
        }

        private static string KindName(int k)
        {
            return k == 0 ? "scalar" : k == 1 ? "array" : "object";
        }

        private sealed class Node
        {
            internal int Kind;                       // 0 scalar, 1 array, 2 object
            internal string Scalar;                  // kind 0
            internal List<Node> Items;               // kind 1
            internal List<KeyValuePair<string, Node>> Members; // kind 2
        }

        /// <summary>Recursive-descent parser for our own canonical format.</summary>
        private static Node Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) { return null; }
            int pos = 0;
            Node n = ParseValue(s, ref pos);
            return pos == s.Length ? n : null;
        }

        private static Node ParseValue(string s, ref int pos)
        {
            if (pos >= s.Length) { return null; }
            char c = s[pos];
            switch (c)
            {
                case 'N':
                    pos++;
                    return new Node { Kind = 0, Scalar = "N" };
                case 'B':
                    if (pos + 1 < s.Length && (s[pos + 1] == '0' || s[pos + 1] == '1'))
                    {
                        var n = new Node { Kind = 0, Scalar = s.Substring(pos, 2) };
                        pos += 2;
                        return n;
                    }
                    return null;
                case 'I':
                {
                    int start = pos;
                    pos++;
                    if (pos < s.Length && s[pos] == '-') { pos++; }
                    while (pos < s.Length && s[pos] >= '0' && s[pos] <= '9') { pos++; }
                    if (pos == start + 1 || (s[start + 1] == '-' && pos == start + 2))
                    {
                        return null;
                    }
                    return new Node { Kind = 0, Scalar = s.Substring(start, pos - start) };
                }
                case 'D':
                case 'F':
                {
                    int start = pos;
                    pos++;
                    if (pos < s.Length && s[pos] == '!')
                    {
                        // D!N / D!+I / D!-I tokens
                        pos++;
                        while (pos < s.Length && "+IN-".IndexOf(s[pos]) >= 0) { pos++; }
                        return new Node { Kind = 0, Scalar = s.Substring(start, pos - start) };
                    }
                    int digits = c == 'D' ? 16 : 8;
                    for (int i = 0; i < digits && pos < s.Length && IsHex(s[pos]); i++) { pos++; }
                    return new Node { Kind = 0, Scalar = s.Substring(start, pos - start) };
                }
                case '"':
                {
                    string str = ParseString(s, ref pos);
                    return str == null ? null : new Node { Kind = 0, Scalar = str };
                }
                case '[':
                {
                    pos++;
                    var items = new List<Node>();
                    if (pos < s.Length && s[pos] == ']') { pos++; return new Node { Kind = 1, Items = items }; }
                    while (pos < s.Length)
                    {
                        Node it = ParseValue(s, ref pos);
                        if (it == null) { return null; }
                        items.Add(it);
                        if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                        if (pos < s.Length && s[pos] == ']') { pos++; return new Node { Kind = 1, Items = items }; }
                        return null;
                    }
                    return null;
                }
                case '{':
                {
                    pos++;
                    var members = new List<KeyValuePair<string, Node>>();
                    if (pos < s.Length && s[pos] == '}') { pos++; return new Node { Kind = 2, Members = members }; }
                    while (pos < s.Length)
                    {
                        if (pos >= s.Length || s[pos] != '"') { return null; }
                        string key = ParseString(s, ref pos);
                        if (key == null || pos >= s.Length || s[pos] != ':') { return null; }
                        pos++;
                        Node v = ParseValue(s, ref pos);
                        if (v == null) { return null; }
                        members.Add(new KeyValuePair<string, Node>(key, v));
                        if (pos < s.Length && s[pos] == ',') { pos++; continue; }
                        if (pos < s.Length && s[pos] == '}') { pos++; return new Node { Kind = 2, Members = members }; }
                        return null;
                    }
                    return null;
                }
                default:
                    return null;
            }
        }

        private static bool IsHex(char c)
        {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        }

        /// <summary>Read a canonical string literal back into raw text.</summary>
        private static string ParseString(string s, ref int pos)
        {
            if (pos >= s.Length || s[pos] != '"') { return null; }
            pos++;
            var sb = new StringBuilder();
            while (pos < s.Length)
            {
                char c = s[pos++];
                if (c == '"') { return sb.ToString(); }
                if (c == '\\' && pos < s.Length)
                {
                    char e = s[pos++];
                    if (e == 'u' && pos + 4 <= s.Length)
                    {
                        int v;
                        if (!int.TryParse(s.Substring(pos, 4), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out v))
                        {
                            return null;
                        }
                        sb.Append((char)v);
                        pos += 4;
                    }
                    else
                    {
                        sb.Append(e);
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            return null; // unterminated
        }
    }
}
