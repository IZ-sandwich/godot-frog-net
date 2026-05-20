using System;
using System.Collections.Generic;
using System.Linq;

namespace MonkeNet.Tests.Infrastructure.Metrics;

/// <summary>
/// Accumulates the M1–M8 metrics defined in the quantitative test plan for one
/// scenario × condition cell. Each scenario calls one of the <c>Record*</c>
/// methods at every sampling tick; the test then calls <see cref="ToSummary"/>
/// to materialize a flat row for the summary CSV.
///
/// <para>
/// Metric semantics (anchored to industry reference points — see the plan
/// file for the rationale):
/// <list type="bullet">
///   <item><b>M1 ClockConvergenceTicks</b> — first tick where 10 consecutive clock-gap samples are all &lt; 2 ticks.</item>
///   <item><b>M2 ClockSteadyStateRmsTicks</b> — RMS clock gap after M1.</item>
///   <item><b>M3 MispredictRate / M3aPhysicsNondet / M3bExternalForce / M3cDegradedNetwork</b> — counts ÷ observation ticks.</item>
///   <item><b>M4 RollbackDepth P50/P95/P99</b> — distribution of resim tick depths.</item>
///   <item><b>M5 PositionErrorRms / PositionErrorP95</b> — RMS and P95 of |client_pos − server_pos|.</item>
///   <item><b>M6 VisualSmoothRatio</b> — mean(|visual − auth|) ÷ mean(|body − auth|).</item>
///   <item><b>M7 PostRollbackConvergenceTicks P95</b> — ticks until post-correction error drops below 0.1 m.</item>
///   <item><b>M8 entity-count scaling</b> — recorded externally by comparing M5 across scenarios with different body counts.</item>
/// </list>
/// </para>
/// </summary>
public sealed class SyncMetrics
{
    // --- raw accumulators ------------------------------------------------

    private readonly List<int> _clockGaps = new();
    private int _firstConvergenceTick = -1;
    private int _convergenceStreak = 0;
    private const int ConvergenceStreakRequired = 10;
    private const int ConvergenceGapThreshold = 2;     // ticks (strict, per plan)

    private long _observationTicks = 0;
    private int _externalForceTotal = 0;
    private int _physicsNondetTotal = 0;
    private int _degradedNetworkTotal = 0;

    private readonly List<int> _rollbackDepths = new();

    private readonly List<float> _positionErrors = new();
    private double _bodyErrorSum = 0;
    private double _visualErrorSum = 0;
    private int _errorPairCount = 0;

    private readonly List<int> _postRollbackConvergenceTicks = new();

    private int _missedInputTotal = 0;
    private long _bandwidthSentBytes = 0;
    private long _bandwidthRecvBytes = 0;
    private double _bandwidthDurationSeconds = 0;

    /// <summary>Threshold (meters) below which the body is considered converged
    /// after a rollback. Matches Gaffer's "no correction needed" floor — see
    /// plan M7.</summary>
    public const float GafferNoCorrectionFloor = 0.1f;

    // --- recorders -------------------------------------------------------

    /// <summary>Call once per clock-state sample. <paramref name="clockGapTicks"/>
    /// is <c>syncedTick − serverTick − latencyTicks</c>.</summary>
    public void RecordClockSample(int clockGapTicks)
    {
        _clockGaps.Add(clockGapTicks);
        if (_firstConvergenceTick < 0)
        {
            if (Math.Abs(clockGapTicks) < ConvergenceGapThreshold)
            {
                _convergenceStreak++;
                if (_convergenceStreak >= ConvergenceStreakRequired)
                    _firstConvergenceTick = _clockGaps.Count - ConvergenceStreakRequired + 1;
            }
            else
            {
                _convergenceStreak = 0;
            }
        }
    }

    /// <summary>Call each observation tick — the count of ticks observed by the
    /// client during the scenario. Used as the denominator for M3 rate metrics.</summary>
    public void RecordObservationTick() => _observationTicks++;

    /// <summary>Advances the observation-tick counter by <paramref name="ticks"/>
    /// at once. Used when polling counts across a fixed window rather than at
    /// every individual tick.</summary>
    public void AddObservationTicks(long ticks) => _observationTicks += ticks;

    /// <summary>Set the cumulative per-class mispredict counts read from the
    /// client at the end of the scenario. Cumulative-then-snapshot is simpler
    /// than per-event recording and matches the harness's existing counter
    /// model.</summary>
    public void SetMispredictTotals(int externalForce, int physicsNondet, int degradedNetwork)
    {
        _externalForceTotal = externalForce;
        _physicsNondetTotal = physicsNondet;
        _degradedNetworkTotal = degradedNetwork;
    }

    /// <summary>Record one rollback depth sample. Call after every observed
    /// increase in <c>MispredictionsCount</c>.</summary>
    public void RecordRollbackDepth(int depthTicks)
    {
        if (depthTicks > 0) _rollbackDepths.Add(depthTicks);
    }

    /// <summary>Record one (client, server) position pair. The error magnitude
    /// is appended to the M5 distribution; body and visual errors feed M6.</summary>
    public void RecordPositionError(float bodyErrorMeters, float visualErrorMeters)
    {
        _positionErrors.Add(bodyErrorMeters);
        _bodyErrorSum += bodyErrorMeters;
        _visualErrorSum += visualErrorMeters;
        _errorPairCount++;
    }

    /// <summary>Record the number of ticks elapsed between a mispredict event
    /// and the body returning to within 0.1m of the server's authoritative pose
    /// (the M7 metric).</summary>
    public void RecordPostRollbackConvergence(int ticks) => _postRollbackConvergenceTicks.Add(ticks);

