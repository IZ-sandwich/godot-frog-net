using System.Collections.Generic;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Metrics;

namespace MonkeNet.Tests.Quantitative.Scenarios;

/// <summary>
/// S3-SingleCubePush — minimal isolation of the player-vs-cube contact
/// problem. Same setup as <see cref="S3CubeStackPush"/> but with ONE cube
/// instead of a 3-tall stack so the contact event is uncomplicated by
/// stack-collapse dynamics or chained Resim upgrades. Used to test the
/// hypothesis that the player's velocity discontinuity at first contact
/// (observed at ~4.94 m/s/tick in the 3-cube case) is intrinsic to the
/// single contact event and not a stack-cascade artefact.
///
/// <para>
/// Cube sits at the same world position as the bottom cube in S3CubeStackPush
/// so the player's approach distance, walking velocity, and first-contact
/// tick are all identical between the two scenarios — only the contact
/// dynamics after first-touch differ.
/// </para>
/// </summary>
public sealed class S3SingleCubePush : IScenario
{
    public string Id => "S3-SingleCubePush";

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

        // Single cube at the same XYZ as the bottom of the 3-stack so first
        // contact happens at the same wall-clock and tick as the stack
        // scenario — only the post-contact dynamics differ.
        using var c = server.Send(new
        {
            cmd = "spawn-entity",
            entity_type = EntityTypeCube,
            authority = 0,
            position = new[] { 0.0, 0.55, 0.0 },
        });
        _propIds.Add(c.RootElement.GetProperty("data").GetProperty("entityId").GetInt32());

        // 120 ticks (2 s) is plenty for a single floor-resting cube — no
        // stack-stabilisation needed.
        server.WaitForTicks(120);

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
            catch { /* keep sampling on transient errors */ }
            metrics.AddObservationTicks(SampleInterval);
            System.Threading.Thread.Sleep(20);
        }
    }
}
