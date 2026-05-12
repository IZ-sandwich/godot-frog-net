using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using GdUnit4;
using Godot;
using MonkeNet.Tests.Infrastructure;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.MultiProcess;

/// <summary>
/// MP-OFFSET-PUSH-01: Multi-process rigid-player vs. rigid-cube offset-push scenario.
/// A single cube is spawned slightly offset from the player's forward axis so a
/// straight-line charge clips it off-centre — applying simultaneous linear push
/// AND angular spin from the contact lever arm. The point of the test is to
/// visualise the client-side body position over time and observe the
/// reconciliation snap that fires near the end of the push motion (when the
/// local Jolt sim and the server Jolt sim diverge enough on contact normals /
/// spin axis to trip <c>HasMisspredicted</c> on the cube).
///
/// Artefacts written under <c>TestResults/OffsetPushPlots/</c>:
///   - offset_push.csv   (tick, eid, type, x, y, z, vx, vy, vz)
///   - offset_push.svg   (player + cube X/Z trajectories, misprediction markers)
///   - offset_push.mp4   (in-engine viewport recording of the windowed client)
///
/// This is a baseline test: the cube currently has NO visual/physics separation,
/// so the server reconcile pushes the body itself when <c>HasMisspredicted</c>
/// trips. The next step in the work plan is to introduce a separate visual node
/// and lerp it toward the body — at which point the same test should still show
/// a snap on the BODY trace but a smooth trace on the VISUAL trace.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class MultiProcessOffsetPushTests
{
    private const string ArtifactRoot = "TestResults/OffsetPushPlots";
    private const byte EntityTypeRigidPlayer = 3;
    private const byte EntityTypeCube = 4;

    // ── scenario geometry ────────────────────────────────────────────────────
    // Floor in MainScene is a 30x1x30 CSGBox centred at Y=-2.5 (top face Y=-2).
    // Cube is 1x1x1 spawned ABOVE the floor so it falls and settles before the
    // player reaches it. CubeOffsetX is a small lateral offset from the player's
    // forward path so the collision is off-centre and produces visible spin.
    private const float CubeStartZ = -2.5f;
    // 0.65 m lateral offset puts the contact well off-centre — the cube spins
    // visibly during push (high angular velocity) and the lever arm amplifies
    // any per-step Jolt divergence between client and server, so the resulting
    // reconcile snap at end-of-push is much louder than a near-axial impact.
    private const float CubeOffsetX = 0.65f;
    private const float CubeStartY = 0.5f;
    private const float PlayerStartZ = 1.5f;
    private const float PlayerStartY = 0f;

    // ── timing ───────────────────────────────────────────────────────────────
    // Brief warmup so clock-sync stabilises before any entities spawn.
    private const int SnapshotArmTicks = 60;
    // Ticks given to the cube to fall onto the floor and stop bouncing before
    // we spawn the player + drive input.
    private const int CubeSettleTicks = 90;
    // After spawning the player, wait this long for its own free-fall to
    // complete before issuing forward input. Anchored to the CLIENT tick so
    // the input schedule lands on the same tick across runs.
    private const int PlayerFallTicks = 90;
    // Length of the scripted run in physics ticks. 60 Hz physics, so 120 ticks
    // = 2 s of total run time. The full sequence — player charges, contacts
    // cube, releases, cube coasts, snap fires — completes well inside that
    // window with current tuning.
    private const int RunTicks = 120;
    // How long the player keeps pressing forward before releasing. From the
    // anchor (when forward input begins) the player needs ~30 ticks to close
    // the 4 m gap to the cube; we keep pushing for a few ticks AFTER first
    // contact so the cube receives an initial impulse, then RELEASE so the
    // cube coasts to rest on its own residual momentum. This is the worst
    // case for client/server agreement: once the player stops pushing there
    // is no continuous contact reaction keeping both Jolt sims in lockstep,
    // so they evolve independently and the snap at end-of-coast is loudest.
    private const int PressForwardTicks = 42;
    // Sample cadence. 2 ticks = 30 samples/s of position data, dense enough
    // for the SVG snap event to be a single-pixel discontinuity in the line.
    private const int SnapshotIntervalTicks = 2;

    private static int _enetPortCounter = 9400;

    private string _godotBin;
    private string _projectPath;
    private MultiProcessOrchestrator _orch;
    private string _serverLogPath;
    private string _clientLogPath;

    [BeforeTest]
    public void SetUp()
    {
        _godotBin = System.Environment.GetEnvironmentVariable("GODOT_BIN");
        if (string.IsNullOrEmpty(_godotBin) || !File.Exists(_godotBin)) return; // skipped
        _projectPath = ResolveProjectPath();
        Directory.CreateDirectory(Path.Combine(_projectPath, ArtifactRoot));
        _orch = new MultiProcessOrchestrator(_godotBin, _projectPath);
    }

    [AfterTest]
    public void TearDown()
    {
        _orch?.Dispose();
        _orch = null;
    }

    [TestCase]
    public void MultiProcess_RigidPlayer_OffsetPushesCube_RendersTraceAndVideo()
    {
        if (_orch == null) return;

        int port = NextPort();
        var server = _orch.Spawn("server", enetPort: port, label: "srv");
        server.WaitReady(networkReady: true, timeoutMs: 30_000);

        string videoPath = Path.Combine(_projectPath, ArtifactRoot, "offset_push.mp4");
        var client = _orch.Spawn("client", enetPort: port, label: "c1", recordVideoPath: videoPath);
        client.WaitReady(networkReady: true, timeoutMs: 30_000);

        _serverLogPath = server.RemoteLogPath;
        _clientLogPath = client.RemoteLogPath;

        WaitForClockSync(server, client, maxGapTicks: 5, timeoutMs: 5_000);

        int clientNetId = client.NetworkId;
        AssertThat(clientNetId).OverrideFailureMessage("client must have a non-zero ENet peer id").IsNotEqual(0);

        server.WaitForTicks(SnapshotArmTicks);

        // Single cube offset slightly on +X so a straight forward charge from
        // the player hits it off-centre, producing simultaneous push + spin.
        int cubeEid = SpawnEntity(server, EntityTypeCube, authority: 0,
            CubeOffsetX, CubeStartY, CubeStartZ);

        // Test-only physics tuning: low friction + low damping so the cube
        // coasts long enough for the visual smoother decay window to be
        // visible on the trace. Applied to BOTH server and client replicas so
        // both Jolt sims run with identical parameters; demo scenes (used
        // outside tests) keep their default friction=0.6 / damp=0.4/0.8.
        // The client's replica won't exist until the entity-event reaches it
        // a few ticks after spawn, so poll briefly before issuing the client
        // override.
        const float TestFriction = 0.15f;
        const float TestLinearDamp = 0.05f;
        const float TestAngularDamp = 0.1f;
        server.Send(new
        {
            cmd = "set-entity-physics",
            entity_id = cubeEid,
            friction = TestFriction,
            linearDamp = TestLinearDamp,
            angularDamp = TestAngularDamp,
        });
        WaitForClientEntity(client, cubeEid, timeoutMs: 5_000);
        client.Send(new
        {
            cmd = "set-entity-physics",
            entity_id = cubeEid,
            friction = TestFriction,
            linearDamp = TestLinearDamp,
            angularDamp = TestAngularDamp,
        });

        server.WaitForTicks(CubeSettleTicks);

        int playerEid = SpawnEntity(server, EntityTypeRigidPlayer, clientNetId,
            0f, PlayerStartY, PlayerStartZ);

        // Anchor the input schedule to the client's clock, not wall time, so
        // the same input fires on the same tick across runs.
        int anchorTick = client.ReadClientTick() + PlayerFallTicks;

        // Schedule: idle while falling → forward for PressForwardTicks (long
        // enough to make contact + deliver an initial push) → release for
        // the rest of the run while the cube coasts to rest. Releasing
        // mid-push maximises divergence between the client and server cube
        // sims (no continuous contact-reaction force coupling them anymore),
        // so the snap at end-of-coast is loudest.
        var schedule = new List<object>
        {
            new { tick = anchorTick - PlayerFallTicks,    moveX = 0.0, moveY = 0.0,  yaw = 0.0, keys = 0 },
            new { tick = anchorTick,                      moveX = 0.0, moveY = -1.0, yaw = 0.0, keys = 0 },
            new { tick = anchorTick + PressForwardTicks,  moveX = 0.0, moveY = 0.0,  yaw = 0.0, keys = 0 },
            new { tick = anchorTick + RunTicks,           moveX = 0.0, moveY = 0.0,  yaw = 0.0, keys = 0 },
        };
        client.Send(new { cmd = "set-input-schedule", entries = schedule });

        client.WaitForClientTick(anchorTick);
        int baselineMispredicts = ReadMispredictCount(client);

        // Sample loop with a deterministic divergence injection. Cross-process
        // Jolt is determinism-limited and run-to-run divergence varies from
        // sub-mm to several cm — too noisy to demonstrate the smoother
        // reliably. At TeleportInjectTick the test issues a server-side
        // teleport that moves the cube ~30 cm sideways out of band. The next
        // snapshot arrives with the new pose, the client mispredicts vs its
        // own sim, HandleReconciliation fires, and PredictionRigidbody3D.
        // Reconcile snaps the body while handing the pre-visual pose to the
        // smoother. The trace then shows: a discontinuous step on the body
        // line and a smooth ramp on the visual line — the architectural
        // signature of the offset-decay model.
        const int TeleportInjectTick = 95;   // ~mid-coast, cube still moving
        const float TeleportOffsetX = 0.3f;  // 30 cm sideways jump
        bool injected = false;

        var samples = new List<MultiProcessMispredictTests.Sample>();
        for (int t = SnapshotIntervalTicks; t <= RunTicks; t += SnapshotIntervalTicks)
        {
            int targetTick = anchorTick + t;
            client.WaitForClientTick(targetTick);
            samples.Add(CaptureSample(client, targetTick));

            if (!injected && t >= TeleportInjectTick)
            {
                // Read the cube's current server-side position by snapping
                // the client view (cheap proxy for the server pose since
                // they're in close sync at this point). Then teleport the
                // server cube 30 cm along +X.
                Vector3 cubePos = Vector3.Zero;
                foreach (var e in samples[samples.Count - 1].Entities)
                {
                    if (e.Id == cubeEid) { cubePos = e.Position; break; }
                }
                server.Send(new
                {
                    cmd = "teleport-entity",
                    entity_id = cubeEid,
                    position = new[] { (double)(cubePos.X + TeleportOffsetX), cubePos.Y, cubePos.Z },
                });
                injected = true;
            }
        }

        int finalMispredicts = ReadMispredictCount(client);
        int mispredictsThisRun = finalMispredicts - baselineMispredicts;

        WriteArtifacts("offset_push", samples, playerEid, cubeEid, baselineMispredicts);

        // The test is observational: we want the artefacts written, regardless
        // of misprediction count. Assert only that we collected real samples
        // covering both entities so the graph isn't silently empty.
        AssertThat(samples.Count)
            .OverrideFailureMessage("expected non-empty sample stream")
            .IsGreater(0);
        bool sawPlayer = false, sawCube = false;
        foreach (var s in samples)
        {
            foreach (var e in s.Entities)
            {
                if (e.Id == playerEid) sawPlayer = true;
                if (e.Id == cubeEid) sawCube = true;
            }
        }
        AssertThat(sawPlayer && sawCube)
            .OverrideFailureMessage($"trace must include both player ({playerEid}) and cube ({cubeEid}). " +
                $"mispredicts={mispredictsThisRun}. Artefacts at {ArtifactRoot}/offset_push.{{csv,svg,mp4}}")
            .IsTrue();

        Godot.GD.Print($"[MP-OFFSET-PUSH] run complete: {samples.Count} samples, " +
            $"{mispredictsThisRun} mispredictions over {RunTicks} ticks. " +
            $"Artefacts: {ArtifactRoot}/offset_push.{{csv,svg,mp4}}");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static int NextPort() => Interlocked.Increment(ref _enetPortCounter);

    private static int SpawnEntity(TestProcess server, byte entityType, int authority,
        float x, float y, float z)
    {
        using var r = server.Send(new
        {
            cmd = "spawn-entity",
            entity_type = (int)entityType,
            authority,
            position = new[] { (double)x, y, z },
        });
        return r.RootElement.GetProperty("data").GetProperty("entityId").GetInt32();
    }

    // Polls the client's entity list until the entity with the given id
    // appears (or until timeoutMs expires). The client doesn't know about a
    // server-spawned entity until the EntityEventMessage reaches it, which
    // takes one round-trip-plus a few ticks of snapshot interpolation —
    // calling set-entity-physics before that returns "entity not found".
    private static void WaitForClientEntity(TestProcess client, int eid, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            using var doc = client.Send(new { cmd = "get-all-entities" });
            foreach (var e in doc.RootElement.GetProperty("data").GetProperty("entities").EnumerateArray())
            {
                if (e.GetProperty("id").GetInt32() == eid) return;
            }
            Thread.Sleep(20);
        }
        Godot.GD.PrintErr($"[MP-OFFSET-PUSH] client entity {eid} did not appear within {timeoutMs}ms");
    }

    private static void WaitForClockSync(TestProcess server, TestProcess client,
        int maxGapTicks, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        int lastGap = int.MinValue;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var sDoc = server.Send(new { cmd = "clock-state" });
                using var cDoc = client.Send(new { cmd = "clock-state" });
                int serverTick = sDoc.RootElement.GetProperty("data").GetProperty("serverTick").GetInt32();
                int syncedTick = cDoc.RootElement.GetProperty("data").GetProperty("syncedTick").GetInt32();
                int latency = cDoc.RootElement.GetProperty("data").GetProperty("averageLatencyTicks").GetInt32();
                int gap = syncedTick - serverTick - latency;
                lastGap = gap;
                if (Math.Abs(gap) <= maxGapTicks) return;
            }
            catch { }
            Thread.Sleep(50);
        }
        Godot.GD.PrintErr($"[MP-OFFSET-PUSH] clock did not converge to ±{maxGapTicks} within {timeoutMs}ms; last gap={lastGap}");
    }

    private static int ReadMispredictCount(TestProcess client)
    {
        using var doc = client.Send(new { cmd = "mispredict-count" });
        return doc.RootElement.GetProperty("data").GetProperty("count").GetInt32();
    }

    private static MultiProcessMispredictTests.Sample CaptureSample(TestProcess client, int sampleTick)
    {
        using var doc = client.Send(new { cmd = "sample-state" });
        var root = doc.RootElement.GetProperty("data");
        var s = new MultiProcessMispredictTests.Sample
        {
            Tick = sampleTick,
            MispredictionsCount = root.GetProperty("mispredictionsCount").GetInt32(),
            Entities = new List<MultiProcessMispredictTests.EntityState>(),
        };
        foreach (var el in root.GetProperty("entities").EnumerateArray())
        {
            var pos = el.GetProperty("position");
            var vel = el.GetProperty("velocity");
            var st = new MultiProcessMispredictTests.EntityState
            {
                Id = el.GetProperty("id").GetInt32(),
                Type = el.GetProperty("type").GetInt32(),
                Authority = el.GetProperty("authority").GetInt32(),
                Position = new Vector3((float)pos[0].GetDouble(), (float)pos[1].GetDouble(), (float)pos[2].GetDouble()),
                Velocity = new Vector3((float)vel[0].GetDouble(), (float)vel[1].GetDouble(), (float)vel[2].GetDouble()),
            };
            if (el.TryGetProperty("angularVelocity", out var av))
            {
                st.AngularVelocity = new Vector3((float)av[0].GetDouble(), (float)av[1].GetDouble(), (float)av[2].GetDouble());
            }
            if (el.TryGetProperty("rotation", out var rq))
            {
                st.Rotation = new Quaternion(
                    (float)rq[0].GetDouble(), (float)rq[1].GetDouble(),
                    (float)rq[2].GetDouble(), (float)rq[3].GetDouble());
            }
            // Visual pose — falls back to body pose when the entity has no
            // PredictionVisualSmoothing3D wired.
            if (el.TryGetProperty("visualPosition", out var vp))
            {
                st.VisualPosition = new Vector3((float)vp[0].GetDouble(), (float)vp[1].GetDouble(), (float)vp[2].GetDouble());
            }
            else st.VisualPosition = st.Position;
            if (el.TryGetProperty("visualRotation", out var vr))
            {
                st.VisualRotation = new Quaternion(
                    (float)vr[0].GetDouble(), (float)vr[1].GetDouble(),
                    (float)vr[2].GetDouble(), (float)vr[3].GetDouble());
            }
            else st.VisualRotation = st.Rotation;
            s.Entities.Add(st);
        }
        return s;
    }

    private void WriteArtifacts(string label, List<MultiProcessMispredictTests.Sample> samples,
        int playerEid, int cubeEid, int baselineMispredicts)
    {
        var dir = Path.Combine(_projectPath, ArtifactRoot);
        var csvPath = Path.Combine(dir, label + ".csv");
        var svgPath = Path.Combine(dir, label + ".svg");
        OffsetPushPlot.WriteCsv(csvPath, samples, playerEid, cubeEid, baselineMispredicts);
        OffsetPushPlot.WriteSvg(svgPath, samples, playerEid, cubeEid, baselineMispredicts, label);
        Godot.GD.Print($"[MP-OFFSET-PUSH] wrote {csvPath}, {svgPath} ({samples.Count} samples)");
        CopyProcessLog(dir, _serverLogPath, label + ".server.log");
        CopyProcessLog(dir, _clientLogPath, label + ".client.log");
    }

    private static void CopyProcessLog(string artifactDir, string srcPath, string targetName)
    {
        if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath)) return;
        try
        {
            using var src = new FileStream(srcPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
            using var dst = new FileStream(Path.Combine(artifactDir, targetName), FileMode.Create, System.IO.FileAccess.Write, FileShare.Read);
            src.CopyTo(dst);
        }
        catch (Exception ex)
        {
            Godot.GD.PrintErr($"[MP-OFFSET-PUSH] failed to copy {srcPath}: {ex.Message}");
        }
    }

    private static string ResolveProjectPath()
    {
        var dir = new DirectoryInfo(System.Environment.CurrentDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "project.godot"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate project.godot from current working directory");
    }
}
