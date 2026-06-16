using System.Collections.Generic;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Metrics;

namespace MonkeNet.Tests.Quantitative.Scenarios;

/// <summary>
/// S3-CubeStackPush — focused isolation of the player-pushes-a-stack artifact
/// that S7-MultiBodyChaos shows alongside 40 other moving bodies, but with
/// just 3 cubes so the visible behaviour is easy to read from the MP4 and
/// the per-condition smoothness numbers aren't averaged out by the rest of
/// the chaos pile.
///
/// <para>
/// Scenario: player spawns 4 m in front of a 3-cube vertical stack, walks
/// forward at constant velocity, contacts the bottom cube around tick ~250,
/// pushes the stack ~3-4 m over the next ~400 ticks while the stack
/// progressively topples / rolls / scatters. The contact + push period is
/// exactly the part that looks bad in C1 (the user-reported case) and
/// progressively better at C2/C3/C4 — under bad networks the snap-to-auth
/// path masks the per-snapshot reconcile saw-tooth behind a constant body
/// teleport which is rendered smoothly by the visual-offset decay, while
/// under C1 (50 ms latency, no loss) the body actually moves at its real
/// velocity AND fires per-snapshot Reconciles, exposing every chained
/// AbsorbBodyTeleport + blend-step + render-pacing artifact.
/// </para>
///
/// <para>
/// Compared to S7: identical player + cube spawn types, identical input
/// schedule (walk forward), identical metrics (M3 mispredict, M4 rollback
/// depth, M5 position drift, M14 visual smoothness). The cube count is the
/// only variable so a side-by-side strip-plot reads as "what does the same
/// push event look like at 3 cubes vs 40 cubes". Runs the full network-
/// condition matrix (C0-C5 + CJITTER) so the C1-vs-C4 inversion the user
/// reports is captured.
/// </para>
/// </summary>
public sealed class S3CubeStackPush : IScenario
{
    public string Id => "S3-CubeStackPush";

    // Full matrix - the headline observation is that C1 looks worse than C4,
    // so every condition needs a row in the dashboard for the inversion to
    // be visible. Adding C5 and CJITTER on top of S7's normal C0-C4 set gives
    // the high-stress and jitter-isolated profiles too.
    public NetworkCondition[] Conditions => NetworkCondition.All;

    // MP4 + debug log are the whole point — this scenario exists to make the
    // visible artifact reproducible and inspectable, not just measurable.
    public bool RecordVideo => true;
    public bool CopyDebugLog => true;

    // M3/M4/M5/M14 are the relevant ones; impulse-response (M7) and clock-
    // sync (M1/M2) don't apply.
    public MetricKey ApplicableMetrics => MetricKey.PhysicsBasic;

    private const int EntityTypeRigidPlayer = 3;
    private const int EntityTypeCube = 4;

    // 3 cubes, ~1 m each, stacked vertically along Y. Same XZ position so they
    // settle into a perfectly aligned stack; bottom cube sits on the floor.
    // Y values chosen to leave a small gap (0.05 m) between stacked cubes so
    // the settle phase has something to converge — exactly-touching y values
    // would still settle but slower, and the eye can't see the difference
    // in the recorded MP4.
    private static readonly (float x, float y, float z)[] StackPositions =
    {
        (0f, 0.55f, 0f),  // bottom
        (0f, 1.65f, 0f),  // middle
        (0f, 2.75f, 0f),  // top
    };

    private int _playerId;
    private readonly List<int> _propIds = new();

    public void Setup(TestProcess server, TestProcess client)
    {
        int clientNetId = client.NetworkId;

        // Player starts 4 m back from the stack so a forward walk contacts
        // the bottom cube around tick ~250 (4 m at 1.5 m/s ≈ 160 ticks of
        // approach after the 60-tick settle). Closer than S7's 8 m so the
        // contact period dominates the 480-tick observation window instead
        // of being a 50-tick blip at the end.
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

        // Settle phase — let the 3-cube stack come to rest. 180 ticks (3 s)
        // is enough for a 3-tall stack even with the 5 cm gap; the chaos pile
        // in S7 needs 240 because it has interlocking grids.
        server.WaitForTicks(180);

        // Side-on camera so the push direction (-Z) reads as horizontal
        // motion in the recorded MP4. Pointed at the stack's base so all 3
        // cubes are in frame even after the top one tumbles forward.
        try
        {
            client.Send(new
            {
                cmd = "set-camera",
                position = new[] { 6.0, 2.0, 2.0 },
                lookAt = new[] { 0.0, 0.8, 0.0 },
            });
        }
        catch { /* headless: no camera to set */ }
    }

    public void Run(TestProcess server, TestProcess client, SyncMetrics metrics)
    {
        // 480 ticks = 8 s @ 60 Hz. Long enough that the push + tumble + roll
        // window dominates; short enough that the per-cell runtime stays
        // under ~20 s including ffmpeg finalisation.
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

                // Per-entity position errors — player + 3 cubes. Averaging
                // across just 4 bodies (vs 41 in S7) keeps small per-cube
                // Jolt noise visible in M5 instead of averaging it out, so
                // tail spikes from a single cube's bad-network reconcile
                // show up in the summary instead of being washed away.
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
