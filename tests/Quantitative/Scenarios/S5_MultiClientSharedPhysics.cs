using System.Collections.Generic;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Metrics;

namespace MonkeNet.Tests.Quantitative.Scenarios;

/// <summary>
/// S5 — Multi-client shared physics. Server + 2 clients. Client A drives a
/// rigid-body player into a shared cube; Client B observes (passive, no
/// authority). Tests non-authoritative client reconciliation quality on the
/// same physics events — the observer should see the same snapshots and the
/// same M5 profile as the driver, modulo a small additional reconcile
/// frequency for non-owned entity prediction.
///
/// <para>
/// Metric of interest: M5 (position error) reported from the OBSERVER's view
/// of the shared cube vs. the server's authoritative state. The plan target
/// is that Client B's M5 RMS tracks Client A's to within a small constant.
/// </para>
/// </summary>
public sealed class S5_MultiClientSharedPhysics : IScenario
{
    public string Id => "S5-MultiClientSharedPhysics";
    public NetworkCondition[] Conditions { get; } =
    {
        NetworkCondition.C0_Baseline,
        NetworkCondition.C2_GoodBroadband,
        NetworkCondition.C3_AvgBroadband,
    };

    public bool RequiresObserver => true;
    // Multi-client shared physics exercises every metric — including M9
    // (missed-input) which is the metric most likely to show non-zero values
    // on the observer side when its remote-entity input cache misses.
    public MetricKey ApplicableMetrics => MetricKey.All;

    private const int EntityTypeRigidPlayer = 3;
    private const int EntityTypeCube = 4;

    private TestProcess _observer;
    private int _playerId;
    private int _cubeId;

    public void SetObserver(TestProcess observer) => _observer = observer;

    public void Setup(TestProcess server, TestProcess client)
    {
        int driverNetId = client.NetworkId;

        // Shared cube in front of the driver. Authority=0 (server-owned) so
        // both clients predict it; collisions on the driver propagate through
        // the server snapshot to the observer.
        using var cubeDoc = server.Send(new
        {
            cmd = "spawn-entity",
            entity_type = EntityTypeCube,
            authority = 0,
            position = new[] { 0.0, 0.6, 0.0 },
        });
        _cubeId = cubeDoc.RootElement.GetProperty("data").GetProperty("entityId").GetInt32();

        // Driver player 6m in front of the cube.
        using var pDoc = server.Send(new
        {
            cmd = "spawn-entity",
            entity_type = EntityTypeRigidPlayer,
            authority = driverNetId,
            position = new[] { 0.0, 1.0, 6.0 },
        });
        _playerId = pDoc.RootElement.GetProperty("data").GetProperty("entityId").GetInt32();

        // Park the observer on an idle input schedule so it doesn't try to
        // emit its own inputs (which would attempt to drive a player it
        // doesn't own — silently a no-op, but cleaner not to schedule).
        int obsAnchor = _observer.ReadClientTick();
        _observer.Send(new
        {
            cmd = "set-input-schedule",
            entries = new[] { new { tick = obsAnchor, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 } },
        });

        server.WaitForTicks(120);
    }

    public void Run(TestProcess server, TestProcess client, SyncMetrics metrics)
    {
        const int RunTicks = 600;            // 10 seconds @ 60 Hz
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
        int lastObsMispredict = 0;
        for (long t = startTick; t < startTick + RunTicks; t += SampleInterval)
        {
            try
            {
                var srv = MultiProcessTestBase.CaptureSampleStatic(server, (int)t);
                var obs = MultiProcessTestBase.CaptureSampleStatic(_observer, (int)t);
                // Position error measured from the OBSERVER's view of the
                // shared cube — that's the metric of interest for "remote
                // entity reconciliation quality" in a 2-client setup.
                S1_Idle.RecordError(srv, obs, metrics, _cubeId);
                S1_Idle.RecordError(srv, obs, metrics, _playerId);

                if (obs.MispredictionsCount > lastObsMispredict)
                {
                    using var dDoc = _observer.Send(new { cmd = "rollback-depth-sample" });
                    int depth = dDoc.RootElement.GetProperty("data").GetProperty("depth").GetInt32();
                    metrics.RecordRollbackDepth(depth);
                    lastObsMispredict = obs.MispredictionsCount;
                }
            }
            catch { /* keep sampling on transient errors */ }
            metrics.AddObservationTicks(SampleInterval);
            System.Threading.Thread.Sleep(25);
        }

        // Replace the driver's classification counts with the observer's at
        // the end of the cell — the metric of interest is the OBSERVER's
        // reconciliation behaviour, not the local driver's.
        using var classDoc = _observer.Send(new { cmd = "mispredict-classification-counts" });
        var d = classDoc.RootElement.GetProperty("data");
        metrics.SetMispredictTotals(
            externalForce: d.GetProperty("externalForce").GetInt32(),
            physicsNondet: d.GetProperty("physicsNondeterminism").GetInt32(),
            degradedNetwork: d.GetProperty("degradedNetwork").GetInt32());
    }
}
