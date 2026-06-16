using System.Collections.Generic;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Metrics;

namespace MonkeNet.Tests.Quantitative.Scenarios;

/// <summary>
/// S3-CubeStackPushHybrid — same setup as <see cref="S3CubeStackPush"/>
/// (player + 3-cube vertical stack), but uses the hybrid policy: the
/// player keeps predicting + rolling back normally, while every prop
/// (anything whose Authority != this client's net id) has
/// <c>ClientPredictedEntity.BypassResimUpgrade = true</c>. Props stay in
/// Interpolate tier forever and get blend-toward-auth on each snapshot
/// instead of being upgraded to Resim on contact.
///
/// <para>
/// This is the empirical answer to "what if we adopted T2's
/// KinematicInterpolation policy on just the cubes?" — the player keeps
/// crisp local-input prediction while the cubes' visual is driven purely
/// by the snapshot stream + smoother PVB. The expected outcome based on
/// the predict-vs-NoResim 3-cube comparison: cube-side artefacts
/// (freeze-then-sprint on contact cascade) should go away while the
/// player retains its input responsiveness.
/// </para>
/// </summary>
public sealed class S3CubeStackPushHybridPredictPlayer : IScenario
{
    public string Id => "S3-CubeStackPushHybrid";

    public NetworkCondition[] Conditions => NetworkCondition.All;

    public bool RecordVideo => true;
    public bool CopyDebugLog => true;

    public MetricKey ApplicableMetrics => MetricKey.PhysicsBasic;

    private const int EntityTypeRigidPlayer = 3;
    private const int EntityTypeCube = 4;

    private static readonly (float x, float y, float z)[] StackPositions =
    {
        (0f, 0.55f, 0f),
        (0f, 1.65f, 0f),
        (0f, 2.75f, 0f),
    };

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

        foreach (var (x, y, z) in StackPositions)
        {
            using var d = server.Send(new
            {
                cmd = "spawn-entity",
                entity_type = EntityTypeCube,
                authority = 0,
                position = new[] { (double)x, y, z },
            });
            _propIds.Add(d.RootElement.GetProperty("data").GetProperty("entityId").GetInt32());
        }

        server.WaitForTicks(180);

        // After spawn-settle: flip BypassResimUpgrade on every non-player
        // entity. The player keeps predicting + rolling back normally; the
        // cubes stay Interpolate forever and blend toward auth on each
        // snapshot via HandleInterpolateReconciliation.
        try
        {
            client.Send(new { cmd = "bypass-resim-for-props", enabled = true });
        }
        catch { /* harness lacks the cmd — tolerate */ }

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
