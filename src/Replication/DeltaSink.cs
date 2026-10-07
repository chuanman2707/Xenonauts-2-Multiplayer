using System;
using System.Diagnostics;
using X2Coop.Net;

namespace X2Coop.Replication
{
    /// <summary>
    /// T5.2 DeltaSink (guest side): receive-side accounting for the two delta
    /// streams. T5.3 owns actual decode+apply; this sink already does the two
    /// jobs that are correct independent of application:
    ///
    ///   - sequence continuity per stream: StateDelta.Seq must be exactly
    ///     last+1 (ReliableOrdered can't reorder - a different value means a
    ///     delta was LOST, which on a host-authoritative mirror is the
    ///     resync trigger T5.3 will escalate); PosDelta.Seq is a SEPARATE
    ///     counter (Sequenced drops are normal - counted, never alarming).
    ///   - a periodic "delta-rx" line so the guest log carries independent
    ///     evidence of the arriving stream (pairs with the host's
    ///     "delta-stats" line for two-instance verification).
    ///
    /// Session-scoped like the other guest state: SaveReceiver.Reset,
    /// SessionClient.Detach and Teardown clear it. Nothing here retains a
    /// message, world or peer reference.
    /// </summary>
    internal static class DeltaSink
    {
        private const int StatsMs = 2000;
        private const int MaxGapLogs = 6;   // first N gaps log individually; the counter keeps counting

        private static readonly Stopwatch _watch = new Stopwatch();

        private static bool _haveSeq;
        private static long _lastSeq;
        private static bool _havePosSeq;
        private static long _lastPosSeq;

        // session totals
        internal static long Deltas;
        internal static long DeltaOps;
        internal static long PosDeltas;
        internal static long PosOps;
        internal static long Gaps;      // reliable-stream anomalies
        internal static long PosDrops;  // sequenced drops (expected, informational)

        // stats window
        private static long _winDeltas, _winOps, _winPosDeltas, _winPosOps;
        private static int _gapLogs;

        /// <summary>Decoded StateDelta arrived (already session-gated by the codec).</summary>
        internal static void OnDelta(StateDeltaMsg msg)
        {
            try
            {
                if (msg == null) { return; }
                int n = msg.Ops != null ? msg.Ops.Length : 0;
                long prevSeq = _haveSeq ? _lastSeq : -1;
                Deltas++;
                DeltaOps += n;
                _winDeltas++;
                _winOps += n;
                if (_haveSeq && msg.Seq != _lastSeq + 1)
                {
                    Gaps++;
                    if (_gapLogs < MaxGapLogs)
                    {
                        _gapLogs++;
                        Log.Warn("delta-rx gap seq=" + msg.Seq
                            + " expected=" + (_lastSeq + 1)
                            + " (a lost reliable delta diverges the mirror - T5.3 resync)");
                    }
                }
                // T5.3: only strictly-new seqs enqueue (a dup/stale packet can
                // never re-apply state the guest already has).
                if (msg.Seq > prevSeq)
                {
                    StateApplier.Enqueue(msg);
                }
                if (prevSeq >= 0 && msg.Seq > prevSeq + 1)
                {
                    // Forward jump = real loss on ReliableOrdered: poison the
                    // stream + escalate. A resync baseline is the only cure.
                    StateApplier.NoteStreamBroken("delta seq gap got=" + msg.Seq
                        + " want=" + (prevSeq + 1));
                    Session.SessionClient.RequestResync(ResyncReason.MissedDelta,
                        "delta seq gap got=" + msg.Seq + " want=" + (prevSeq + 1));
                }
                if (!_haveSeq || msg.Seq > _lastSeq)
                {
                    _lastSeq = msg.Seq; // never regress on a dup/stale packet
                    _haveSeq = true;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("delta-rx", ex);
            }
        }

        /// <summary>
        /// A SaveOffer was accepted: its baseline snapshot covers every seq
        /// &lt;= baselineSeq ("first live delta = baselineSeq + 1",
        /// Messages.cs:410), so the watermark must move to baselineSeq -
        /// without it the first post-baseline delta reads as a one-seq gap
        /// and the resync loops forever (seen live: three baseline cycles,
        /// each flagged got=baselineSeq+1 want=baselineSeq). Never regress:
        /// a stale offer must not re-arm already-applied seqs.
        /// </summary>
        internal static void NoteBaselineSeq(long baselineSeq)
        {
            try
            {
                if (!_haveSeq || baselineSeq > _lastSeq)
                {
                    _lastSeq = baselineSeq;
                    _haveSeq = true;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("delta-rx baseline", ex);
            }
        }

        /// <summary>Decoded PosDelta arrived (latest-state lane; drops are normal).</summary>
        internal static void OnPosDelta(PosDeltaMsg msg)
        {
            try
            {
                if (msg == null) { return; }
                int n = msg.Ops != null ? msg.Ops.Length : 0;
                PosDeltas++;
                PosOps += n;
                _winPosDeltas++;
                _winPosOps += n;
                if (_havePosSeq && msg.Seq > _lastPosSeq + 1)
                {
                    PosDrops += msg.Seq - _lastPosSeq - 1; // informational only
                }
                if (!_havePosSeq || msg.Seq > _lastPosSeq)
                {
                    _lastPosSeq = msg.Seq; // never regress on a reordered stale packet
                    _havePosSeq = true;
                }
                // T5.3: every pos message feeds the coalescer - latest seq per
                // (entity,component) wins, so stale packets can never regress
                // a position.
                StateApplier.EnqueuePos(msg);
            }
            catch (Exception ex)
            {
                Log.Exception("delta-rx pos", ex);
            }
        }

        /// <summary>SessionClient.Tick pump: emits the delta-rx stats line.</summary>
        internal static void Tick()
        {
            try
            {
                if (!_watch.IsRunning) { _watch.Restart(); return; }
                if (_watch.ElapsedMilliseconds < StatsMs) { return; }
                _watch.Restart();
                if (_winDeltas == 0 && _winPosDeltas == 0 && _winOps == 0 && _winPosOps == 0)
                {
                    return; // silent while the mirror stream is quiet
                }
                Log.Info("delta-rx deltas=" + _winDeltas
                    + " ops=" + _winOps
                    + " pos=" + _winPosDeltas
                    + " posOps=" + _winPosOps
                    + " gaps=" + Gaps
                    + " lastSeq=" + (_haveSeq ? _lastSeq.ToString() : "-"));
                _winDeltas = _winOps = _winPosDeltas = _winPosOps = 0;
            }
            catch (Exception ex)
            {
                Log.Exception("delta-rx tick", ex);
            }
        }

        /// <summary>Session end (SaveReceiver.Reset / Detach / Teardown).</summary>
        internal static void Reset()
        {
            bool had = Deltas != 0 || PosDeltas != 0 || _haveSeq;
            _watch.Reset();
            _haveSeq = false;
            _havePosSeq = false;
            _lastSeq = 0;
            _lastPosSeq = 0;
            Deltas = DeltaOps = PosDeltas = PosOps = 0;
            Gaps = PosDrops = 0;
            _winDeltas = _winOps = _winPosDeltas = _winPosOps = 0;
            _gapLogs = 0;
            if (had)
            {
                Log.Info("delta-rx: reset (session end)");
            }
        }
    }
}
