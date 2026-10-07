using System;
using System.Collections.Generic;
using X2Coop.Net;

namespace X2Coop.Replication
{
    /// <summary>
    /// T5.3 latest-state coalescer for the PosDelta lane. PosDelta rides
    /// LiteNetLib Sequenced (drops + only-newest delivery are expected), but
    /// several sequenced packets can still land inside one frame, and a
    /// packet can carry ops for the same (entity, component) key that a
    /// LATER packet also carries. The applier drains this buffer once per
    /// frame and only the highest-sequence op per key is ever applied -
    /// an older packet can never overwrite a newer position.
    ///
    /// Keys are (entityId, componentType-name) pairs - the same key shape
    /// the host coalesces on. Game-free on purpose (DeltaOp lives in the
    /// codec layer) so the xunit suite links + drives it directly.
    /// </summary>
    internal sealed class PosCoalescer
    {
        private sealed class Entry
        {
            public long Seq;
            public DeltaOp Op;
        }

        private readonly Dictionary<string, Entry> _latest =
            new Dictionary<string, Entry>(StringComparer.Ordinal);

        /// <summary>Seq of the newest PosDelta message seen (for the apply-pos log).</summary>
        internal long LastMsgSeq;
        /// <summary>HostSendTicks of the newest PosDelta message seen.</summary>
        internal long LastMsgTicks;
        /// <summary>Ops superseded by a newer op before ever applying.</summary>
        internal long StaleOps;

        /// <summary>Fold every op of a PosDelta message into the buffer.</summary>
        internal void Offer(PosDeltaMsg msg)
        {
            if (msg == null || msg.Ops == null) { return; }
            if (msg.Seq > LastMsgSeq)
            {
                LastMsgSeq = msg.Seq;
                LastMsgTicks = msg.HostSendTicks;
            }
            for (int i = 0; i < msg.Ops.Length; i++)
            {
                DeltaOp op = msg.Ops[i];
                string key = op.EntityId + "|" + (op.ComponentType ?? string.Empty);
                Entry e;
                if (_latest.TryGetValue(key, out e))
                {
                    if (msg.Seq <= e.Seq)
                    {
                        StaleOps++; // an older packet loses - never applied
                        continue;
                    }
                    StaleOps++; // the pending op it replaces dies stale
                    e.Seq = msg.Seq;
                    e.Op = op;
                }
                else
                {
                    _latest[key] = new Entry { Seq = msg.Seq, Op = op };
                }
            }
        }

        /// <summary>Number of distinct (entity,component) keys waiting to apply.</summary>
        internal int PendingCount { get { return _latest.Count; } }

        /// <summary>Take the newest op per key (application order is irrelevant across keys).</summary>
        internal List<DeltaOp> Drain()
        {
            var list = new List<DeltaOp>(_latest.Count);
            foreach (KeyValuePair<string, Entry> kv in _latest)
            {
                list.Add(kv.Value.Op);
            }
            _latest.Clear();
            return list;
        }

        /// <summary>Drop everything (world rebind / reset / poison).</summary>
        internal void Clear()
        {
            _latest.Clear();
            LastMsgSeq = 0;
            LastMsgTicks = 0;
        }
    }
}
