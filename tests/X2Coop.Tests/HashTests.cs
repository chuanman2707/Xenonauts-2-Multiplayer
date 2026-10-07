using System;
using System.Collections.Generic;
using System.Text;
using FullSerializer;
using Xunit;
using X2Coop.Replication;

namespace X2Coop.Tests
{
    /// <summary>
    /// T5.5 game-free verification of <see cref="CanonicalJson"/>: canonical
    /// key ordering, scalar type tagging, wire-faithful numeric
    /// normalization, string escaping, item sorting, digest determinism, and
    /// the first-difference helper used for mismatch diagnostics.
    ///
    /// The keystone property under test: canonical(fsData) must be identical
    /// on both peers whenever the WIRE forms are identical - because the guest
    /// rebuilds fsData by parsing the host's CompressedJson, every scalar is
    /// asserted stable through an actual print->parse round trip.
    /// </summary>
    public class HashTests
    {
        private static fsData Dict(params object[] kv)
        {
            var d = new Dictionary<string, fsData>();
            for (int i = 0; i + 1 < kv.Length; i += 2)
            {
                d[(string)kv[i]] = (fsData)kv[i + 1];
            }
            return new fsData(d);
        }

        private static string Canon(fsData d) => CanonicalJson.Write(d);

        /// <summary>
        /// fsData -> CompressedJson -> JSON -> fsData (what the wire does).
        /// fsJsonParser.Parse itself cannot run on this test host: its IL
        /// references ReadOnlySpan inside mscorlib (present on Unity's Mono,
        /// absent on net472 - TypeLoadException, and System.Memory cannot
        /// redirect a type lookup inside mscorlib). WireReparse below is a
        /// minimal parser that reproduces the parser's documented contract:
        /// integer literals -> fsDataType.Int64, decimal/exponent literals ->
        /// fsDataType.Double (see fsJsonParser.TryParseNumber / task notes).
        /// </summary>
        private static fsData RoundTrip(fsData d)
        {
            string json = fsJsonPrinter.CompressedJson(d);
            fsData back;
            string err;
            Assert.True(WireReparse.TryParse(json, out back, out err),
                "parse failed: " + (err ?? "?") + " in " + json);
            return back;
        }

        // ------------------------------------------------------------------
        // canonical shape
        // ------------------------------------------------------------------

        [Fact]
        public void Scalars_AreTypeTagged()
        {
            Assert.Equal("N", Canon(new fsData()));
            Assert.Equal("B1", Canon(new fsData(true)));
            Assert.Equal("B0", Canon(new fsData(false)));
            Assert.Equal("I42", Canon(new fsData(42L)));
            Assert.Equal("I-7", Canon(new fsData(-7L)));
            Assert.Equal("\"abc\"", Canon(new fsData("abc")));
            // int vs double vs string of the same digits must never collide
            Assert.NotEqual(Canon(new fsData(1L)), Canon(new fsData(1.0)));
            Assert.NotEqual(Canon(new fsData(1L)), Canon(new fsData("1")));
        }

        [Fact]
        public void ObjectKeys_SortedOrdinal()
        {
            fsData a = Dict("b", new fsData(2L), "a", new fsData(1L), "A", new fsData(0L));
            fsData b = Dict("a", new fsData(1L), "A", new fsData(0L), "b", new fsData(2L));
            Assert.Equal("{\"A\":I0,\"a\":I1,\"b\":I2}", Canon(a));
            Assert.Equal(Canon(a), Canon(b)); // insertion order irrelevant
        }

        [Fact]
        public void Arrays_KeepOrder()
        {
            fsData a = new fsData(new List<fsData> { new fsData(1L), new fsData(2L) });
            fsData b = new fsData(new List<fsData> { new fsData(2L), new fsData(1L) });
            Assert.Equal("[I1,I2]", Canon(a));
            Assert.NotEqual(Canon(a), Canon(b));
        }

        [Fact]
        public void Strings_EscapedDeterministically()
        {
            Assert.Equal("\"a\\\"b\"", Canon(new fsData("a\"b")));
            Assert.Equal("\"a\\\\b\"", Canon(new fsData("a\\b")));
            Assert.Equal("\"\\u0009tab\"", Canon(new fsData("\ttab")));
            Assert.Equal("\"\\u00e9\"", Canon(new fsData("é")));
        }

        // ------------------------------------------------------------------
        // wire-faithful numeric normalization
        // ------------------------------------------------------------------

        [Fact]
        public void Int64_RoundTripsLossless()
        {
            // fsJsonParser keeps integers lossless (digit path / long.TryParse)
            // - including beyond 2^53 where a double detour would corrupt.
            long big = long.MaxValue - 3;
            Assert.Equal("I" + big, Canon(RoundTrip(new fsData(big))));
            Assert.Equal(Canon(new fsData(big)), Canon(RoundTrip(new fsData(big))));
            Assert.Equal(Canon(new fsData(long.MinValue)),
                Canon(RoundTrip(new fsData(long.MinValue))));
        }

