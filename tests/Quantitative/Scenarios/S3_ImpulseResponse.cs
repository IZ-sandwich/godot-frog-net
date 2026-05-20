using System;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Metrics;

namespace MonkeNet.Tests.Quantitative.Scenarios;

/// <summary>
/// S3 — Impulse response. Server applies one known impulse to a passive cube
/// at a deterministic tick; client doesn't know about it until the snapshot
/// arrives. Isolates the external-force misprediction classification + M4
/// rollback depth + M7 post-rollback convergence time.
///
/// <para>
/// Expected: one external-force mispredict, M7 ≤ 7 ticks (GGPO-equivalent
/// convergence budget). Runs at a single condition because the metric of
/// interest (impulse latency response) saturates the prediction pipeline once
/// any latency is present.
/// </para>
/// </summary>
public sealed class S3_ImpulseResponse : IScenario
{
    public string Id => "S3-ImpulseResponse";
    public NetworkCondition[] Conditions { get; } = { NetworkCondition.C2_GoodBroadband };
    // Impulse-response is the only scenario that explicitly measures M7
    // (ticks-to-converge after a single applied impulse).
    public MetricKey ApplicableMetrics => MetricKey.All;

    private const int EntityTypeCube = 4;
    private int _cubeId;

    public void Setup(TestProcess server, TestProcess client)
    {
        using var doc = server.Send(new
        {
            cmd = "spawn-entity",
            entity_type = EntityTypeCube,
            authority = 0,
            position = new[] { 0.0, 1.0, 0.0 },
        });
        _cubeId = doc.RootElement.GetProperty("data").GetProperty("entityId").GetInt32();

        // Wait for the cube to fall + sleep so the only motion in the trace is
        // the impulse's response, not the settle.
        server.WaitForTicks(180);
    }

    public void Run(TestProcess server, TestProcess client, SyncMetrics metrics)
    {
        const int PreImpulseTicks = 60;
        const int PostImpulseTicks = 120;

        long startTick = S1_Idle.ReadServerTick(server);
        int beforeMispredicts;
        using (var doc = client.Send(new { cmd = "mispredict-count" }))
            beforeMispredicts = doc.RootElement.GetProperty("data").GetProperty("count").GetInt32();

        // Quiet observation window before the impulse — establishes baseline.
        for (long t = startTick; t < startTick + PreImpulseTicks; t += 4)
        {
            try
            {
                var srv = MultiProcessTestBase.CaptureSampleStatic(server, (int)t);
                var cli = MultiProcessTestBase.CaptureSampleStatic(client, (int)t);
                S1_Idle.RecordError(srv, cli, metrics, _cubeId);
            }
            catch { }
            metrics.AddObservationTicks(4);
            System.Threading.Thread.Sleep(20);
        }

        // Apply a lateral impulse — large enough to clearly exceed Gaffer's
        // 0.1 m no-correction floor within ~1 tick.
        server.Send(new
        {
            cmd = "apply-impulse",
            entity_id = _cubeId,
            deltaLinearVelocity = new[] { 5.0, 1.0, 0.0 },
        });
        long impulseTick = S1_Idle.ReadServerTick(server);

        // Measure post-rollback convergence: poll until body position error
        // drops back below the Gaffer floor.
        int convergenceTicks = -1;
        for (long t = impulseTick; t < impulseTick + PostImpulseTicks; t += 2)
        {
            try
            {
                var srv = MultiProcessTestBase.CaptureSampleStatic(server, (int)t);
                var cli = MultiProcessTestBase.CaptureSampleStatic(client, (int)t);
                S1_Idle.RecordError(srv, cli, metrics, _cubeId);
                var sEnt = S1_Idle.FindById(srv, _cubeId);
                var cEnt = S1_Idle.FindById(cli, _cubeId);
                if (sEnt != null && cEnt != null && convergenceTicks < 0)
                {
                    float err = (sEnt.Position - cEnt.Position).Length();
                    if (err < SyncMetrics.GafferNoCorrectionFloor && (t - impulseTick) > 2)
                    {
                        convergenceTicks = (int)(t - impulseTick);
                        metrics.RecordPostRollbackConvergence(convergenceTicks);
                    }
                }
                if (cli.MispredictionsCount > beforeMispredicts)
                {
                    using var dDoc = client.Send(new { cmd = "rollback-depth-sample" });
                    int depth = dDoc.RootElement.GetProperty("data").GetProperty("depth").GetInt32();
                    metrics.RecordRollbackDepth(depth);
                    beforeMispredicts = cli.MispredictionsCount;
                }
            }
            catch { }
            metrics.AddObservationTicks(2);
            System.Threading.Thread.Sleep(20);
        }
    }
}
