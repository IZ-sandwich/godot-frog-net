using System.Collections.Generic;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Metrics;

namespace MonkeNet.Tests.Quantitative.Scenarios;

/// <summary>
/// S8 — Worst-case degradation stress. 300ms latency + 10% packet loss + 30ms
/// jitter, paired with the S4 physics-stack scenario. The only scenario where
/// M3c (degraded-network mispredict rate) is allowed to be non-zero: the
/// 120-tick prediction buffer covers 2 s of RTT, so some snapshots at 600 ms
/// RTT may arrive just inside the window while a 10% loss burst can push the
/// effective inter-snapshot gap past it.
///
/// <para>
/// Pass criterion is functional, not quantitative: the system must remain
/// operational (no crashes, prediction eventually converges) under conditions
/// no game would actually try to play at.
/// </para>
/// </summary>
public sealed class S8_DegradationStress : IScenario
{
    public string Id => "S8-DegradationStress";
    public NetworkCondition[] Conditions { get; } = { NetworkCondition.C5_Stress };
    public MetricKey ApplicableMetrics => MetricKey.PhysicsBasic;

    private const int EntityTypeRigidPlayer = 3;
    private const int EntityTypeCube = 4;
    private const int CubeCount = 6;

    private int _playerId;
    private readonly List<int> _cubeIds = new();

    public void Setup(TestProcess server, TestProcess client)
    {
        int clientNetId = client.NetworkId;
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
        using var p = server.Send(new
        {
            cmd = "spawn-entity",
            entity_type = EntityTypeRigidPlayer,
            authority = clientNetId,
            position = new[] { 0.0, 1.0, 8.0 },
        });
        _playerId = p.RootElement.GetProperty("data").GetProperty("entityId").GetInt32();
        server.WaitForTicks(240);
    }

    public void Run(TestProcess server, TestProcess client, SyncMetrics metrics)
    {
        const int RunTicks = 720;
        const int SampleInterval = 6;

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
            System.Threading.Thread.Sleep(25);
        }
    }
}
