using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using GdUnit4;
using Godot;
using MonkeNet.Tests.Infrastructure;
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
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class MultiProcessMispredictTests
{
    private const string ArtifactRoot = "TestResults/MispredictPlots";
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

    private static int _enetPortCounter = 9300;

    private string _godotBin;
    private string _projectPath;
    private MultiProcessOrchestrator _orch;
    // Subprocess MonkeLogger log paths reported via the ready cmd. Used by
    // WriteArtifacts to copy each per-process log into the artifact dir so it
    // can be diff'd against manually-recorded gameplay logs.
    private string _serverLogPath;
    private string _clientLogPath;

    [BeforeTest]
    public void SetUp()
    {
        _godotBin = System.Environment.GetEnvironmentVariable("GODOT_BIN");
        if (string.IsNullOrEmpty(_godotBin) || !File.Exists(_godotBin)) return; // skipped — see MC-01

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

    // MP-MISPREDICT-01 ──────────────────────────────────────────────────────────
    // Build a cube tower, then drive the rigid player through 3 forward-and-back
    // passes while pulsing the Jump key. Counts mispredictions for the duration
    // of the interaction (excluding the settle window) and asserts the count
    // stays under a budget. The client subprocess records video directly via
    // --write-movie, so the AVI shows the ACTUAL production rendering of the
    // client's view (LocalRigidPlayer + DummyCubes) with collision-shape
    // wireframes drawn by --debug-collisions.
    [TestCase]
    public void MultiProcess_RigidPlayer_RunsIntoTowerWhileJumping_MispredictionsStayUnderBudget()
    {
        if (_orch == null) return;

        int port = NextPort();
        var server = _orch.Spawn("server", enetPort: port, label: "srv");
        server.WaitReady(networkReady: true, timeoutMs: 30_000);

        string videoPath = Path.Combine(_projectPath, ArtifactRoot, "tower_run.mp4");
        // Spawn the client with video recording enabled. The Godot subprocess
        // runs WINDOWED (no --headless) at a fixed resolution, and an external
        // ffmpeg sidecar captures the window via gdigrab into the mp4. The
        // engine runs at its native physics rate — recording does not slow
        // simulation, unlike --write-movie.
        var client = _orch.Spawn("client", enetPort: port, label: "c1", recordVideoPath: videoPath);
        client.WaitReady(networkReady: true, timeoutMs: 30_000);

        // Remember each subprocess's MonkeLogger log path (reported via the ready
        // cmd). user:// resolves differently depending on which project.godot the
        // subprocess loaded — main repo vs the tests project — so reading the path
        // straight from the process is more reliable than reconstructing it.
        _serverLogPath = server.RemoteLogPath;
        _clientLogPath = client.RemoteLogPath;

        // Wait for the client clock to be synced to the server before any
        // entity spawns. networkReady=true alone doesn't imply the clock has
        // converged in all topologies — see MultiProcessClockSyncTests for the
        // baseline. Without this, the first cube spawn races the first
        // averaged clock-sync window and the client renders cubes at stale
        // tick offsets (large snap-back on DummyCube), inflating the
        // measured misprediction count for reasons unrelated to physics.
        WaitForClockSync(server, client, maxGapTicks: 5, timeoutMs: 5_000);

        int clientNetId = client.NetworkId;
        AssertThat(clientNetId).OverrideFailureMessage("client must have a non-zero ENet peer id").IsNotEqual(0);

        // Brief warmup so clock-sync stabilises before any entities exist.
        server.WaitForTicks(SnapshotArmTicks);

        // Pre-spawn the cube tower (auth=0 → DummyCube on the client, interpolated
        // from server snapshots). Cubes are spawned with vertical gaps so each
        // free-falls onto the stack.
        var cubeEids = new List<int>();
        for (int i = 0; i < TowerCubeCount; i++)
        {
            float y = TowerBaseY + i * CubeSpacingY;
            cubeEids.Add(SpawnEntity(server, EntityTypeCube, authority: 0, 0f, y, TowerZ));
        }
        server.WaitForTicks(TowerSettleTicks);

        // Spawn at Y=0 to match the user's default spawn (pid24564 log shows the
        // player initially at pos=(0,0,0), then free-falling onto floor at Y≈-2.08).
        int playerEid = SpawnEntity(server, EntityTypeRigidPlayer, clientNetId, 0f, 0f, 1.5f);

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
        // Initial idle while falling, just to be explicit.
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
        // Release input on the final tick so the run terminates cleanly.
        schedule.Add(new { tick = anchorTick + RunTicks, moveX = 0.0, moveY = 0.0, yaw = 0.0, keys = 0 });
        client.Send(new { cmd = "set-input-schedule", entries = schedule });

        // Block until the client clock reaches the anchor so subsequent
        // sample captures are aligned to the schedule (avoids capturing
        // pre-input baseline as if it were mid-scenario).
        client.WaitForClientTick(anchorTick);
        int baselineMispredictsAfterFall = ReadMispredictCount(client);

        // Sample loop: schedule is autonomous now, so the test thread only
        // captures snapshots at SnapshotIntervalTicks cadence relative to
        // the anchor tick. Tick targets are absolute so capture-time wall
        // jitter does not shift them.
        var samples = new List<Sample>();
        for (int t = SnapshotIntervalTicks; t <= RunTicks; t += SnapshotIntervalTicks)
        {
            int targetTick = anchorTick + t;
            client.WaitForClientTick(targetTick);
            samples.Add(CaptureSample(client, targetTick));
        }

        // Budget covers only what happened DURING the scripted scenario;
        // baseline absorbs spawn-fall mispredictions (gravity-clock-offset
        // between the just-spawned player on server vs client).
        int finalMispredicts = ReadMispredictCount(client);
        int mispredictsThisRun = finalMispredicts - baselineMispredictsAfterFall;

        WriteArtifacts("tower_run", samples, playerEid, cubeEids, baselineMispredictsAfterFall);

        AssertThat(mispredictsThisRun)
            .OverrideFailureMessage(
                $"client mispredicted {mispredictsThisRun} times in {RunTicks} ticks while charging the tower " +
                $"(budget {MispredictBudget}). Trace + video at {ArtifactRoot}/tower_run.{{csv,svg,mp4}}")
            .IsLessEqual(MispredictBudget);
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

    // Polls the client's sample-state until the given entity reports a vertical
    // velocity below threshold (i.e. it has come to rest on the floor). Catches
    // the snapshot-interpolation lag between server and client that fixed-tick
    // waits don't account for — the server can have the player settled while
    // the client is still rendering it mid-fall a few ticks behind.
    // Polls server + client clock-state until the absolute gap
    // (clientSyncedTick − serverTick − latency) is within budget. Establishes
    // a synced clock before the test starts spawning entities so the trace
    // measures physics misprediction, not "client clock catching up to server".
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
            catch { /* harness not ready yet; retry */ }
            Thread.Sleep(50);
        }
        Godot.GD.PrintErr($"[MP-MISPREDICT] clock did not converge to ±{maxGapTicks} within {timeoutMs}ms; last gap={lastGap}");
    }

    private static int ReadMispredictCount(TestProcess client)
    {
        using var doc = client.Send(new { cmd = "mispredict-count" });
        return doc.RootElement.GetProperty("data").GetProperty("count").GetInt32();
    }

    private static Sample CaptureSample(TestProcess client, int sampleTick)
    {
        using var doc = client.Send(new { cmd = "sample-state" });
        var root = doc.RootElement.GetProperty("data");
        var s = new Sample
        {
            Tick = sampleTick,
            MispredictionsCount = root.GetProperty("mispredictionsCount").GetInt32(),
            Entities = new List<EntityState>(),
        };
        foreach (var el in root.GetProperty("entities").EnumerateArray())
        {
            var pos = el.GetProperty("position");
            var vel = el.GetProperty("velocity");
            s.Entities.Add(new EntityState
            {
                Id = el.GetProperty("id").GetInt32(),
                Type = el.GetProperty("type").GetInt32(),
                Authority = el.GetProperty("authority").GetInt32(),
                Position = new Vector3((float)pos[0].GetDouble(), (float)pos[1].GetDouble(), (float)pos[2].GetDouble()),
                Velocity = new Vector3((float)vel[0].GetDouble(), (float)vel[1].GetDouble(), (float)vel[2].GetDouble()),
            });
        }
        return s;
    }

    private void WriteArtifacts(string label, List<Sample> samples, int playerEid,
        List<int> cubeEids, int baselineMispredicts)
    {
        var dir = Path.Combine(_projectPath, ArtifactRoot);
        var csvPath = Path.Combine(dir, label + ".csv");
        var svgPath = Path.Combine(dir, label + ".svg");
        MispredictPlot.WriteCsv(csvPath, samples, playerEid, cubeEids, baselineMispredicts);
        MispredictPlot.WriteSvg(svgPath, samples, playerEid, cubeEids, baselineMispredicts, label);
        Godot.GD.Print($"[MP-MISPREDICT] wrote {csvPath}, {svgPath} ({samples.Count} samples)");

        // Copy each subprocess's monke-net log into the artifact dir so the
        // test logs can be diff'd against manually-recorded gameplay logs.
        CopyProcessLog(dir, _serverLogPath, label + ".server.log");
        CopyProcessLog(dir, _clientLogPath, label + ".client.log");
    }

    // Copies a subprocess's MonkeLogger log into the artifact directory under a
    // stable, role-tagged name. The source path is reported by the harness over
    // the orch socket so we don't have to guess the user_data dir.
    private static void CopyProcessLog(string artifactDir, string srcPath, string targetName)
    {
        if (string.IsNullOrEmpty(srcPath))
        {
            Godot.GD.PrintErr($"[MP-MISPREDICT] no log path reported for {targetName}");
            return;
        }
        if (!File.Exists(srcPath))
        {
            Godot.GD.PrintErr($"[MP-MISPREDICT] log file does not exist: {srcPath}");
            return;
        }
        try
        {
            // Use a copy that allows the source file to remain open by the
            // subprocess (which may still be writing to it). File.Copy on a
            // file held with FileShare.Read works on Windows but not on Linux
            // — read+write the bytes explicitly to be portable.
            using var src = new FileStream(srcPath, FileMode.Open, System.IO.FileAccess.Read, FileShare.ReadWrite);
            using var dst = new FileStream(Path.Combine(artifactDir, targetName), FileMode.Create, System.IO.FileAccess.Write, FileShare.Read);
            src.CopyTo(dst);
            Godot.GD.Print($"[MP-MISPREDICT] copied log {Path.GetFileName(srcPath)} → {targetName}");
        }
        catch (Exception ex)
        {
            Godot.GD.PrintErr($"[MP-MISPREDICT] failed to copy {srcPath}: {ex.Message}");
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

    // ── data ──────────────────────────────────────────────────────────────────

    internal class Sample
    {
        public int Tick;
        public int MispredictionsCount;
        public List<EntityState> Entities;
    }

    internal class EntityState
    {
        public int Id;
        public int Type;
        public int Authority;
        public Vector3 Position;
        public Vector3 Velocity;
        // Populated by tests that need orientation / spin data on the trace
        // (e.g. offset-push). Default to identity / zero so consumers that
        // never set these fields don't accidentally emit garbage.
        public Vector3 AngularVelocity;
        public Quaternion Rotation = Quaternion.Identity;
        // Visual mesh pose — equal to (Position, Rotation) unless a
        // PredictionVisualSmoothing3D is wired on the entity, in which case
        // it reports the smoothed/offset-decayed visible pose instead.
        public Vector3 VisualPosition;
        public Quaternion VisualRotation = Quaternion.Identity;
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
