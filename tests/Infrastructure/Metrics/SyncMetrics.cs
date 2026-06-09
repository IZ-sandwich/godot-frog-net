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
///   <item><b>M9 MissedInputRate</b> — missed-input events ÷ observation ticks.</item>
///   <item><b>M10 BandwidthKBps P50/P95</b> — distribution of per-second kB/s
///     samples covering both spawn-burst and steady-state. The harness emits
///     one bucket per wall-second while sampling is armed; the runner records
///     them via <see cref="AddBandwidthBuckets"/> and the summary reports
///     P50 (typical) + P95 (tail). A single sample averaged over the whole
///     scenario was found to vary 24× between back-to-back runs because the
///     spawn burst dominated short windows — bucketing collapses that noise.</item>
///   <item><b>M11 SnapToAuthRate</b> — snap-on-overflow events ÷ observation ticks. Distinct from M3 (mispredict rate): M3 measures prediction quality, M11 measures how often <c>MaxRollbackTicks</c> is binding.</item>
///   <item><b>M13 ServerMissedInputRate</b> — server-side missed-input events ÷ observation ticks. Counts (tick × entity) pairs where the server ticked without a fresh stamped input from the owner and fell back to repeat-stale / default. Direct signal for the input-delay tuning loop (Option C).</item>
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
    private int _snapToAuthTotal = 0;
    private int _serverMissedInputTotal = 0;
    private readonly List<float> _bandwidthBuckets = new();

    // M14: visual smoothness. The client buckets every per-render-frame |Δv|²
    // value (one per render frame, where Δv is the change in visual-mesh
    // world-space velocity between consecutive frames). Storing the full
    // distribution rather than just a running sum lets the summary report
    // BOTH the RMS (tail-sensitive) AND the p50 (typical-frame jerk) — RMS
    // alone is dominated by snap-overflow spikes and hides whether the
    // typical frame is smooth. The runner also writes a per-scenario CDF
    // plot from these samples so a reader can see the full per-condition
    // distribution. Units of each stored sample are (m/s)²; RMS / p50 /
    // p95 reported in m/s (via sqrt at summary time).
    private readonly List<float> _visualSmoothnessDvSquared = new();

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

    /// <summary>Set the cumulative snap-to-auth count read from the client at
    /// the end of the scenario. M11 counts snapshots that arrived too old to
    /// resim (depth &gt; MaxRollbackTicks) and were corrected by teleport-snap
    /// instead. Semantically distinct from M3 (rollback mispredicts): M3
    /// measures prediction quality, M11 measures how often the cap is binding.</summary>
    public void SetSnapToAuthTotal(int total) => _snapToAuthTotal = total;

    /// <summary>Set the cumulative server-side missed-input count read from
    /// the server at the end of the scenario. M13 counts (tick × entity)
    /// events where the server consumed an input but no fresh
    /// client-stamped input was queued — server fell back to repeat-stale
    /// or default. Distinct from M9 (client-side prediction replay missed
    /// an input in snapshot history): M9 is a replay-time event at the
    /// client; M13 is an apply-time event at the server. Non-zero means
    /// inputs aren't arriving at the server in time for it to apply them
    /// at the stamped tick — usually a sign that <see cref="ClientInputManager.InputDelayTicks"/>
    /// is too low relative to current network jitter, or the client is
    /// not sending inputs at the server's tick rate.</summary>
    public void SetServerMissedInputTotal(int total) => _serverMissedInputTotal = total;

    /// <summary>Record the per-second kB/s bucket array returned by the harness
    /// at the end of the bandwidth observation window. Each entry covers one
    /// wall-second sample (configured by <c>BandwidthBucketMinMs</c> in the
    /// harness). M10 reports P50 + P95 of this distribution.</summary>
    public void AddBandwidthBuckets(IReadOnlyList<float> bucketsKBps)
    {
        if (bucketsKBps == null) return;
        foreach (var v in bucketsKBps) _bandwidthBuckets.Add(v);
    }

    /// <summary>Record the raw |Δv|² samples returned by the harness at the
    /// end of the scenario. Each entry covers one render frame; values are
    /// in (m/s)². M14 reports √(mean(samples)) (RMS) and √(p50(samples))
    /// (typical-frame jerk) and √(p95(samples)) (tail).</summary>
    public void AddVisualSmoothnessSamples(IReadOnlyList<float> dvSquaredSamples)
    {
        if (dvSquaredSamples == null) return;
        foreach (var v in dvSquaredSamples) _visualSmoothnessDvSquared.Add(v);
    }

    /// <summary>Raw |Δv|² samples accumulated by the runner for this cell.
    /// Exposed so the per-scenario CDF writer can fetch the distribution
    /// after the cell completes.</summary>
    public IReadOnlyList<float> VisualSmoothnessDvSquaredSamples => _visualSmoothnessDvSquared;

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
            M10_BandwidthP50KBps = (float)Percentile(_bandwidthBuckets.Select(x => (double)x).ToList(), 0.50),
            M10_BandwidthP95KBps = (float)Percentile(_bandwidthBuckets.Select(x => (double)x).ToList(), 0.95),
            M11_SnapToAuthRatePct = SafeRate(_snapToAuthTotal),
            M13_ServerMissedInputRatePct = SafeRate(_serverMissedInputTotal),
            M14_VisualSmoothnessRmsDeltaV = ComputeVisualSmoothnessRms(),
            M14_VisualSmoothnessP50DeltaV = ComputeVisualSmoothnessPercentile(0.50),
            M14_VisualSmoothnessP95DeltaV = ComputeVisualSmoothnessPercentile(0.95),
            ObservationTicks = _observationTicks,
            SampleCount = _positionErrors.Count,
        };
    }

    private float ComputeVisualSmoothnessRms()
    {
        if (_visualSmoothnessDvSquared.Count == 0) return 0f;
        double sum = 0;
        foreach (var v in _visualSmoothnessDvSquared) sum += v;
        return (float)Math.Sqrt(sum / _visualSmoothnessDvSquared.Count);
    }

    private float ComputeVisualSmoothnessPercentile(double p)
    {
        if (_visualSmoothnessDvSquared.Count == 0) return 0f;
        // Percentile over |Δv|² then sqrt — reporting in m/s keeps the units
        // identical to RMS so the strip plot can compare them on one axis.
        double pct = Percentile(_visualSmoothnessDvSquared.Select(x => (double)x).ToList(), p);
        return (float)Math.Sqrt(pct);
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
        public float M10_BandwidthP50KBps;
        public float M10_BandwidthP95KBps;
        public float M11_SnapToAuthRatePct;
        public float M13_ServerMissedInputRatePct;
        public float M14_VisualSmoothnessRmsDeltaV;
        public float M14_VisualSmoothnessP50DeltaV;
        public float M14_VisualSmoothnessP95DeltaV;
        public long ObservationTicks;
        public int SampleCount;
    }
}
