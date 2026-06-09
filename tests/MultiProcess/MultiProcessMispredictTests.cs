using System.Collections.Generic;
using GdUnit4;
using Godot;
using MonkeNet.Tests.Infrastructure;
using MonkeNet.Tests.Infrastructure.Artifacts;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.MultiProcess;

/// <summary>
/// MP-MISPREDICT-01: Multi-process rigid-player misprediction test. Reproduces the
/// user-reported scenario from monke-net_2026-05-11_14-29-32_pid24564.log: build a
/// cube TOWER by spawning many server-authoritative cubes in a vertical stack, then
/// have a client-owned rigid player run into the tower MULTIPLE TIMES while
/// JUMPING. The user observed ~39 mispredictions across this scenario in real
/// gameplay; the test asserts the number stays under a generous budget that catches
/// regressions while leaving room for cross-process Jolt jitter.
///
/// Artefacts written under <c>TestResults/MispredictPlots/</c>:
///   - tower_run.csv   (long-form: tick, mispredictionsCount, eid, type, pos, vel)
///   - tower_run.svg   (player + cube Z trajectories, mispredict markers)
///   - tower_run.mp4   (h264/mp4 video recorded by an external ffmpeg gdigrab
///                      sidecar capturing the WINDOWED client process. Unlike
///                      Godot's --write-movie which gated engine timing on the
///                      encoder and broke clock alignment, this approach lets
///                      the engine run at its native speed; ffmpeg drops frames
///                      if it can't keep up, but physics is never delayed.
///                      Requires ffmpeg on PATH or FFMPEG_BIN env var.)
///
/// ── 2026-05-12 root-cause investigation ──────────────────────────────────────
///
/// REMAINING root cause is independent-Jolt-instance divergence in stacked
/// contact dynamics. Evidence at tick 465 (stack settling, no player nearby):
///   eid=1 bottom cube on floor: |posDiff|=0.000 mm  (bit-identical)
///   eid=2..5 stacked above:     0.2 → 7.7 mm        (linear stair-step)
///   eid=6 top cube:             13.3 mm
///   eid=7 player at rest:       0.000 mm            (bit-identical)
/// Bodies at rest are bit-perfect across processes; bodies in stacked contact
/// drift ~0.5 mm/tick from contact-resolution FP differences. mDeterministicSimulation
/// is on (in-process contact ordering) but JPH_CROSS_PLATFORM_DETERMINISTIC is
/// OFF in the Godot Jolt build, so cross-process FP arithmetic isn't bit-equal.
///
/// At tick 530 the player encounters the (now slightly-divergent) tower with
/// IDENTICAL input on both sides but a different CONTACT SET (1 vs 2 colliders).
/// Different contact count → different constraint forces → different player
/// velocity → next tick drifts more. That's the feedback loop producing the
/// clusters of mispredictions at ticks 532-547, 611-643, 666-729 etc. Inputs,
/// body insertion order, push-impulse computation, and basic FP integration
/// are all verified deterministic — only stacked-contact resolution diverges.
///
/// ── Possible next steps toward Photon-Fusion-2-style architecture ────────────
///
/// Fusion 2 sidesteps this entirely: server-authoritative rigid bodies are
/// interpolated-only on the client, never locally simulated. Fusion's playbook
/// for "responsive feel when interacting with physics props" is state-authority
/// transfer: when the local player engages with an object, ownership flips to
/// the player's client (which then is the authority, not predicting), and
/// flips back when they disengage. To approximate this:
///
/// 1. Make EntityType=4 cubes server-authoritative interpolated by default.
///    Set ClientDummyScene = DummyCube (interpolated, no local Jolt sim) so
///    cubes the player isn't touching never run client-side physics. Eliminates
///    the background mm-level drift the top of the stack accumulates.
///    Trade-off: client-visible push lag = snapshot RTT (~100 ms).
///
/// 2. Wire authority-transfer-on-contact for cubes. The plumbing exists:
///    ClientEntityManager.RequestAuthorityAnticipated already does the dummy→
///    predicted scene swap with provisional authority. Trigger it from the
///    rigid player's contact callback when entering contact with a cube; the
///    server-side approver in ServerEntityManager.OwnershipApprover decides
///    whether to grant. On grant, the client owns and simulates the cube; on
///    release (player no longer in contact for N ticks), authority transfers
///    back to the server and the cube returns to interpolated.
///
/// 3. (Cheaper interim) Snap distant cubes to auth state per snapshot, scaled
///    by distance to the local player. Already partially done by
///    LocalRigidPropPrediction.SyncSleepState for sleeping bodies — extend to
///    "always snap when player is &gt; 3 m away" so the top-of-stack drift
///    can't accumulate while the player is across the map. Cheap, no
///    architectural change, but doesn't help once the player engages.
///
/// 4. Replace LocalRigidPlayer with a hand-written deterministic controller
///    (capsule-cast + slide math, no RigidBody3D, no Jolt for the player).
///    Fusion's NetworkCharacterController works this way precisely because
///    PhysX/Jolt isn't reliably deterministic across processes. Keeps the
///    player bit-identical between client and server given identical input,
///    eliminates the contact-set-divergence feedback loop on the player.
///    Player-cube interaction then goes through ApplyImpulse rather than
///    Jolt-resolved rigid-body contact, which is already easier to make
///    deterministic (impulse magnitude is a function of input, not body state).
///
/// 5. (Nuclear) Rebuild Godot with JPH_CROSS_PLATFORM_DETERMINISTIC defined
///    in modules/jolt_physics/SCsub. Real fix for the underlying cause —
///    Jolt's bit-deterministic mode forces software FMA emulation and uses
///    the in-tree platform-independent sin/cos/sqrt instead of libm. Costs
///    ~10-15% physics perf. Fusion CAN'T do this (PhysX is closed-source);
///    you can. Combine with #1-2 above and the misprediction budget could
///    realistically drop to single digits.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class MultiProcessMispredictTests : MultiProcessTestBase
{
    protected override string ArtifactSubdir => "MispredictPlots";

    [BeforeTest] public void SetUp() => SetUpInternal();
    [AfterTest]  public void TearDown() => TearDownInternal();

    private const byte EntityTypeRigidPlayer = 3;
    private const byte EntityTypeCube = 4;

    // Tower geometry. Cubes are 1x1x1 BoxShape3D; spaced vertically with a real
    // gap (1.5 m centre-to-centre, so 0.5 m of air between cube tops/bottoms)
    // so each cube is in free-fall when spawned and drops onto the stack as the
    // settle period progresses. TowerBaseY is above the floor (Y≈-2.08 in this
    // demo) so even the bottom cube drops before resting. Z=-3 places the tower
    // in the player's forward path.
    private const int TowerCubeCount = 6;
    private const float CubeSpacingY = 1.5f;
    private const float TowerBaseY = 0.5f;
    // Tower close enough to the player's spawn (Z=+1.5) that a normal forward
    // run (~5 m/s) reaches it within ~0.5 s and visibly knocks cubes around in
    // the recorded video. Was -3 originally, which left the player charging at
    // empty space for most of the recording.
    private const float TowerZ = -1.5f;

    // Sampling cadence + total run length. 720 ticks = 12s at 60 Hz physics; long
    // enough for 3 forward+back passes through the tower with jumps.
    private const int SnapshotIntervalTicks = 4;
    private const int RunTicks = 720;
    // Brief warmup before any entity spawns so the clock-sync subsystem stabilises
    // (without it, the first snapshot races registration and produces a misleading
    // misprediction unrelated to the bug under test). After the warmup we count
    // mispredictions from the player's spawn tick onward, including the first
    // spawn-fall misprediction (gravity-clock-offset; see user log pid24564 at
    // server tick 395 where predicted Y=-0.003 vs authoritative Y=-0.057).
    private const int SnapshotArmTicks = 60;
    // Ticks given to the tower to fall onto the floor and stop bouncing before
    // we spawn the player + drive input.
    private const int TowerSettleTicks = 120;
    private const int MispredictBudget = 25;

    // MP-MISPREDICT-01 ──────────────────────────────────────────────────────────
    // Build a cube tower, then drive the rigid player through 3 forward-and-back
    // passes while pulsing the Jump key. Counts mispredictions for the duration
    // of the interaction (excluding the settle window) and asserts the count
    // stays under a budget. The client subprocess records video directly via
    // an in-process ffmpeg sidecar, so the AVI shows the ACTUAL production
    // rendering of the client's view (LocalRigidPlayer + DummyCubes) with
    // collision-shape wireframes drawn by --debug-collisions.
    [TestCase]
    public void MultiProcess_RigidPlayer_RunsIntoTowerWhileJumping_MispredictionsStayUnderBudget()
    {
        RunTowerScenario(label: "tower_run", cubeMetadata: "",
            mispredictBudget: MispredictBudget, recordVideo: true);
    }

    // ── Policy-comparison tests ────────────────────────────────────────────────
    // Same scenario re-run under each of the three configurable client-side
    // prediction policies. The point is NOT to assert a specific mispredict
    // count — different policies will reach a different equilibrium count;
    // BlendedVelocity in particular never falls below the threshold (every
    // sub-snap drift increments the counter) so its number is inherently
    // larger. Each test uses a generous per-policy budget so the assertions
    // catch genuine regressions while leaving room for the policy's expected
    // characteristic. The interesting comparison is the SVG/CSV trace:
    // body deviation, visual jump magnitude, and the time-series of
    // misprediction events are visually distinguishable across the three
    // policies.

    // Hysteresis (default, T2-style): cubes start in Interpolate tier, upgrade
    // to Resim for 15 ticks on player contact, full rollback on misprediction
    // during the upgrade window. Same as the unparameterised test above but
    // tagged via metadata so the run is comparable to the other policy traces.
    [TestCase]
    public void MultiProcess_PolicyHysteresis_RunsIntoTower_TraceComparison()
    {
        RunTowerScenario(label: "policy_hysteresis", cubeMetadata: "policy=hysteresis",
            mispredictBudget: MispredictBudget, recordVideo: true);
    }

    // AlwaysPredict (Netick / "always predict everything" pattern): cubes are
    // permanently Resim and every snapshot drift past threshold triggers a
    // full rollback. Expected to mispredict MORE than Hysteresis because
    // there's no kinematic-interp absorb path for idle cubes — the per-test
    // budget is sized to that.
    [TestCase]
    public void MultiProcess_PolicyAlwaysPredict_RunsIntoTower_TraceComparison()
    {
        RunTowerScenario(label: "policy_always_predict", cubeMetadata: "policy=always-predict",
            mispredictBudget: MispredictBudget * 3, recordVideo: true);
    }

    // AuthorityTransfer (Photon Fusion 2 style): cubes default to Interpolate;
    // on player contact the client requests ownership and the server flips
    // Authority via AuthorityChangedMessage. While owned the body is locally
    // simulated and the local sim IS authoritative. Budget is loose because:
    // (a) the PLAYER is also Resim-locally-owned and mispredicts on every
    // cross-process Jolt contact-resolution divergence, contributing the bulk
    // of the count; (b) between contact phases the cube briefly returns to
    // server authority and can drift in Interpolate tier before the next
    // request. The trace shows the AUTH-TRANSFER log lines and Authority
    // flips on the cubes — that's the value-add over Hysteresis.
    [TestCase]
    public void MultiProcess_PolicyAuthorityTransfer_RunsIntoTower_TraceComparison()
    {
        RunTowerScenario(label: "policy_authority_transfer", cubeMetadata: "policy=authority-transfer",
            mispredictBudget: 300, recordVideo: true);
    }

    // BlendedVelocity (UE5 PredictiveInterpolation style): hybrid — cubes
    // default to Interpolate (kinematic, snapshot-driven), upgrade to Resim
    // on local-client contact via the hysteresis counter, and during the
    // Resim window mispredictions are absorbed by a per-entity velocity
    // blend + positional correction (no full-scene rollback, no pose
    // teleport). Outside the contact window the cube tracks server snapshots
    // exactly; on observers (who don't drive the player) the upgrade never
    // fires and observer mispredicts approach zero. This matches UE5's
    // documented use of PredictiveInterpolation for non-pawn rigid bodies —
    // observers interpolate, the active toucher predicts.
    [TestCase]
    public void MultiProcess_PolicyBlendedVelocity_RunsIntoTower_TraceComparison()
    {
        RunTowerScenario(label: "policy_blended_velocity", cubeMetadata: "policy=blended-velocity",
            mispredictBudget: MispredictBudget * 20, recordVideo: true);
    }

    /// <summary>Shared body for the four MP-MISPREDICT tests. Spawns a cube tower
    /// (each cube tagged with <paramref name="cubeMetadata"/> so LocalRigidPropPrediction
    /// can vary its policy), spawns the rigid player owned by the client, drives the
    /// scripted "3 forward+back passes while jumping" input schedule, captures samples,
    /// emits CSV+SVG plots under TestResults/MispredictPlots/<paramref name="label"/>.*
    /// and asserts the mispredict count stays under <paramref name="mispredictBudget"/>.</summary>
    private void RunTowerScenario(string label, string cubeMetadata, int mispredictBudget, bool recordVideo)
    {
        if (Orch == null) return;

        var (server, client, observer) = SpawnTriad(label, recordVideo: recordVideo);

        // Brief warmup so clock-sync stabilises before any entities exist.
        server.WaitForTicks(SnapshotArmTicks);

        // Pre-spawn the cube tower (auth=0 → DummyCube on the client, interpolated
        // from server snapshots). Cubes are spawned with vertical gaps so each
        // free-falls onto the stack. Per-cube metadata threads the policy
        // selector into LocalRigidPropPrediction's _Ready override.
        var cubeEids = new List<int>();
        for (int i = 0; i < TowerCubeCount; i++)
        {
            float y = TowerBaseY + i * CubeSpacingY;
            cubeEids.Add(SpawnEntity(server, EntityTypeCube, authority: 0, 0f, y, TowerZ, metadata: cubeMetadata));
        }
        server.WaitForTicks(TowerSettleTicks);

        // Spawn at Y=0 to match the user's default spawn (pid24564 log shows the
        // player initially at pos=(0,0,0), then free-falling onto floor at Y≈-2.08).
        int playerEid = SpawnEntity(server, EntityTypeRigidPlayer, client.NetworkId, 0f, 0f, 1.5f);

        // Anchor the rest of the scenario to a deterministic CLIENT tick.
        // Reading "client tick right now" + adding a fixed offset means the
        // input schedule lands on the same tick across runs regardless of
        // how long spawn took on the wall clock. We then wait for the
        // anchor tick to actually arrive on the client (≤5 ms poll resolution
        // = ≤1 physics tick of jitter) before starting the scenario.
        //
        // PlayerFallTicks is generous enough that free-fall is finished
        // BEFORE the first scheduled input fires. Replaces the prior
        // velocity-polling settle loop, which by definition returned at a
        // wall-clock-dependent moment.
        const int PlayerFallTicks = 90;
        int anchorTick = client.ReadClientTick() + PlayerFallTicks;

        // Build the entire input plan upfront, keyed to (anchorTick + delta).
        // Schedule is consumed by HarnessInputProducer.GenerateCurrentInput
        // each physics tick on the client. Because resolution = client tick,
        // the same input fires on the same tick across runs.
        const byte SpaceFlag = 0b_0000_0001;
        var schedule = new List<object>();
        schedule.Add(new { tick = anchorTick - PlayerFallTicks, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 });
        for (int elapsed = 0; elapsed < RunTicks; elapsed += 20)
        {
            int phase = elapsed / 60;
            int phaseMod = phase % 4;          // 0,1,2,3 → fwd-jump, fwd, back, back
            float moveY = (phaseMod < 2) ? -1f : +1f;
            bool jump = (elapsed / 20) % 2 == 0 && phaseMod < 2;
            byte keys = jump ? SpaceFlag : (byte)0;
            schedule.Add(new
            {
                tick = anchorTick + elapsed,
                moveX = 0.0,
                moveY = (double)moveY,
                yaw = 0.0,
                keys = (int)keys,
            });
        }
        schedule.Add(new { tick = anchorTick + RunTicks, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 });
        client.Send(new { cmd = "set-input-schedule", entries = schedule });

        client.WaitForClientTick(anchorTick);
        int baselineMispredictsAfterFall = ReadMispredictCount(client);
        int observerBaselineMispredicts = ReadMispredictCount(observer);

        // Capture BOTH server and client samples at every sample tick. The
        // body/visual/rotation deviation panels need the server pose to
        // compare against the client's predicted+reconciled pose. (Replaces
        // the deleted StackDeterminism test — same data, surfaced inside the
        // mispredict scenario where stacked-contact divergence actually
        // matters for gameplay.)
        var clientSamples = new List<Sample>();
        var serverSamples = new List<Sample>();
        for (int t = SnapshotIntervalTicks; t <= RunTicks; t += SnapshotIntervalTicks)
        {
            int targetTick = anchorTick + t;
            client.WaitForClientTick(targetTick);
            clientSamples.Add(CaptureSample(client, targetTick));
            serverSamples.Add(CaptureSample(server, targetTick));
        }

        int finalMispredicts = ReadMispredictCount(client);
        int mispredictsThisRun = finalMispredicts - baselineMispredictsAfterFall;
        int observerMispredictsThisRun = ReadMispredictCount(observer) - observerBaselineMispredicts;

        var paths = ArtifactsFor(label);
        WriteTowerPlot(paths, clientSamples, playerEid, cubeEids, baselineMispredictsAfterFall);
        // Additional comprehensive divergence plot: body+visual position
        // deviation + rotation deviation per cube AND per player, with
        // mispredict markers. Replaces the old StackDeterminism test by
        // surfacing the same data inside the gameplay-relevant scenario.
        WriteDivergencePlot(paths, clientSamples, serverSamples, playerEid, cubeEids, baselineMispredictsAfterFall);
        // Snap-magnitude plot: per-entity body teleport magnitude at each
        // reconcile event. Parses the client log for PHYS-RB-RECONCILE and
        // SMOOTH-ABSORB events, plots magnitudes as impulse-style spikes.
        // This is the metric that captures the visible "snap" — divergence
        // panels show steady-state error, snap-magnitude shows the instant
        // body teleport that the smoother has to absorb.
        WriteSnapPlot(paths, playerEid, cubeEids);
        // BV-interference plot: how much the per-tick velocity blend is FORCING
        // velocity onto cubes that natural physics did not produce. Captures
        // the "stuck on edge" / "rotation suppressed" symptoms. Only meaningful
        // for the BlendedVelocity policy — other policies have no BV-OVERRIDE
        // log lines so the file would be empty.
        if (cubeMetadata.Contains("blended-velocity"))
            WriteBvOverridePlot(paths, cubeEids);
        Godot.GD.Print($"[MP-MISPREDICT] policy={cubeMetadata} wrote {paths.Csv}, {paths.Svg} ({clientSamples.Count} samples, {mispredictsThisRun} mispredicts vs budget {mispredictBudget})");
        CopyProcessLogs(paths);
        CopyObserverLog(paths, label);

        AssertThat(mispredictsThisRun)
            .OverrideFailureMessage(
                $"client mispredicted {mispredictsThisRun} times in {RunTicks} ticks while charging the tower " +
                $"(policy='{cubeMetadata}', budget {mispredictBudget}). Trace at TestResults/MispredictPlots/{label}.{{csv,svg,mp4}}")
            .IsLessEqual(mispredictBudget);
        // Observer is parked idle and shouldn't accrue more reconciles than
        // the active driver — same budget as the driver. If the observer
        // exceeds this, it indicates a snapshot/input-forwarding gap where
        // observers can't reproduce the server's authoritative motion.
        AssertThat(observerMispredictsThisRun)
            .OverrideFailureMessage(
                $"observer mispredicted {observerMispredictsThisRun} times in {RunTicks} ticks (policy='{cubeMetadata}', budget {mispredictBudget}). " +
                $"Likely cause: server isn't forwarding the driver's input to observers via GameSnapshotMessage.Inputs, " +
                $"so the observer's prediction diverges every tick the driver is moving.")
            .IsLessEqual(mispredictBudget);
    }

    // Writes tower_run.csv (long-form, one row per (tick, entity) — player +
    // every cube) and tower_run.svg (single-panel world-Z trace, player line
    // on top of each cube line, mispredict markers as vertical dashed lines).
    // The Z-trace is the "are the player and cubes where I expect them?" plot;
    // the divergence plot below it answers "how far apart are server and client?".
    private static void WriteTowerPlot(ArtifactPaths paths, List<Sample> clientSamples,
        int playerEid, List<int> cubeEids, int baselineMispredicts)
    {
        var tracked = new HashSet<int>(cubeEids) { playerEid };
        CsvWriter.Write(paths.Csv, clientSamples, e => tracked.Contains(e.Id),
            new CsvWriter.Column("mispredictionsCount", _ => "0"), // overridden in per-row context below
            new CsvWriter.Column("eid", e => CsvWriter.I(e.Id)),
            new CsvWriter.Column("etype", e => CsvWriter.I(e.Type)),
            new CsvWriter.Column("x", e => CsvWriter.F(e.Position.X)),
            new CsvWriter.Column("y", e => CsvWriter.F(e.Position.Y)),
            new CsvWriter.Column("z", e => CsvWriter.F(e.Position.Z)),
            new CsvWriter.Column("vx", e => CsvWriter.F(e.Velocity.X)),
            new CsvWriter.Column("vy", e => CsvWriter.F(e.Velocity.Y)),
            new CsvWriter.Column("vz", e => CsvWriter.F(e.Velocity.Z)));

        var plot = new SvgPlot("tower_run — player + cube world-Z trace");
        var zPanel = plot.AddPanel("world Z (m) — player charges into the tower along −Z", yUnits: "m");

        // Cubes first so the player line sits on top, with consistent colors.
        for (int i = 0; i < cubeEids.Count; i++)
        {
            int eid = cubeEids[i];
            string color = SvgPlot.Palette.Series[(i + 1) % SvgPlot.Palette.Series.Length]; // index 0 reserved for player
            var pts = new List<(int, float)>();
            foreach (var s in clientSamples)
            {
                foreach (var e in s.Entities)
                {
                    if (e.Id == eid) { pts.Add((s.Tick, e.Position.Z)); break; }
                }
            }
            zPanel.AddSeries($"cube{i} eid={eid}", color, pts);
        }
        var playerPts = new List<(int, float)>();
        foreach (var s in clientSamples)
        {
            foreach (var e in s.Entities)
            {
                if (e.Id == playerEid) { playerPts.Add((s.Tick, e.Position.Z)); break; }
            }
        }
        zPanel.AddSeries("player", SvgPlot.Palette.Series[0], playerPts, strokeWidth: 1.8f);

        int prev = baselineMispredicts;
        foreach (var s in clientSamples)
        {
            if (s.MispredictionsCount > prev) plot.AddVerticalMarker(s.Tick, "mispredict");
            prev = s.MispredictionsCount;
        }
        plot.Save(paths.Svg);
    }

    // Writes <label>.divergence.svg: per-entity body position deviation,
    // visual position deviation, and rotation deviation (1 − |q_s · q_c|) over
    // time, with misprediction markers. One color per entity (player + each
    // cube) using the canonical SvgPlot palette.
    private static void WriteDivergencePlot(ArtifactPaths paths, List<Sample> clientSamples, List<Sample> serverSamples,
        int playerEid, List<int> cubeEids, int baselineMispredicts)
    {
        var divergencePath = System.IO.Path.Combine(paths.Directory,
            System.IO.Path.GetFileNameWithoutExtension(paths.Svg) + ".divergence.svg");

        var plot = new SvgPlot("tower_run — per-entity divergence (server vs client) with mispredict markers");
        var bodyPanel   = plot.AddPanel("|server.body.pos − client.body.pos| (m)", yUnits: "m");
        var visualPanel = plot.AddPanel("|server.body.pos − client.visual.pos| (m) — what the player actually sees", yUnits: "m");
        var rotPanel    = plot.AddPanel("rotation deviation 1−|server.q · client.q|", yUnits: "");

        // Each tracked entity gets its own color, used consistently across all 3 panels.
        var tracked = new List<(int eid, string label)> { (playerEid, "player") };
        for (int i = 0; i < cubeEids.Count; i++) tracked.Add((cubeEids[i], $"cube{i} eid={cubeEids[i]}"));

        for (int idx = 0; idx < tracked.Count; idx++)
        {
            int eid = tracked[idx].eid;
            string label = tracked[idx].label;
            string color = SvgPlot.Palette.Series[idx % SvgPlot.Palette.Series.Length];

            var bodyDev = new List<(int, float)>();
            var visualDev = new List<(int, float)>();
            var rotDev = new List<(int, float)>();
            for (int i = 0; i < clientSamples.Count; i++)
            {
                EntityState sE = null, cE = null;
                foreach (var e in serverSamples[i].Entities) if (e.Id == eid) { sE = e; break; }
                foreach (var e in clientSamples[i].Entities) if (e.Id == eid) { cE = e; break; }
                if (sE == null || cE == null) continue;
                int tick = clientSamples[i].Tick;
                bodyDev.Add((tick, (sE.Position - cE.Position).Length()));
                visualDev.Add((tick, (sE.Position - cE.VisualPosition).Length()));
                rotDev.Add((tick, 1f - Mathf.Abs(sE.Rotation.Dot(cE.Rotation))));
            }

            bodyPanel.AddSeries(label, color, bodyDev);
            visualPanel.AddSeries(label, color, visualDev);
            rotPanel.AddSeries(label, color, rotDev);
        }

        int prev = baselineMispredicts;
        foreach (var s in clientSamples)
        {
            if (s.MispredictionsCount > prev)
            {
                plot.AddVerticalMarker(s.Tick, "mispredict");
            }
            prev = s.MispredictionsCount;
        }

        plot.Save(divergencePath);
        Godot.GD.Print($"[MP-MISPREDICT] wrote divergence plot {divergencePath}");
    }

    /// <summary>
    /// Parses the client log for body-teleport events (PHYS-RB-RECONCILE for the
    /// rollback path, SMOOTH-ABSORB for the smoother capture) and plots per-entity
    /// snap magnitudes as impulse spikes vs tick. This is the metric that maps
    /// directly to "visible snap" — divergence panels show error trend, but a
    /// snap is the instantaneous body teleport that the smoother has to absorb
    /// in one frame.
    ///
    /// Series shape: each snap event becomes a vertical spike (three points:
    /// (tick, 0), (tick, magnitude), (tick, 0)) so the polyline draws an
    /// impulse-style bar at the snap tick.
    /// </summary>
    private static void WriteSnapPlot(ArtifactPaths paths, int playerEid, List<int> cubeEids)
    {
        string clientLogPath = System.IO.Path.Combine(paths.Directory,
            System.IO.Path.GetFileNameWithoutExtension(paths.Svg) + ".client.log");
        if (!System.IO.File.Exists(clientLogPath))
        {
            Godot.GD.Print($"[MP-MISPREDICT] snap plot SKIPPED — log not found at {clientLogPath}");
            return;
        }

        // Per-entity snap events: (tick, magnitude). One list per entity id.
        var rollbackSnapsByEid = new Dictionary<int, List<(int tick, float mag)>>();

        // Two-pass log scan. First pass: pair PRED-ROLLBACK ticks with the
        // PHYS-RB-RECONCILE lines that follow them in the same rollback.
        // A rollback emits one PRED-ROLLBACK then N PHYS-RB-RECONCILE lines
        // (one per body in the predicted-state). Tick attribution: the
        // PRED-ROLLBACK's tick is the SNAPSHOT TICK being rolled back to,
        // which is what we want for plotting (cause-and-effect on the same
        // x-axis as mispredict markers).
        int currentRollbackTick = -1;
        var rollbackTickRe = new System.Text.RegularExpressions.Regex(@"\[PRED-ROLLBACK\] tick=(\d+)");
        var reconcileRe = new System.Text.RegularExpressions.Regex(
            @"\[PHYS-RB-RECONCILE\] body=(\d+).*\|posDelta\|=([\d.]+)");

        foreach (var rawLine in System.IO.File.ReadLines(clientLogPath))
        {
            var rbMatch = rollbackTickRe.Match(rawLine);
            if (rbMatch.Success)
            {
                currentRollbackTick = int.Parse(rbMatch.Groups[1].Value);
                continue;
            }
            if (currentRollbackTick < 0) continue;
            var rcMatch = reconcileRe.Match(rawLine);
            if (!rcMatch.Success) continue;
            int eid = int.Parse(rcMatch.Groups[1].Value);
            float posDelta = float.Parse(rcMatch.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            // Only count meaningful teleports — sub-mm reconciles fire constantly
            // and aren't visible. 1 cm is generous; UE5's typical pose threshold
            // is the same order of magnitude.
            if (posDelta < 0.01f) continue;
            if (!rollbackSnapsByEid.TryGetValue(eid, out var list))
            {
                list = new List<(int, float)>();
                rollbackSnapsByEid[eid] = list;
            }
            list.Add((currentRollbackTick, posDelta));
        }

        // Build a plot with one panel showing all entities' snap-spike
        // events. Per-entity color matches the divergence plot for visual
        // continuity.
        var snapPath = System.IO.Path.Combine(paths.Directory,
            System.IO.Path.GetFileNameWithoutExtension(paths.Svg) + ".snaps.svg");
        var plot = new SvgPlot("snap magnitudes per entity — body teleport at reconcile (rollback path)");
        var snapPanel = plot.AddPanel("|body teleport| (m) — the magnitude the smoother has to absorb", yUnits: "m");

        var tracked = new List<(int eid, string label)> { (playerEid, "player") };
        for (int i = 0; i < cubeEids.Count; i++)
            tracked.Add((cubeEids[i], $"cube{i} eid={cubeEids[i]}"));

        for (int idx = 0; idx < tracked.Count; idx++)
        {
            int eid = tracked[idx].eid;
            string color = SvgPlot.Palette.Series[idx % SvgPlot.Palette.Series.Length];
            if (!rollbackSnapsByEid.TryGetValue(eid, out var snaps) || snaps.Count == 0) continue;
            // Convert each (tick, mag) into a three-point impulse spike so
            // the polyline draws as a vertical bar instead of a connected
            // baseline-to-baseline series. SvgPlot's polyline renderer
            // happily draws zero-width segments — no special impulse mode
            // needed.
            var pts = new List<(int, float)>(snaps.Count * 3);
            foreach (var (t, m) in snaps)
            {
                pts.Add((t, 0f));
                pts.Add((t, m));
                pts.Add((t, 0f));
            }
            snapPanel.AddSeries(tracked[idx].label, color, pts, strokeWidth: 1.4f);
        }

        plot.Save(snapPath);
        Godot.GD.Print($"[MP-MISPREDICT] wrote snap plot {snapPath}");
    }

    // BV-interference metric. Parses [BV-OVERRIDE] log lines emitted by
    // LocalRigidPropPrediction.ApplyContinuousBlendedVelocity and plots, per
    // cube, the per-tick magnitudes by which the blend FORCES linear+angular
    // velocity onto the body beyond what natural physics produced. Concretely:
    //
    //   linOverride = |blendedLin - currentLin|
    //   angOverride = |blendedAng - currentAng|
    //
    // High angOverride during settling = BV suppressing natural rotation
    // (the "stuck on edge" symptom). High linOverride sustained = BV chasing
    // stale snapshot data instead of letting the body coast.
    //
    // Two panels, one for angular override (rad/s) and one for linear (m/s).
    // Each cube gets its own series so per-body patterns are distinguishable.
    private static void WriteBvOverridePlot(ArtifactPaths paths, List<int> cubeEids)
    {
        string clientLogPath = System.IO.Path.Combine(paths.Directory,
            System.IO.Path.GetFileNameWithoutExtension(paths.Svg) + ".client.log");
        if (!System.IO.File.Exists(clientLogPath))
        {
            Godot.GD.Print($"[MP-MISPREDICT] BV override plot SKIPPED — log not found at {clientLogPath}");
            return;
        }

        // Regex captures eid, tick, linOverride, angOverride.
        var bvRe = new System.Text.RegularExpressions.Regex(
            @"\[BV-OVERRIDE\] eid=(\d+) tick=(\d+).*linOverride=([\d.]+) angOverride=([\d.]+)");

        var angByEid = new Dictionary<int, List<(int tick, float v)>>();
        var linByEid = new Dictionary<int, List<(int tick, float v)>>();

        int totalLines = 0, bvLines = 0, matched = 0;
        foreach (var rawLine in System.IO.File.ReadLines(clientLogPath))
        {
            totalLines++;
            if (rawLine.Contains("BV-OVERRIDE")) bvLines++;
            var m = bvRe.Match(rawLine);
            if (!m.Success) continue;
            matched++;
            int eid = int.Parse(m.Groups[1].Value);
            int tick = int.Parse(m.Groups[2].Value);
            float lin = float.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
            float ang = float.Parse(m.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (!angByEid.TryGetValue(eid, out var alist)) { alist = new(); angByEid[eid] = alist; }
            if (!linByEid.TryGetValue(eid, out var llist)) { llist = new(); linByEid[eid] = llist; }
            alist.Add((tick, ang));
            llist.Add((tick, lin));
        }
        Godot.GD.Print($"[MP-MISPREDICT] BV parse: totalLines={totalLines} bvLines={bvLines} matched={matched} distinct eids={angByEid.Count}");

        var svgPath = System.IO.Path.Combine(paths.Directory,
            System.IO.Path.GetFileNameWithoutExtension(paths.Svg) + ".bv_override.svg");
        var plot = new SvgPlot("BV interference — per-tick velocity override the blend forces onto the body (lower=more natural physics)");
        var angPanel = plot.AddPanel("angular override (rad/s) — how much rotation BV is suppressing or amplifying", yUnits: "rad/s");
        var linPanel = plot.AddPanel("linear override (m/s) — how much translation BV is forcing", yUnits: "m/s");

        for (int i = 0; i < cubeEids.Count; i++)
        {
            int eid = cubeEids[i];
            string color = SvgPlot.Palette.Series[i % SvgPlot.Palette.Series.Length];
            string label = $"cube{i} eid={eid}";
            if (angByEid.TryGetValue(eid, out var alist) && alist.Count > 0)
                angPanel.AddSeries(label, color, alist, strokeWidth: 1.2f);
            if (linByEid.TryGetValue(eid, out var llist) && llist.Count > 0)
                linPanel.AddSeries(label, color, llist, strokeWidth: 1.2f);
        }

        plot.Save(svgPath);

        // Also write a summary line so the run output captures the headline
        // numbers without having to open the SVG.
        float maxAng = 0f, sumAng = 0f; int nAng = 0;
        float maxLin = 0f, sumLin = 0f; int nLin = 0;
        foreach (var (_, list) in angByEid) foreach (var (_, v) in list) { if (v > maxAng) maxAng = v; sumAng += v; nAng++; }
        foreach (var (_, list) in linByEid) foreach (var (_, v) in list) { if (v > maxLin) maxLin = v; sumLin += v; nLin++; }
        float meanAng = nAng > 0 ? sumAng / nAng : 0f;
        float meanLin = nLin > 0 ? sumLin / nLin : 0f;
        Godot.GD.Print($"[MP-MISPREDICT] wrote BV override plot {svgPath} — samples={nAng} maxAngOverride={maxAng:F3} meanAngOverride={meanAng:F4} maxLinOverride={maxLin:F3} meanLinOverride={meanLin:F4}");
    }
}

// Tiny extension exposing the orch tick-count cmd as a method on TestProcess so the
// test doesn't have to know the JSON shape.
internal static class TestProcessExtensions
{
    public static long ReadTickCountSafe(this TestProcess p)
    {
        using var doc = p.Send(new { cmd = "tick-count" });
        return doc.RootElement.GetProperty("data").GetProperty("ticks").GetInt64();
    }
}
