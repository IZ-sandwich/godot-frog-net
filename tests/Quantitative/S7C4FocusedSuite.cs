using System.Collections.Generic;
using GdUnit4;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Metrics;
using MonkeNet.Tests.Quantitative.Scenarios;

namespace MonkeNet.Tests.Quantitative;

// Focused single-cell wrapper for S7 × C4 used during snapback-fix verification.
// Inner-loop-style fast iteration on the same scenario the bug was reproduced
// in, without running the full 8 × 4 matrix.
[TestSuite]
[RequireGodotRuntime]
public class S7C4FocusedSuite : QuantitativeTestBase
{
    [BeforeTest] public void SetUp()    => SetUpInternal();
    [AfterTest]  public void TearDown() => TearDownInternal();

    [TestCase]
    public void RunS7C4()
    {
        RunMatrix(new IScenario[] { new S7C4Only() });
    }

    [TestCase]
    public void RunS7C0()
    {
        RunMatrix(new IScenario[] { new S7C0Only() });
    }

    // Single-cell C2 entrypoint — added for the periodic-stall investigation so
    // a dotnet-trace profile attaches to one cell only (smaller trace, single
    // condition to analyse).
    [TestCase]
    public void RunS7C2()
    {
        RunMatrix(new IScenario[] { new S7C2Only() });
    }

    /// <summary>Run S7 across the full C0..C4 condition matrix. Used to study
    /// how rollback cap + snap-on-overflow behaves across network qualities
    /// in a single run, so the per-condition plot tool has a consistent set
    /// of artifacts to compare.</summary>
    [TestCase]
    public void RunS7AllConditions()
    {
        RunMatrix(new IScenario[] { new S7AllConditions() });
    }

    /// <summary>Run S2 (linear motion) across its full condition matrix —
    /// used for the smoother-interpolation deep-dive where we need clean
    /// constant-velocity data to plot rigidbody-vs-visual position and
    /// rendered-vs-physics position separately for each network condition.
    /// Wraps the stock <see cref="S2_LinearMotion"/> to force CopyDebugLog on
    /// (default is off) so post-hoc plotting tools have SMOOTH-FRAME +
    /// PRED-REG data per condition.</summary>
    [TestCase]
    public void RunS2AllConditions()
    {
        RunMatrix(new IScenario[] { new S2WithDebugLogs() });
    }

    private sealed class S2WithDebugLogs : IScenario
    {
        private readonly S2_LinearMotion _inner = new();
        public string Id => "S2-LinearMotion";
        public NetworkCondition[] Conditions => _inner.Conditions;
        public bool RecordVideo => true;
        public bool CopyDebugLog => true;
        public MetricKey ApplicableMetrics => _inner.ApplicableMetrics;
        public void Setup(TestProcess server, TestProcess client) => _inner.Setup(server, client);
        public void Run(TestProcess server, TestProcess client, SyncMetrics metrics) => _inner.Run(server, client, metrics);
    }

    private sealed class S7C4Only : IScenario
    {
        private readonly S7_MultiBodyChaos _inner = new();
        public string Id => "S7-MultiBodyChaos";
        public NetworkCondition[] Conditions { get; } = { NetworkCondition.C4_Poor };
        public bool RecordVideo => true;
        public bool CopyDebugLog => true;
        public MetricKey ApplicableMetrics => _inner.ApplicableMetrics;
        public void Setup(TestProcess server, TestProcess client) => _inner.Setup(server, client);
        public void Run(TestProcess server, TestProcess client, SyncMetrics metrics) => _inner.Run(server, client, metrics);
    }

    private sealed class S7C0Only : IScenario
    {
        private readonly S7_MultiBodyChaos _inner = new();
        public string Id => "S7-MultiBodyChaos";
        public NetworkCondition[] Conditions { get; } = { NetworkCondition.C0_Baseline };
        public bool RecordVideo => true;
        public bool CopyDebugLog => true;
        public MetricKey ApplicableMetrics => _inner.ApplicableMetrics;
        public void Setup(TestProcess server, TestProcess client) => _inner.Setup(server, client);
        public void Run(TestProcess server, TestProcess client, SyncMetrics metrics) => _inner.Run(server, client, metrics);
    }

    private sealed class S7C2Only : IScenario
    {
        private readonly S7_MultiBodyChaos _inner = new();
        public string Id => "S7-MultiBodyChaos";
        public NetworkCondition[] Conditions { get; } = { NetworkCondition.C2_GoodBroadband };
        public bool RecordVideo => true;
        public bool CopyDebugLog => true;
        public MetricKey ApplicableMetrics => _inner.ApplicableMetrics;
        public void Setup(TestProcess server, TestProcess client) => _inner.Setup(server, client);
        public void Run(TestProcess server, TestProcess client, SyncMetrics metrics) => _inner.Run(server, client, metrics);
    }

    private sealed class S7AllConditions : IScenario
    {
        private readonly S7_MultiBodyChaos _inner = new();
        public string Id => "S7-MultiBodyChaos";
        public NetworkCondition[] Conditions { get; } =
        {
            NetworkCondition.C0_Baseline,
            NetworkCondition.C1_Lan,
            NetworkCondition.C2_GoodBroadband,
            NetworkCondition.C3_AvgBroadband,
            NetworkCondition.C4_Poor,
        };
        public bool RecordVideo => true;
        public bool CopyDebugLog => true;
        public MetricKey ApplicableMetrics => _inner.ApplicableMetrics;
        public void Setup(TestProcess server, TestProcess client) => _inner.Setup(server, client);
        public void Run(TestProcess server, TestProcess client, SyncMetrics metrics) => _inner.Run(server, client, metrics);
    }
}
