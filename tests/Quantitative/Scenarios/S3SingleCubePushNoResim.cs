using System.Collections.Generic;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Metrics;

namespace MonkeNet.Tests.Quantitative.Scenarios;

/// <summary>
/// S3-SingleCubePushNoResim — same setup as
/// <see cref="S3SingleCubePush"/>, but flips
/// <c>ClientPredictionManager.MaxRollbackTicks = 0</c> via the
/// <c>max-rollback-ticks</c> harness command after spawn-settle.
/// With a zero-depth rollback buffer, every incoming snapshot is older
/// than the (empty) prediction history and routes through
/// <c>BlendToAuthViaPvb</c> instead of <c>RollbackAndResimulate</c>.
///
/// <para>
/// Effectively turns prediction OFF: the client doesn't run any
/// forward resim against an authoritative snapshot, just snaps every
/// affected body straight to the auth pose and starts a Projective
/// Velocity Blend on the smoother. This is what a snapshot-
/// interpolation-only client would do (Source-Engine remote entities,
/// Unreal replicated movement on non-locally-controlled actors).
/// </para>
///
/// <para>
/// Used to answer "what does it look like if we just never predict?"
/// The expected trade-offs vs the predicting modes:
/// </para>
/// <list type="bullet">
/// <item>Player visual lags behind input by ~1-2 RTT (no prediction)</item>
/// <item>No mispredict rollback ripple — every snapshot is just a snap+blend</item>
/// <item>M14 / M16 should be smoother in steady state but the player feel suffers</item>
/// </list>
/// </summary>
public sealed class S3SingleCubePushNoResim : IScenario
{
    public string Id => "S3-SingleCubePushNoResim";

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

        // Force every subsequent snapshot through BlendToAuthViaPvb by
        // setting MaxRollbackTicks=0. Triggered AFTER spawn-settle so the
        // initial spawn-tick alignment / clock sync had a real rollback
        // budget to work with — the no-resim semantics only apply during
        // the observation run.
        try
        {
            client.Send(new { cmd = "max-rollback-ticks", ticks = 0 });
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
