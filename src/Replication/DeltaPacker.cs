using System;
using System.Collections.Generic;
using System.Text;
using X2Coop.Net;

namespace X2Coop.Replication
{
    /// <summary>
    /// T5.2 delta packet packing - game-free, linked into the xunit suite.
    /// Exact byte model of the codec wire format
    /// ([MsgType:1][sessionId:16][seq:8][hostSendTicks:8][opCount:2][ops])
    /// so <see cref="Split"/> can guarantee every emitted message stays at or
    /// under <see cref="Protocol.MaxMessageSize"/> BEFORE encode - the codec's
    /// own oversize gate then never fires on a well-formed batch. An op larger
    /// than the whole budget (a ~>65 KiB component payload, also capped by
    /// WireLimits.MaxOpPayloadBytes on encode) is emitted alone so the codec's
    /// deterministic reject bounds the damage to that one op.
    /// </summary>
    internal static class DeltaPacker
    {
        /// <summary>[type:1]+[sessionId:16] + [seq:8]+[hostSendTicks:8]+[opCount:2].</summary>
        internal const int FixedBytes = MsgCodec.EnvelopeSize + 8 + 8 + 2;

        /// <summary>
        /// One op's wire bytes: [entityId:4][kind:1][typeLen:4][type][payLen:4][payload].
        /// Matches DeltaOpWire.WriteBody exactly (PutString = int32 UTF8 count;
        /// PutBytesWithLength = int32 count + bytes; null payload encodes as 0).
        /// </summary>
        internal static int OpWireBytes(DeltaOp op)
        {
            int typeBytes = 0;
            if (!string.IsNullOrEmpty(op.ComponentType))
            {
                try { typeBytes = Encoding.UTF8.GetByteCount(op.ComponentType); }
                catch { typeBytes = 0; }
            }
            return 4 + 1 + 4 + typeBytes + 4 + (op.Payload != null ? op.Payload.Length : 0);
        }

        /// <summary>Exact encoded size of a delta message carrying <paramref name="ops"/>.</summary>
        internal static int MsgWireBytes(IReadOnlyList<DeltaOp> ops)
        {
            long sum = FixedBytes;
            if (ops != null)
            {
                for (int i = 0; i < ops.Count; i++) { sum += OpWireBytes(ops[i]); }
            }
            return sum > int.MaxValue ? int.MaxValue : (int)sum;
        }

        /// <summary>
        /// Greedy order-preserving split: fill each chunk until the next op
        /// would exceed <paramref name="budgetBytes"/> or the chunk hits
        /// <see cref="WireLimits.MaxDeltaOps"/>. A single op over budget goes
        /// out alone (encode then deterministically rejects it - the seq is
        /// still burned, which is correct: a gap beats a silent divergence).
        /// Empty input returns an empty list (never a zero-op packet).
        /// </summary>
        internal static List<DeltaOp[]> Split(IReadOnlyList<DeltaOp> ops, int budgetBytes)
        {
            var result = new List<DeltaOp[]>(4);
            if (ops == null || ops.Count == 0 || budgetBytes <= FixedBytes)
            {
                return result;
            }
            int i = 0;
            int n = ops.Count;
            while (i < n)
            {
                int bytes = FixedBytes;
                int count = 0;
                while (i < n)
                {
                    int opBytes = OpWireBytes(ops[i]);
                    if (count > 0
                        && (bytes + opBytes > budgetBytes || count >= WireLimits.MaxDeltaOps))
                    {
                        break;
                    }
                    bytes += opBytes;
                    count++;
                    i++;
                    if (opBytes + FixedBytes > budgetBytes) { break; } // lone oversized op
                }
                var chunk = new DeltaOp[count];
                for (int k = 0; k < count; k++) { chunk[k] = ops[i - count + k]; }
                result.Add(chunk);
            }
            return result;
        }
    }
}
