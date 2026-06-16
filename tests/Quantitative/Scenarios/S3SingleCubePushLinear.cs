using System.Collections.Generic;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Metrics;

namespace MonkeNet.Tests.Quantitative.Scenarios;

/// <summary>
/// S3-SingleCubePushLinear — identical setup to
/// <see cref="S3SingleCubePush"/>, but flips every smoother on the client to
/// <c>PredictionVisualSmoothing3D.PosDecayMode.Linear</c> via the
/// <c>smoother-decay-mode</c> harness command after spawn-settle. Used to
/// measure the freeze-frame-ratio / phase-lag / direction-mismatch
/// difference between exponential offset decay (the legacy behaviour) and
/// Q3 / UE-Linear-mode offset decay (constant correction velocity) under
/// the same player+cube setup, same network conditions, same input.
/// </summary>
public sealed class S3SingleCubePushLinear : IScenario
{
    public string Id => "S3-SingleCubePushLinear";

    public NetworkCondition[] Conditions => NetworkCondition.All;

    public bool RecordVideo => true;
    public bool CopyDebugLog => true;

    public MetricKey ApplicableMetrics => MetricKey.PhysicsBasic;

    private const int EntityTypeRigidPlayer = 3;
    private const int EntityTypeCube = 4;

    private int _playerId;
    private readonly List<int> _propIds = new();

    public void Setup(TestProcess server, TestProcess client)
    {
        int clientNetId = client.NetworkId;

        using var pDoc = server.Send(new
        {
            cmd = "spawn-entity",
            entity_type = EntityTypeRigidPlayer,
            authority = clientNetId,
            position = new[] { 0.0, 1.0, 4.0 },
        });
        _playerId = pDoc.RootElement.GetProperty("data").GetProperty("entityId").GetInt32();

        using var c = server.Send(new
        {
            cmd = "spawn-entity",
            entity_type = EntityTypeCube,
            authority = 0,
            position = new[] { 0.0, 0.55, 0.0 },
        });
        _propIds.Add(c.RootElement.GetProperty("data").GetProperty("entityId").GetInt32());

        server.WaitForTicks(120);

        // Flip every smoother on the client to Linear decay AFTER spawn
        // (smoothers don't exist until the entities are spawned). 0.1 s
        // window matches the legacy DecayTime default, so the wall-clock
        // budget for correction-to-zero is identical between the modes —
        // the difference being whether the decay is constant-velocity
        // (Linear) or exponentially asymptotic (Exponential).
        try
        {
            client.Send(new
            {
                cmd = "smoother-decay-mode",
                mode = "Linear",
                smoothingTime = 0.1,
            });
        }
        catch { /* not all client builds support the cmd — tolerate */ }

        try
        {
            client.Send(new
            {
                cmd = "set-camera",
                position = new[] { 6.0, 2.0, 2.0 },
                lookAt = new[] { 0.0, 0.8, 0.0 },
            });
        }
        catch { /* headless */ }
    }

    public void Run(TestProcess server, TestProcess client, SyncMetrics metrics)
    {
        const int RunTicks = 480;
        const int SampleInterval = 4;

        int anchor = client.ReadClientTick();
        var schedule = new List<object>
        {
            new { tick = anchor + 5,             moveX = 0.0, moveY = -1.0, yaw = 0.0, keys = 0 },
            new { tick = anchor + RunTicks - 5,  moveX = 0.0, moveY =  0.0, yaw = 0.0, keys = 0 },
        };
        client.Send(new { cmd = "set-input-schedule", entries = schedule });

        long startTick = S1_Idle.ReadServerTick(server);
        long endTick = startTick + RunTicks;
        int lastMispredict = 0;
        for (long t = startTick; t < endTick; t += SampleInterval)
        {
            try
            {
                var srv = MultiProcessTestBase.CaptureSampleStatic(server, (int)t);
                var cli = MultiProcessTestBase.CaptureSampleStatic(client, (int)t);

                S1_Idle.RecordError(srv, cli, metrics, _playerId);
                foreach (var eid in _propIds)
                    S1_Idle.RecordError(srv, cli, metrics, eid);

                if (cli.MispredictionsCount > lastMispredict)
                {
                    using var dDoc = client.Send(new { cmd = "rollback-depth-sample" });
                    int depth = dDoc.RootElement.GetProperty("data").GetProperty("depth").GetInt32();
                    metrics.RecordRollbackDepth(depth);
                    lastMispredict = cli.MispredictionsCount;
                }
            }
            catch { /* keep sampling */ }
            metrics.AddObservationTicks(SampleInterval);
            System.Threading.Thread.Sleep(20);
        }
    }
}