    /// <summary>Set the cumulative missed-input count read from the client at
    /// the end of the scenario. Cumulative count rather than per-event keeps
    /// the wire chatter low — pulled once per cell.</summary>
    public void SetMissedInputTotal(int total) => _missedInputTotal = total;

    /// <summary>Record one bandwidth-stats sample. <paramref name="durationSeconds"/>
    /// is the elapsed time since the previous sample (used to compute the rate).
    /// Pushed by the runner at fixed intervals during the observation window.</summary>
    public void AddBandwidthSample(int sentBytes, int recvBytes, double durationSeconds)
    {
        _bandwidthSentBytes += sentBytes;
        _bandwidthRecvBytes += recvBytes;
        _bandwidthDurationSeconds += durationSeconds;
    }

    // --- summarisation ---------------------------------------------------

    public Summary ToSummary(string scenario, string condition)
    {
        return new Summary
        {
            Scenario = scenario,
            Condition = condition,
            M1_ClockConvergenceTicks = _firstConvergenceTick,
            M2_ClockSteadyStateRmsTicks = ComputeClockRms(),
            M3_MispredictRatePct = SafeRate(_externalForceTotal + _physicsNondetTotal + _degradedNetworkTotal),
            M3a_PhysicsNondetRatePct = SafeRate(_physicsNondetTotal),
            M3b_ExternalForceRatePct = SafeRate(_externalForceTotal),
            M3c_DegradedNetworkRatePct = SafeRate(_degradedNetworkTotal),
            M4_RollbackDepthP50 = Percentile(_rollbackDepths, 0.50),
            M4_RollbackDepthP95 = Percentile(_rollbackDepths, 0.95),
            M4_RollbackDepthP99 = Percentile(_rollbackDepths, 0.99),
            M5_PositionErrorRms = ComputeRms(_positionErrors),
            M5_PositionErrorP95 = (float)Percentile(_positionErrors.Select(x => (double)x).ToList(), 0.95),
            M6_VisualSmoothRatio = _errorPairCount == 0 || _bodyErrorSum <= 0
                ? 0f
                : (float)(_visualErrorSum / _bodyErrorSum),
            M7_PostRollbackConvergenceP95 = Percentile(_postRollbackConvergenceTicks, 0.95),
            M9_MissedInputRatePct = SafeRate(_missedInputTotal),
            M10_BandwidthKBps = _bandwidthDurationSeconds <= 0
                ? 0f
                : (float)((_bandwidthSentBytes + _bandwidthRecvBytes) / 1024.0 / _bandwidthDurationSeconds),
            ObservationTicks = _observationTicks,
            SampleCount = _positionErrors.Count,
        };
    }

    private float ComputeClockRms()
    {
        // -1 (sentinel for "did not converge / fail") rather than NaN so the
        // dashboard treats this as an explicit failure rather than as N/A.
        // The masking step in the runner replaces these with NaN only when
        // the scenario doesn't exercise clock metrics, which is impossible
        // in practice — every cell has a clock-sample warm-up — but the
        // mask handling is uniform across all metrics for consistency.
        if (_firstConvergenceTick < 0) return -1f;
        double sumSq = 0;
        int n = 0;
        for (int i = _firstConvergenceTick; i < _clockGaps.Count; i++)
        {
            sumSq += (double)_clockGaps[i] * _clockGaps[i];
            n++;
        }
        return n == 0 ? 0f : (float)Math.Sqrt(sumSq / n);
    }

    private float SafeRate(long numerator)
    {
        if (_observationTicks <= 0) return 0f;
        return (float)(100.0 * numerator / _observationTicks);
    }

    private static float ComputeRms(List<float> values)
    {
        if (values.Count == 0) return 0f;
        double sumSq = 0;
        foreach (var v in values) sumSq += (double)v * v;
        return (float)Math.Sqrt(sumSq / values.Count);
    }

    private static int Percentile(List<int> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(x => x).ToList();
        int idx = (int)Math.Min(sorted.Count - 1, Math.Max(0, Math.Ceiling(p * sorted.Count) - 1));
        return sorted[idx];
    }

    private static double Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return 0;
        var sorted = values.OrderBy(x => x).ToList();
        int idx = (int)Math.Min(sorted.Count - 1, Math.Max(0, Math.Ceiling(p * sorted.Count) - 1));
        return sorted[idx];
    }

    public sealed class Summary
    {
        public string Scenario = "";
        public string Condition = "";
        // -1.0 means "did not converge" (a real measurement, treated as fail
        // downstream). NaN means "not applicable to this scenario" — these
        // fields use float so the same NaN sentinel works uniformly across
        // every metric column without integer hackery.
        public float M1_ClockConvergenceTicks;
        public float M2_ClockSteadyStateRmsTicks;
        public float M3_MispredictRatePct;
        public float M3a_PhysicsNondetRatePct;
        public float M3b_ExternalForceRatePct;
        public float M3c_DegradedNetworkRatePct;
        public float M4_RollbackDepthP50;
        public float M4_RollbackDepthP95;
        public float M4_RollbackDepthP99;
        public float M5_PositionErrorRms;
        public float M5_PositionErrorP95;
        public float M6_VisualSmoothRatio;
        public float M7_PostRollbackConvergenceP95;
        public float M9_MissedInputRatePct;
        public float M10_BandwidthKBps;
        public long ObservationTicks;
        public int SampleCount;
    }
}
