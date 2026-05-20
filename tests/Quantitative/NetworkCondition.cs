namespace MonkeNet.Tests.Quantitative;

/// <summary>
/// One row from the quantitative-suite network condition matrix. Compact,
/// human-readable label + the three parameters the UDP relay needs to inject
/// the condition.
/// </summary>
public sealed class NetworkCondition
{
    public string Id { get; }
    public string Label { get; }
    public int LatencyMs { get; }
    public float LossRate { get; }
    public int JitterMs { get; }

    public NetworkCondition(string id, string label, int latencyMs, float lossRate, int jitterMs)
    {
        Id = id;
        Label = label;
        LatencyMs = latencyMs;
        LossRate = lossRate;
        JitterMs = jitterMs;
    }

    public override string ToString() => $"{Id} ({Label}: {LatencyMs}ms / {LossRate:P0} loss / ±{JitterMs}ms jitter)";

    // The canonical matrix from the plan. Tests use IDs C0..C5 to filter which
    // conditions to run for a given scenario; the matrix is the single source
    // of truth for "what does C3 mean?".
    public static readonly NetworkCondition C0_Baseline       = new("C0", "Baseline",       latencyMs:   0, lossRate: 0.00f, jitterMs:  0);
    public static readonly NetworkCondition C1_Lan            = new("C1", "LAN",            latencyMs:  50, lossRate: 0.00f, jitterMs:  0);
    public static readonly NetworkCondition C2_GoodBroadband  = new("C2", "GoodBroadband",  latencyMs: 100, lossRate: 0.01f, jitterMs: 10);
    public static readonly NetworkCondition C3_AvgBroadband   = new("C3", "AvgBroadband",   latencyMs: 200, lossRate: 0.02f, jitterMs: 20);
    public static readonly NetworkCondition C4_Poor           = new("C4", "Poor",           latencyMs: 300, lossRate: 0.05f, jitterMs: 30);
    public static readonly NetworkCondition C5_Stress         = new("C5", "Stress",         latencyMs: 300, lossRate: 0.10f, jitterMs: 50);

    public static readonly NetworkCondition[] All =
    {
        C0_Baseline, C1_Lan, C2_GoodBroadband, C3_AvgBroadband, C4_Poor, C5_Stress,
    };
}