        [Fact]
        public void Double_WireNormalization()
        {
            // What the wire carries is ToString(Invariant) - canonical must be
            // the parsed-back value so "wire-equal" == "hash-equal".
            double v = 0.1;
            fsData back = RoundTrip(new fsData(v));
            Assert.Equal(Canon(new fsData(v)), Canon(back));
            // An integral double prints as "3.0" - still parses to 3.0
            fsData back3 = RoundTrip(new fsData(3.0));
            Assert.Equal(Canon(new fsData(3.0)), Canon(back3));
            // 1.0 double must still differ from 1 int
            Assert.NotEqual(Canon(new fsData(1L)), Canon(back3));
        }

        [Fact]
        public void Float_WireNormalization()
        {
            // JSON numbers carry no kind: fsJsonPrinter prints a float as a
            // plain number and the parser re-materializes it as a Double
            // (F-tag -> D-tag). Production hashing never reparses wire text -
            // both peers serialize LIVE components of identical member types,
            // so kinds are symmetric by construction. What must hold here:
            // the reparsed value canonicalizes to the SAME normalized double
            // as the numerically-equal original double.
            float f = 1.5f;
            fsData back = RoundTrip(new fsData(f));
            Assert.Equal(fsDataType.Double, back.Type); // wire drops the F kind
            Assert.Equal(Canon(new fsData((double)f)), Canon(back));
            // 0.3f prints as "0.3" - the reparsed double is 0.3, NOT
            // (double)0.3f. Fidelity lives at the FLOAT level: the guest's
            // float field is (float)wireText which recovers 0.3f exactly -
            // exactly what NormFloat models (ToString -> parse -> cast).
            float odd = 0.3f;
            fsData oddBack = RoundTrip(new fsData(odd));
            Assert.Equal(fsDataType.Double, oddBack.Type);
            Assert.Equal(odd, (float)oddBack.AsDouble);            // guest's float field
            Assert.Equal(Canon(new fsData(odd)),
                Canon(new fsData((float)oddBack.AsDouble)));      // same F-tag hash
            // ...and the float tags themselves remain stable+distinct
            Assert.Equal(Canon(new fsData(f)), Canon(new fsData(f)));
            Assert.NotEqual(Canon(new fsData(f)), Canon(back)); // F vs D tagged
        }

        [Fact]
        public void NonFinite_DoublesAreDistinctTokens()
        {
            Assert.Equal("D!N", Canon(new fsData(double.NaN)));
            Assert.Equal("D!+I", Canon(new fsData(double.PositiveInfinity)));
            Assert.Equal("D!-I", Canon(new fsData(double.NegativeInfinity)));
            Assert.NotEqual(Canon(new fsData(double.NaN)),
                Canon(new fsData(double.PositiveInfinity)));
        }

        [Fact]
        public void WholeTree_RoundTripStable()
        {
            fsData tree = Dict(
                "ents", new fsData(new List<fsData> { new fsData(12L), new fsData(-4L) }),
                "pos", Dict("x", new fsData(3.25), "y", new fsData(-0.5f)),
                "name", new fsData("sol<dier> \"A\""),
                "flag", new fsData(true),
                "missing", new fsData(),
                "count", new fsData(9007199254740993L) // > 2^53
            );
            // The wire drops the Float kind: -0.5f reparses as Double -0.5.
            // Everything else is kind-stable, so compare against the tree as
            // the wire actually delivers it.
            fsData asWire = Dict(
                "ents", new fsData(new List<fsData> { new fsData(12L), new fsData(-4L) }),
                "pos", Dict("x", new fsData(3.25), "y", new fsData((double)-0.5f)),
                "name", new fsData("sol<dier> \"A\""),
                "flag", new fsData(true),
                "missing", new fsData(),
                "count", new fsData(9007199254740993L)
            );
            Assert.Equal(Canon(asWire), Canon(RoundTrip(tree)));
            // Member-order invariance across the whole path too.
            Assert.Equal(Canon(asWire), Canon(RoundTrip(asWire)));
        }

        // ------------------------------------------------------------------
        // sort + digest
        // ------------------------------------------------------------------

        [Fact]
        public void Items_SortByEntityThenType()
        {
            var items = new List<HashItem>
            {
                new HashItem { EntityId = 5, TypeName = "B.Type", Canon = "I1" },
                new HashItem { EntityId = 2, TypeName = "Z.Type", Canon = "I2" },
                new HashItem { EntityId = 5, TypeName = "A.Type", Canon = "I3" },
            };
            CanonicalJson.Sort(items);
            Assert.Equal(2, items[0].EntityId);
            Assert.Equal("A.Type", items[1].TypeName);
            Assert.Equal("B.Type", items[2].TypeName);
        }

