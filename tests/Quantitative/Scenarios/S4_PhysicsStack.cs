using System.Collections.Generic;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Metrics;

namespace MonkeNet.Tests.Quantitative.Scenarios;

/// <summary>
/// S4 — Physics stack collision. Rigid player walks into a 6-cube tower. The
/// canonical "physics interaction under bad conditions" scenario from the
/// existing MispredictTests, extended to sweep the full network-condition
/// matrix instead of running at C0 only.
///
/// <para>
/// Per the plan: M3b grows monotonically with latency (more prediction horizon
/// → more stacked-body uncertainty); M3c = 0% at ≤ 200ms latency; M5 RMS &lt;
/// 0.3m under ≤ 300ms / 5% loss.
/// </para>
/// </summary>
public sealed class S4_PhysicsStack : IScenario
{
    public string Id => "S4-PhysicsStack";
    public NetworkCondition[] Conditions => NetworkCondition.All;
    // Physics interaction without an explicit impulse → M7 N/A; all others apply.
    public MetricKey ApplicableMetrics => MetricKey.PhysicsBasic;

    private const int EntityTypeRigidPlayer = 3;
    private const int EntityTypeCube = 4;
    private const int CubeCount = 6;

    private int _playerId;
    private readonly List<int> _cubeIds = new();

    public void Setup(TestProcess server, TestProcess client)
    {
        int clientNetId = client.NetworkId;

        // Build a 6-cube vertical tower in the player's path. Heights stagger
        // so the upper cubes briefly stack before they cascade off the lower.
        for (int i = 0; i < CubeCount; i++)
        {
            using var d = server.Send(new
            {
                cmd = "spawn-entity",
                entity_type = EntityTypeCube,
                authority = 0,
                position = new[] { 0.0, 0.6 + i * 1.05, 0.0 },
            });
            _cubeIds.Add(d.RootElement.GetProperty("data").GetProperty("entityId").GetInt32());
        }

        // Player 8m in front; faces the tower along -Z.
        using var p = server.Send(new
        {
            cmd = "spawn-entity",
            entity_type = EntityTypeRigidPlayer,
            authority = clientNetId,
            position = new[] { 0.0, 1.0, 8.0 },
        });
        _playerId = p.RootElement.GetProperty("data").GetProperty("entityId").GetInt32();

        // Let the tower settle into a stable resting state before the run.
        server.WaitForTicks(240);
    }

    public void Run(TestProcess server, TestProcess client, SyncMetrics metrics)
    {
        const int RunTicks = 720;             // 12 seconds @ 60 Hz
        const int SampleInterval = 4;

        int anchor = client.ReadClientTick();
        client.Send(new
        {
            cmd = "set-input-schedule",
            entries = new[]
            {
                new { tick = anchor + 5,            moveX = 0.0, moveY = -1.0, yaw = 0.0, keys = 0 },
                new { tick = anchor + RunTicks - 5, moveX = 0.0, moveY =  0.0, yaw = 0.0, keys = 0 },
            },
        });

        long startTick = S1_Idle.ReadServerTick(server);
        int lastMispredict = 0;
        for (long t = startTick; t < startTick + RunTicks; t += SampleInterval)
        {
            try
            {
                var srv = MultiProcessTestBase.CaptureSampleStatic(server, (int)t);
                var cli = MultiProcessTestBase.CaptureSampleStatic(client, (int)t);
                S1_Idle.RecordError(srv, cli, metrics, _playerId);
                foreach (var eid in _cubeIds)
                    S1_Idle.RecordError(srv, cli, metrics, eid);

                if (cli.MispredictionsCount > lastMispredict)
                {
                    using var dDoc = client.Send(new { cmd = "rollback-depth-sample" });
                    int depth = dDoc.RootElement.GetProperty("data").GetProperty("depth").GetInt32();
                    metrics.RecordRollbackDepth(depth);
                    lastMispredict = cli.MispredictionsCount;
                }
            }
            catch { }
            metrics.AddObservationTicks(SampleInterval);
            System.Threading.Thread.Sleep(20);
        }
    }
}
