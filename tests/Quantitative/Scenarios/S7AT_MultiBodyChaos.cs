using System.Collections.Generic;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Metrics;

namespace MonkeNet.Tests.Quantitative.Scenarios;

/// <summary>
/// S7-AT — S7 Multi-body chaos under the <b>AuthorityTransfer</b> policy.
/// Same scenario and network conditions as S7, but cubes + balls are
/// spawned with the <c>policy=authority-transfer</c> metadata so contact
/// causes the client to claim ownership (Photon Fusion 2 style), and the
/// server forwards the body's state from the owner via the relay path
/// (UseAuthorityTransferRelay on the cube/ball OwnershipPolicy resource).
///
/// <para>
/// Direct comparison target for the default S7 (Hysteresis) and S7-BV so
/// the quantitative summary CSV exposes how the relay-based authority
/// model trades off divergence vs. on-contact handoff cost against the
/// rollback-on-contact (Hysteresis) and per-tick-blend (BlendedVelocity)
/// models under high entity counts.
/// </para>
/// </summary>
public sealed class S7AT_MultiBodyChaos : IScenario
{
    public string Id => "S7AT-MultiBodyChaos";
    public NetworkCondition[] Conditions { get; } =
    {
        NetworkCondition.C0_Baseline,
        NetworkCondition.C2_GoodBroadband,
        NetworkCondition.C3_AvgBroadband,
        NetworkCondition.C4_Poor,
    };

    public bool RecordVideo => true;
    public bool CopyDebugLog => true;

    public MetricKey ApplicableMetrics => MetricKey.PhysicsBasic;

    private const int EntityTypeBall = 1;
    private const int EntityTypeRigidPlayer = 3;
    private const int EntityTypeCube = 4;

    private const int CubeCount = 20;
    private const int BallCount = 20;

    // Policy override metadata. LocalRigidPropPrediction reads this in
    // OnEntitySpawned (~line 131) and sets Policy = AuthorityTransfer for any
    // cube/ball whose spawn metadata contains "policy=authority-transfer".
    private const string PolicyMetadata = "policy=authority-transfer";

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
            position = new[] { 0.0, 1.0, 8.0 },
        });
        _playerId = pDoc.RootElement.GetProperty("data").GetProperty("entityId").GetInt32();

        for (int i = 0; i < CubeCount; i++)
        {
            float x = -2.5f + (i % 5) * 1.1f;
            float y = 0.6f + (i / 5) * 1.1f;
            using var d = server.Send(new
            {
                cmd = "spawn-entity",
                entity_type = EntityTypeCube,
                authority = 0,
                position = new[] { (double)x, y, 0.0 },
                metadata = PolicyMetadata,
            });
            _propIds.Add(d.RootElement.GetProperty("data").GetProperty("entityId").GetInt32());
        }
        for (int i = 0; i < BallCount; i++)
        {
            float x = 2.5f - (i % 5) * 1.1f;
            float y = 0.6f + (i / 5) * 1.1f;
            using var d = server.Send(new
            {
                cmd = "spawn-entity",
                entity_type = EntityTypeBall,
                authority = 0,
                position = new[] { (double)x, y, 0.0 },
                metadata = PolicyMetadata,
            });
            _propIds.Add(d.RootElement.GetProperty("data").GetProperty("entityId").GetInt32());
        }

        server.WaitForTicks(240);

        try
        {
            client.Send(new
            {
                cmd = "set-camera",
                position = new[] { 12.0, 4.0, 5.0 },
                lookAt = new[] { 0.0, -1.0, 4.0 },
            });
        }
        catch { /* headless: no camera to set */ }
    }

    public void Run(TestProcess server, TestProcess client, SyncMetrics metrics)
    {
        const int RunTicks = 900;
        const int SampleInterval = 6;

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
            System.Threading.Thread.Sleep(25);
        }
    }
}