        [Fact]
        public void Digest_StableAcrossInputOrder()
        {
            var items = new List<HashItem>
            {
                new HashItem { EntityId = 1, TypeName = "T.A", Canon = "I1" },
                new HashItem { EntityId = 2, TypeName = "T.B", Canon = "\"x\"" },
                new HashItem { EntityId = 1, TypeName = "T.B", Canon = "B1" },
            };
            CanonicalJson.Sort(items);
            byte[] d1 = CanonicalJson.Digest(items);
            var reordered = new List<HashItem>
            {
                new HashItem { EntityId = 2, TypeName = "T.B", Canon = "\"x\"" },
                new HashItem { EntityId = 1, TypeName = "T.B", Canon = "B1" },
                new HashItem { EntityId = 1, TypeName = "T.A", Canon = "I1" },
            };
            CanonicalJson.Sort(reordered);
            byte[] d2 = CanonicalJson.Digest(reordered);
            Assert.Equal(d1, d2);
            Assert.Equal(32, d1.Length);
        }

        [Fact]
        public void Digest_ChangesOnAnyDifference()
        {
            var baseItems = new List<HashItem>
            {
                new HashItem { EntityId = 1, TypeName = "T.A", Canon = "I1" },
                new HashItem { EntityId = 2, TypeName = "T.B", Canon = "I2" },
            };
            CanonicalJson.Sort(baseItems);
            byte[] d1 = CanonicalJson.Digest(baseItems);

            // one scalar differs
            var diff = new List<HashItem>
            {
                new HashItem { EntityId = 1, TypeName = "T.A", Canon = "I1" },
                new HashItem { EntityId = 2, TypeName = "T.B", Canon = "I3" },
            };
            CanonicalJson.Sort(diff);
            Assert.NotEqual(d1, CanonicalJson.Digest(diff));

            // missing item differs
            var fewer = new List<HashItem>
            {
                new HashItem { EntityId = 1, TypeName = "T.A", Canon = "I1" },
            };
            CanonicalJson.Sort(fewer);
            Assert.NotEqual(d1, CanonicalJson.Digest(fewer));
        }

        // ------------------------------------------------------------------
        // first-diff helper
        // ------------------------------------------------------------------

        [Fact]
        public void Diff_ReportsMemberPath()
        {
            string a = Canon(Dict("hp", new fsData(10L), "name", new fsData("a")));
            string b = Canon(Dict("hp", new fsData(11L), "name", new fsData("a")));
            string d = CanonicalJson.Diff(a, b);
            Assert.NotNull(d);
            Assert.Contains("hp", d);
        }

        [Fact]
        public void Diff_NestedAndArrayPaths()
        {
            string a = Canon(Dict("pos", Dict("x", new fsData(1.0), "y", new fsData(2.0))));
            string b = Canon(Dict("pos", Dict("x", new fsData(1.0), "y", new fsData(3.0))));
            Assert.Contains("pos.y", CanonicalJson.Diff(a, b));

            string c = Canon(new fsData(new List<fsData>
                { new fsData(1L), new fsData(9L) }));
            string e = Canon(new fsData(new List<fsData>
                { new fsData(1L), new fsData(8L) }));
            Assert.Contains("[1]", CanonicalJson.Diff(c, e));
        }

        [Fact]
        public void Diff_Null_WhenEqual()
        {
            Assert.Null(CanonicalJson.Diff("{\"a\":I1}", "{\"a\":I1}"));
        }

        [Fact]
        public void Diff_MemberSetMismatch()
        {
            string d = CanonicalJson.Diff("{\"a\":I1,\"b\":I2}", "{\"a\":I1}");
            Assert.NotNull(d);
            Assert.Contains("member", d);
        }
    }

    /// <summary>
    /// Minimal JSON -> fsData parser implementing the fsJsonParser contract
    /// the wire depends on: objects/arrays/strings/bools/null literal, and
    /// numbers typed Int64 for bare integer literals, Double for
    /// decimal/exponent forms. Exists because fsJsonParser.Parse throws
    /// TypeLoadException on a net472 test host (ReadOnlySpan in mscorlib);
    /// the game exercises the real parser on Unity Mono - these tests only
    /// need the shape/typing rules, not its error handling.
    /// </summary>
    internal static class WireReparse
    {
        internal static bool TryParse(string s, out fsData data, out string err)
        {
            data = null;
            err = null;
            if (string.IsNullOrEmpty(s)) { err = "empty"; return false; }
            int pos = 0;
            fsData v = Value(s, ref pos, ref err);
            SkipWs(s, ref pos);
            if (err != null || v == null || pos != s.Length)
            {
                err = err ?? ("trailing at " + pos);
                return false;
            }
            data = v;
            return true;
        }

