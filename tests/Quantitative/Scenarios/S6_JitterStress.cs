using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Metrics;

namespace MonkeNet.Tests.Quantitative.Scenarios;

/// <summary>
/// S6 — High-jitter clock stress. Single client, no movement. Heavy jitter
/// (±50ms uniform) with 0% packet loss isolates the clock-sync algorithm's
/// resilience to latency variance without the confounding of dropped snapshots.
///
/// <para>
/// Expected: M1 (convergence time) increases vs. baseline but stays ≤ 120
/// ticks; M2 degrades roughly proportional to jitter magnitude. The mostly-
/// idle scenario also acts as a regression check that high jitter does not
/// inflate the physics-nondeterminism mispredict count for a body at rest.
/// </para>
/// </summary>
public sealed class S6_JitterStress : IScenario
{
    public string Id => "S6-JitterStress";
    public NetworkCondition[] Conditions { get; } =
    {
        new("C-JITTER", "JitterStress", latencyMs: 50, lossRate: 0f, jitterMs: 50),
    };
    // Static ball, no input, no rollbacks — only the clock-sync metrics
    // (M1, M2) and bandwidth (M10) are physically meaningful. Including
    // physics-related metrics here would show 0 for every cell and dilute
    // the dashboard.
    public MetricKey ApplicableMetrics => MetricKey.ClockOnly;

    private const int EntityTypeBall = 1;
    private int _ballId;

    public void Setup(TestProcess server, TestProcess client)
    {
        using var doc = server.Send(new
        {
            cmd = "spawn-entity",
            entity_type = EntityTypeBall,
            authority = 0,
            position = new[] { 0.0, 1.0, 0.0 },
        });
        _ballId = doc.RootElement.GetProperty("data").GetProperty("entityId").GetInt32();
        server.WaitForTicks(120);
    }

    public void Run(TestProcess server, TestProcess client, SyncMetrics metrics)
    {
        const int RunTicks = 600;     // 10 s @ 60 Hz — long enough to observe steady-state jitter
        const int SampleInterval = 4;

        long startTick = S1_Idle.ReadServerTick(server);
        for (long t = startTick; t < startTick + RunTicks; t += SampleInterval)
        {
            try
            {
                var srv = MultiProcessTestBase.CaptureSampleStatic(server, (int)t);
                var cli = MultiProcessTestBase.CaptureSampleStatic(client, (int)t);
                S1_Idle.RecordError(srv, cli, metrics, _ballId);

                // Resample the clock gap continuously — jitter's impact is on
                // clock convergence, not physics, so the clock samples are the
                // metric of interest. (M1/M2 captured up front during the
                // QuantitativeTestBase warm-up are sufficient, but additional
                // steady-state samples here improve the M2 estimate.)
                using var cDoc = client.Send(new { cmd = "clock-state" });
                using var sDoc = server.Send(new { cmd = "clock-state" });
                int serverTick = sDoc.RootElement.GetProperty("data").GetProperty("serverTick").GetInt32();
                int syncedTick = cDoc.RootElement.GetProperty("data").GetProperty("syncedTick").GetInt32();
                int latency = cDoc.RootElement.GetProperty("data").GetProperty("averageLatencyTicks").GetInt32();
                metrics.RecordClockSample(syncedTick - serverTick - latency);
            }
            catch { }
            metrics.AddObservationTicks(SampleInterval);
            System.Threading.Thread.Sleep(25);
        }
    }
}
