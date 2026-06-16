using System.Collections.Generic;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Metrics;

namespace MonkeNet.Tests.Quantitative.Scenarios;

/// <summary>
/// S3-CubeStackPushT2KI — same setup as <see cref="S3CubeStackPush"/>
/// (player + 3-cube vertical stack), but flips every prop's
/// <c>InterpolationPolicy</c> to <c>KinematicInterpolation</c> via the
/// <c>set-policy-for-props</c> harness command after spawn-settle. Cubes
/// become frozen-kinematic; their pose is driven each
/// <c>OnPostPhysicsTick</c> from the snapshot history buffer. On contact
/// the proximity-sweep upgrade flips them to Resim, they unfreeze, seed
/// velocity from the latest snapshot, and run local Jolt for the
/// hysteresis window. Demote back to kinematic afterwards.
///
/// <para>
/// This is the real T2 cherry-pick of the <c>KinematicInterpolation</c>
/// policy — the architectural alternative to both the legacy Hysteresis
/// (predict-with-rollback) and the test-only <c>BypassResimUpgrade</c>
/// hack we measured earlier. The hypothesis (validated by the
/// no-resim-on-cubes vs predict comparison earlier): cubes should look
/// smoother because no <c>Reconcile</c> / <c>AbsorbBodyTeleport</c> chain
/// fires while they're at rest or following the snapshot stream; the
/// player should NOT mispredict on contact because the proximity-sweep
/// upgrade still flips the cubes to Resim before the contact tick — the
/// piece <c>BypassResimUpgrade</c> killed.
/// </para>
/// </summary>
public sealed class S3CubeStackPushT2KinematicInterp : IScenario
{
    public string Id => "S3-CubeStackPushT2KI";

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

        // Flip every prop (non-player) to KinematicInterpolation. The cubes
        // freeze immediately and start tracking the snapshot stream; the
        // player keeps its default Hysteresis (predict + rollback on
        // mispredict).
        try
        {
            client.Send(new { cmd = "set-policy-for-props", policy = "KinematicInterpolation" });
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