        private static void SkipWs(string s, ref int pos)
        {
            while (pos < s.Length
                && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\n' || s[pos] == '\r'))
            {
                pos++;
            }
        }

        private static fsData Value(string s, ref int pos, ref string err)
        {
            SkipWs(s, ref pos);
            if (pos >= s.Length) { err = "eof"; return null; }
            switch (s[pos])
            {
                case '{': return Object(s, ref pos, ref err);
                case '[': return Array(s, ref pos, ref err);
                case '"':
                {
                    string str = Str(s, ref pos, ref err);
                    return err != null ? null : new fsData(str);
                }
                case 't':
                    if (Match(s, ref pos, "true")) { return new fsData(true); }
                    break;
                case 'f':
                    if (Match(s, ref pos, "false")) { return new fsData(false); }
                    break;
                case 'n':
                    if (Match(s, ref pos, "null")) { return new fsData(); }
                    break;
                default:
                    if (s[pos] == '-' || (s[pos] >= '0' && s[pos] <= '9'))
                    {
                        return Number(s, ref pos, ref err);
                    }
                    break;
            }
            err = "bad value at " + pos;
            return null;
        }

        private static bool Match(string s, ref int pos, string lit)
        {
            if (pos + lit.Length > s.Length) { return false; }
            for (int i = 0; i < lit.Length; i++)
            {
                if (s[pos + i] != lit[i]) { return false; }
            }
            pos += lit.Length;
            return true;
        }

        private static fsData Number(string s, ref int pos, ref string err)
        {
            int start = pos;
            if (pos < s.Length && s[pos] == '-') { pos++; }
            bool real = false;
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c >= '0' && c <= '9') { pos++; continue; }
                if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-')
                {
                    real |= c == '.' || c == 'e' || c == 'E';
                    pos++;
                    continue;
                }
                break;
            }
            string tok = s.Substring(start, pos - start);
            long lv;
            double dv;
            if (!real && long.TryParse(tok,
                    System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out lv))
            {
                return new fsData(lv);
            }
            if (double.TryParse(tok,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out dv))
            {
                return new fsData(dv);
            }
            err = "bad number '" + tok + "'";
            return null;
        }

        private static string Str(string s, ref int pos, ref string err)
        {
            pos++; // opening quote
            var sb = new StringBuilder(s.Length - pos);
            while (pos < s.Length)
            {
                char c = s[pos++];
                if (c == '"') { return sb.ToString(); }
                if (c == '\\' && pos < s.Length)
                {
                    char e = s[pos++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (pos + 4 <= s.Length)
                            {
                                sb.Append((char)int.Parse(s.Substring(pos, 4),
                                    System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture));
                                pos += 4;
                            }
                            else { err = "bad \\u"; return null; }
                            break;
                        default: err = "bad esc " + e; return null;
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
            err = "unterminated string";
            return null;
        }

        private static fsData Array(string s, ref int pos, ref string err)
        {
            pos++; // '['
            var list = new List<fsData>();
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == ']') { pos++; return new fsData(list); }
            while (pos < s.Length)
            {
                fsData v = Value(s, ref pos, ref err);
                if (err != null) { return null; }
                list.Add(v);
                SkipWs(s, ref pos);
                if (pos >= s.Length) { break; }
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == ']') { pos++; return new fsData(list); }
                break;
            }
            err = "bad array at " + pos;
            return null;
        }

        private static fsData Object(string s, ref int pos, ref string err)
        {
            pos++; // '{'
            var dict = new Dictionary<string, fsData>();
            SkipWs(s, ref pos);
            if (pos < s.Length && s[pos] == '}') { pos++; return new fsData(dict); }
            while (pos < s.Length)
            {
                SkipWs(s, ref pos);
                if (pos >= s.Length || s[pos] != '"')
                {
                    err = "expected key at " + pos;
                    return null;
                }
                string k = Str(s, ref pos, ref err);
                if (err != null) { return null; }
                SkipWs(s, ref pos);
                if (pos >= s.Length || s[pos] != ':')
                {
                    err = "expected ':' at " + pos;
                    return null;
                }
                pos++;
                fsData v = Value(s, ref pos, ref err);
                if (err != null) { return null; }
                dict[k] = v;
                SkipWs(s, ref pos);
                if (pos >= s.Length) { break; }
                if (s[pos] == ',') { pos++; continue; }
                if (s[pos] == '}') { pos++; return new fsData(dict); }
                break;
            }
            err = "bad object at " + pos;
            return null;
        }
    }
}
