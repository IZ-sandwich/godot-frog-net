using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GameDemo;
using GdUnit4;
using Godot;
using MonkeNet.Shared;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// CMP-01: Collision-motion plot harness.
///
/// Drives a player → vehicle head-on push through five scenarios and records the
/// per-frame Z position of both bodies, so jerky motion during collisions can be
/// visualised and compared against a deterministic baseline:
///
///   1. baseline      — AnimatableBody3D cylinder + RigidBody3D box, no library code,
///                      single physics space. The "ideal" trace.
///   2. listen_cb     — listen-server CharacterBody3D player + vehicle, driven by
///                      <see cref="SharedPlayerMovement"/> + <see cref="VehiclePhysics"/>
///                      in a single space.
///   3. listen_rb     — listen-server RigidBody3D player + vehicle, driven by
///                      <see cref="RigidPlayerPhysics"/> + <see cref="VehiclePhysics"/>.
///   4. host_cb       — pure-client CharacterBody3D player. Server simulates player +
///                      vehicle on its own collision-layer pair; the client predicts
///                      its own player on the client layers; the visible vehicle pose
///                      is the server's vehicle pose, lagged by the snapshot delay.
///   5. host_rb       — same as host_cb with <see cref="RigidPlayerPhysics"/>.
///
/// Outputs (under the test working directory):
///   TestResults/CollisionPlots/baseline.csv, listen_cb.csv, listen_rb.csv,
///                              host_cb.csv, host_rb.csv
///   TestResults/CollisionPlots/plot.svg
///   TestResults/CollisionPlots/summary.txt
///
/// Iteration-1 caveats:
///   * No rollback resimulation, no PredictionVisualSmoothing, no real network manager.
///     The host_* scenarios fake snapshot lag with a frame-buffer queue.
///   * Known issue: in this harness the listen_cb and host_cb players (CharacterBody3D
///     + MoveAndSlide via <see cref="SharedPlayerMovement"/>) do not move forward —
///     the very first MoveAndSlide call reports redundant floor contacts and zeros
///     velocity, leaving the body stuck at start. <see cref="PlayerPushTests"/> creates
///     identical bodies in [BeforeTest] and does NOT see this; reproducing the same
///     setup in this class still reproduces the bug. Cause not yet isolated. The
///     listen_rb / host_rb / baseline traces are correct and useful as-is.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class CollisionMotionPlotTests
{
    // Scenario knobs — keep identical across all five scenarios so traces are comparable.
    private const int FrameCount = 120;          // 2 s at 60 Hz.
    private const float Dt = 1f / 60f;
    private const float PlayerStartZ = 0f;
    private const float VehicleStartZ = -3f;     // 3 m forward of player at t=0.
    private const float BodyY = -1f;             // matches MainScene floor (top at Y=-2).
    private const int SnapshotLagFrames = 2;     // host_* mode: dummy vehicle lags server by this many ticks.

    // Project layer mapping mirrors EntitySpawner / project.godot:
    //   bit 0 = Environment, bit 1 = ClientPlayers, bit 15 = ServerPlayers.
    private const uint EnvironmentLayer = 1u << 0;
    private const uint ClientLayer = 1u << 1;
    private const uint ServerLayer = 1u << 15;
    private const uint ClientMask = EnvironmentLayer | ClientLayer;
    private const uint ServerMask = EnvironmentLayer | ServerLayer;

    private ISceneRunner _runner;
    private Rid _space;

    [BeforeTest]
    public async Task SetUp()
    {
        MonkeNetConfig.Instance = null;
        _runner = ISceneRunner.Load("res://demo/MainScene.tscn", autoFree: true);
        await _runner.AwaitIdleFrame();
        _space = _runner.Scene().GetViewport().World3D.Space;
    }

    [AfterTest]
    public void TearDown()
    {
        _runner?.Dispose();
        MonkeNetConfig.Instance = null;
    }

    // ── Single-method orchestrator ─────────────────────────────────────────────
    [TestCase]
    public async Task CollisionMotion_AllScenarios_PlotProduced()
    {
        string outputDir = ResolveOutputDir();
        Directory.CreateDirectory(outputDir);

        // Temporarily run listen_cb FIRST to test if order matters.
        var listenCb = await RecordListenServerCharacterBody();
        ResetWorldForNextScenario();
        await _runner.AwaitIdleFrame();

        var baseline = await RecordBaseline();
        ResetWorldForNextScenario();
        await _runner.AwaitIdleFrame();

        var listenRb = await RecordListenServerRigidBody();
        ResetWorldForNextScenario();
        await _runner.AwaitIdleFrame();

        var hostCb = await RecordHostClientCharacterBody();
        ResetWorldForNextScenario();
        await _runner.AwaitIdleFrame();

        var hostRb = await RecordHostClientRigidBody();
        ResetWorldForNextScenario();

        WriteCsv(outputDir, "baseline.csv", baseline);
        WriteCsv(outputDir, "listen_cb.csv", listenCb);
        WriteCsv(outputDir, "listen_rb.csv", listenRb);
        WriteCsv(outputDir, "host_cb.csv", hostCb);
        WriteCsv(outputDir, "host_rb.csv", hostRb);

        var labelled = new (string Name, Trace Trace)[]
        {
            ("baseline", baseline),
            ("listen_cb", listenCb),
            ("listen_rb", listenRb),
            ("host_cb", hostCb),
            ("host_rb", hostRb),
        };

        WritePlotSvg(Path.Combine(outputDir, "plot.svg"), labelled);
        string summary = BuildSummary(labelled);
        File.WriteAllText(Path.Combine(outputDir, "summary.txt"), summary);

        // Echo summary to test output so it shows up in test-output.log.
        Godot.GD.Print(summary);
        Godot.GD.Print($"[CMP-01] Wrote plot to: {Path.Combine(outputDir, "plot.svg")}");

        // Sanity assertions — the harness must produce non-empty traces and a plot file.
        AssertThat(baseline.Frames.Count).IsEqual(FrameCount);
        AssertThat(File.Exists(Path.Combine(outputDir, "plot.svg"))).IsTrue();
    }

    // ── Scenario 1: baseline ───────────────────────────────────────────────────
    // AnimatableBody3D cylinder (kinematic; sync_to_physics derives an implicit
    // velocity from per-frame position deltas so Jolt computes contact response
    // and pushes the dynamic box) + dynamic RigidBody3D box. No library code in
    // the loop — this is the deterministic reference trace.
    private async Task<Trace> RecordBaseline()
    {
        var (cylinder, box) = CreateKinematicCylinderAndBox(ClientLayer, ClientMask);

        // Mirror PlayerPushTests SetUp: idle frame after AddChild flushes any deferred
        // body registration so MoveAndSlide / PhysicsServer queries behave consistently.
        await _runner.AwaitIdleFrame();
        // First space step so Jolt registers both bodies.
        PhysicsServer3D.SpaceStep(_space, Dt);
        PhysicsServer3D.SpaceFlushQueries(_space);

        var trace = new Trace { Description = "AnimatableBody3D cylinder + RigidBody3D box (Jolt natural push)" };
        Vector3 step = new Vector3(0, 0, -SharedPlayerMovement.MaxRunSpeed * Dt);

        for (int f = 0; f < FrameCount; f++)
        {
            cylinder.GlobalPosition += step;

            PhysicsServer3D.SpaceStep(_space, Dt);
            PhysicsServer3D.SpaceFlushQueries(_space);

            trace.Frames.Add(new FrameSample
            {
                Frame = f,
                PlayerZ = cylinder.GlobalPosition.Z,
                VehicleZ = box.GlobalPosition.Z,
            });
        }

        cylinder.Free();
        box.Free();
        return trace;
    }

    // ── Scenario 2: listen-server CharacterBody3D player + vehicle ─────────────
    // Single physics space (listen-server shares it). Drives the actual library
    // code path: SharedPlayerMovement.AdvancePhysics + VehiclePhysics.AdvancePhysics
    // run in lock-step inside one tick.
    private async Task<Trace> RecordListenServerCharacterBody()
    {
        var (player, movement, vehicle, vehiclePred) = CreateCharacterPlayerAndVehicle(ClientLayer, ClientMask);
        await _runner.AwaitIdleFrame();
        PhysicsServer3D.SpaceStep(_space, Dt);
        PhysicsServer3D.SpaceFlushQueries(_space);

        var trace = new Trace { Description = "Listen-server CharacterBody3D player + LocalVehicle (single space)" };
        var input = new CharacterInputMessage { MoveX = 0, MoveY = -1, CameraYaw = 0, Keys = 0 };

        for (int f = 0; f < FrameCount; f++)
        {
            movement.AdvancePhysics(input);
            VehiclePhysics.AdvancePhysics(vehiclePred, default(CharacterInputMessage));

            PhysicsServer3D.SpaceStep(_space, Dt);
            PhysicsServer3D.SpaceFlushQueries(_space);

            trace.Frames.Add(new FrameSample
            {
                Frame = f,
                PlayerZ = player.GlobalPosition.Z,
                VehicleZ = vehicle.GlobalPosition.Z,
            });
        }

        // Top-level nodes only — Free() recursively frees children (movement, vehiclePred).
        player.Free();
        vehicle.Free();
        return trace;
    }

    // ── Scenario 3: listen-server RigidBody3D player + vehicle ────────────────
    private async Task<Trace> RecordListenServerRigidBody()
    {
        var (player, playerPred, vehicle, vehiclePred) = CreateRigidPlayerAndVehicle(ClientLayer, ClientMask);
        await _runner.AwaitIdleFrame();
        PhysicsServer3D.SpaceStep(_space, Dt);
        PhysicsServer3D.SpaceFlushQueries(_space);

        var trace = new Trace { Description = "Listen-server RigidBody3D player + LocalVehicle (single space)" };
        var input = new CharacterInputMessage { MoveX = 0, MoveY = -1, CameraYaw = 0, Keys = 0 };

        for (int f = 0; f < FrameCount; f++)
        {
            RigidPlayerPhysics.AdvancePhysics(playerPred, input);
            VehiclePhysics.AdvancePhysics(vehiclePred, default(CharacterInputMessage));

            PhysicsServer3D.SpaceStep(_space, Dt);
            PhysicsServer3D.SpaceFlushQueries(_space);

            trace.Frames.Add(new FrameSample
            {
                Frame = f,
                PlayerZ = player.GlobalPosition.Z,
                VehicleZ = vehicle.GlobalPosition.Z,
            });
        }

        player.Free();
        vehicle.Free();
        return trace;
    }

    // ── Scenario 4: host + client, CharacterBody3D player ─────────────────────
    // Two parallel pairs in one physics space, separated by collision layers
    // (mirrors PVC-01). The client owns and predicts its own player; the server
    // owns and simulates the authoritative player + vehicle. The recorded
    // "vehicle" trace is the server vehicle's position lagged by SnapshotLagFrames
    // ticks — what the client's DummyVehicle would interpolate to.
    private async Task<Trace> RecordHostClientCharacterBody()
    {
        var (clientPlayer, clientMovement, _, _) = CreateCharacterPlayerAndVehicle(
            ClientLayer, ClientMask, includeVehicle: false);
        var (serverPlayer, serverMovement, serverVehicle, serverVehiclePred) =
            CreateCharacterPlayerAndVehicle(ServerLayer, ServerMask);

        await _runner.AwaitIdleFrame();
        PhysicsServer3D.SpaceStep(_space, Dt);
        PhysicsServer3D.SpaceFlushQueries(_space);

        var trace = new Trace { Description = "Host+client CharacterBody3D (server-vehicle lag = " + SnapshotLagFrames + " ticks)" };
        var input = new CharacterInputMessage { MoveX = 0, MoveY = -1, CameraYaw = 0, Keys = 0 };
        var serverVehicleHistory = new Queue<float>();

        for (int f = 0; f < FrameCount; f++)
        {
            // Client predicts its player (no rollback in iteration 1).
            clientMovement.AdvancePhysics(input);
            // Server simulates authoritative player + vehicle with the same input.
            serverMovement.AdvancePhysics(input);
            VehiclePhysics.AdvancePhysics(serverVehiclePred, default(CharacterInputMessage));

            PhysicsServer3D.SpaceStep(_space, Dt);
            PhysicsServer3D.SpaceFlushQueries(_space);

            // Snapshot the server vehicle pose this tick and emit the older one.
            serverVehicleHistory.Enqueue(serverVehicle.GlobalPosition.Z);
            float visibleVehicleZ = serverVehicleHistory.Count > SnapshotLagFrames
                ? serverVehicleHistory.Dequeue()
                : VehicleStartZ;

            trace.Frames.Add(new FrameSample
            {
                Frame = f,
                PlayerZ = clientPlayer.GlobalPosition.Z,
                VehicleZ = visibleVehicleZ,
            });
        }

        clientPlayer.Free();
        serverPlayer.Free();
        serverVehicle.Free();
        return trace;
    }

    // ── Scenario 5: host + client, RigidBody3D player ─────────────────────────
    private async Task<Trace> RecordHostClientRigidBody()
    {
        var (clientPlayer, clientPred, _, _) = CreateRigidPlayerAndVehicle(
            ClientLayer, ClientMask, includeVehicle: false);
        var (serverPlayer, serverPred, serverVehicle, serverVehiclePred) =
            CreateRigidPlayerAndVehicle(ServerLayer, ServerMask);

        await _runner.AwaitIdleFrame();
        PhysicsServer3D.SpaceStep(_space, Dt);
        PhysicsServer3D.SpaceFlushQueries(_space);

        var trace = new Trace { Description = "Host+client RigidBody3D (server-vehicle lag = " + SnapshotLagFrames + " ticks)" };
        var input = new CharacterInputMessage { MoveX = 0, MoveY = -1, CameraYaw = 0, Keys = 0 };
        var serverVehicleHistory = new Queue<float>();

        for (int f = 0; f < FrameCount; f++)
        {
            RigidPlayerPhysics.AdvancePhysics(clientPred, input);
            RigidPlayerPhysics.AdvancePhysics(serverPred, input);
            VehiclePhysics.AdvancePhysics(serverVehiclePred, default(CharacterInputMessage));

            PhysicsServer3D.SpaceStep(_space, Dt);
            PhysicsServer3D.SpaceFlushQueries(_space);

            serverVehicleHistory.Enqueue(serverVehicle.GlobalPosition.Z);
            float visibleVehicleZ = serverVehicleHistory.Count > SnapshotLagFrames
                ? serverVehicleHistory.Dequeue()
                : VehicleStartZ;

            trace.Frames.Add(new FrameSample
            {
                Frame = f,
                PlayerZ = clientPlayer.GlobalPosition.Z,
                VehicleZ = visibleVehicleZ,
            });
        }

        clientPlayer.Free();
        serverPlayer.Free();
        serverVehicle.Free();
        return trace;
    }

    // ── Body factories ─────────────────────────────────────────────────────────

    private (AnimatableBody3D cylinder, RigidBody3D box) CreateKinematicCylinderAndBox(uint layer, uint mask)
    {
        var interpOff = Node.PhysicsInterpolationModeEnum.Off;

        // AnimatableBody3D = kinematic body that derives an effective velocity from per-frame
        // position changes; Jolt uses that to compute the impulse it applies to dynamic
        // bodies in contact. SyncToPhysics keeps the body in step with the physics frame.
        var cylinder = new AnimatableBody3D
        {
            CollisionLayer = layer,
            CollisionMask = mask,
            SyncToPhysics = true,
            PhysicsInterpolationMode = interpOff,
            Position = new Vector3(0, BodyY, PlayerStartZ),
        };
        cylinder.AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.5f, Height = 2f },
        });
        _runner.Scene().AddChild(cylinder);

        var box = new RigidBody3D
        {
            CollisionLayer = layer,
            CollisionMask = mask,
            Mass = 1f,
            GravityScale = 0f,
            LinearDamp = 0f,
            AngularDamp = 0f,
            PhysicsInterpolationMode = interpOff,
            Position = new Vector3(0, BodyY, VehicleStartZ),
        };
        box.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(2, 1, 4) },
        });
        _runner.Scene().AddChild(box);

        return (cylinder, box);
    }

    private (CharacterBody3D player, SharedPlayerMovement movement, RigidBody3D vehicle, PredictionRigidbody3D vehiclePred)
        CreateCharacterPlayerAndVehicle(uint layer, uint mask, bool includeVehicle = true)
    {
        var interpOff = Node.PhysicsInterpolationModeEnum.Off;

        var player = new CharacterBody3D
        {
            CollisionLayer = layer,
            CollisionMask = mask,
            PhysicsInterpolationMode = interpOff,
            Position = new Vector3(0, BodyY, PlayerStartZ),
        };
        player.AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.5f, Height = 2f },
        });
        _runner.Scene().AddChild(player);

        var movement = new SharedPlayerMovement();
        player.AddChild(movement);
        movement.Initialize(player);

        if (!includeVehicle) return (player, movement, null, null);

        var vehicle = new RigidBody3D
        {
            CollisionLayer = layer,
            CollisionMask = mask,
            Mass = 1f,
            GravityScale = 0f,
            // Match VehiclePhysics' manual-drag model: zero the body's built-in damp so
            // we don't double-up drag once we route through PredictionRigidbody3D.
            LinearDamp = 0f,
            AngularDamp = 0f,
            PhysicsInterpolationMode = interpOff,
            Position = new Vector3(0, BodyY, VehicleStartZ),
        };
        vehicle.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(2, 1, 4) },
        });
        _runner.Scene().AddChild(vehicle);

        var vehiclePred = new PredictionRigidbody3D();
        vehicle.AddChild(vehiclePred);
        vehiclePred.Initialize(vehicle);

        return (player, movement, vehicle, vehiclePred);
    }

    private (RigidBody3D player, PredictionRigidbody3D playerPred, RigidBody3D vehicle, PredictionRigidbody3D vehiclePred)
        CreateRigidPlayerAndVehicle(uint layer, uint mask, bool includeVehicle = true)
    {
        var interpOff = Node.PhysicsInterpolationModeEnum.Off;

        var player = new RigidBody3D
        {
            CollisionLayer = layer,
            CollisionMask = mask,
            Mass = 1f,
            GravityScale = 0f,
            LinearDamp = 0f,
            AngularDamp = 0f,
            LockRotation = true,
            PhysicsInterpolationMode = interpOff,
            Position = new Vector3(0, BodyY, PlayerStartZ),
        };
        player.AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.5f, Height = 2f },
        });
        _runner.Scene().AddChild(player);

        var playerPred = new PredictionRigidbody3D();
        player.AddChild(playerPred);
        playerPred.Initialize(player);

        if (!includeVehicle) return (player, playerPred, null, null);

        var vehicle = new RigidBody3D
        {
            CollisionLayer = layer,
            CollisionMask = mask,
            Mass = 1f,
            GravityScale = 0f,
            LinearDamp = 0f,
            AngularDamp = 0f,
            PhysicsInterpolationMode = interpOff,
            Position = new Vector3(0, BodyY, VehicleStartZ),
        };
        vehicle.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(2, 1, 4) },
        });
        _runner.Scene().AddChild(vehicle);

        var vehiclePred = new PredictionRigidbody3D();
        vehicle.AddChild(vehiclePred);
        vehiclePred.Initialize(vehicle);

        return (player, playerPred, vehicle, vehiclePred);
    }

    // After each scenario, flush queued physics queries so any leftover state from
    // freed bodies doesn't leak into the next scenario.
    private void ResetWorldForNextScenario()
    {
        PhysicsServer3D.SpaceFlushQueries(_space);
    }

    // ── CSV / SVG / summary writers ────────────────────────────────────────────

    private static string ResolveOutputDir()
    {
        // Test working directory is the test project root (tests/). Normalise to absolute.
        return Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "CollisionPlots"));
    }

    private static void WriteCsv(string dir, string fileName, Trace trace)
    {
        var sb = new StringBuilder();
        sb.AppendLine("frame,player_z,vehicle_z");
        foreach (var f in trace.Frames)
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1:0.######},{2:0.######}", f.Frame, f.PlayerZ, f.VehicleZ));
        File.WriteAllText(Path.Combine(dir, fileName), sb.ToString());
    }

    private static string BuildSummary((string Name, Trace Trace)[] traces)
    {
        var baseline = traces[0].Trace;
        var sb = new StringBuilder();
        sb.AppendLine("=== CMP-01 collision-motion plot summary ===");
        sb.AppendLine($"frames per scenario: {FrameCount}, dt: {Dt:0.######}s");
        sb.AppendLine();
        sb.AppendLine("Per-scenario deviation from baseline (Euclidean over both bodies' Z, all frames):");
        sb.AppendLine("  scenario        meanDev[m]   maxDev[m]   maxDevFrame   description");
        foreach (var (name, trace) in traces)
        {
            var (mean, max, frame) = ComputeDeviation(baseline, trace);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "  {0,-15} {1,10:0.0000} {2,11:0.0000} {3,13}   {4}",
                name, mean, max, frame, trace.Description));
        }
        return sb.ToString();
    }

    private static (float mean, float max, int maxFrame) ComputeDeviation(Trace baseline, Trace other)
    {
        int n = Math.Min(baseline.Frames.Count, other.Frames.Count);
        if (n == 0) return (0, 0, 0);
        double sum = 0;
        float max = 0;
        int maxFrame = 0;
        for (int i = 0; i < n; i++)
        {
            float dp = other.Frames[i].PlayerZ - baseline.Frames[i].PlayerZ;
            float dv = other.Frames[i].VehicleZ - baseline.Frames[i].VehicleZ;
            float d = (float)Math.Sqrt(dp * dp + dv * dv);
            sum += d;
            if (d > max) { max = d; maxFrame = i; }
        }
        return ((float)(sum / n), max, maxFrame);
    }

    // Inline SVG plot — dependency-free, opens in any browser. One subplot for the
    // player Z trace and one for the vehicle Z trace, stacked vertically.
    private static void WritePlotSvg(string svgPath, (string Name, Trace Trace)[] traces)
    {
        const int Width = 1280;
        const int PlotH = 320;
        const int LegendH = 40;
        const int TitleH = 30;
        const int Margin = 60;
        const int Height = TitleH + LegendH + 2 * (PlotH + Margin) + Margin;

        // Find global Z bounds across all scenarios (player + vehicle separately).
        float playerMin = float.PositiveInfinity, playerMax = float.NegativeInfinity;
        float vehicleMin = float.PositiveInfinity, vehicleMax = float.NegativeInfinity;
        int frameCount = 0;
        foreach (var (_, trace) in traces)
        {
            frameCount = Math.Max(frameCount, trace.Frames.Count);
            foreach (var f in trace.Frames)
            {
                if (f.PlayerZ < playerMin) playerMin = f.PlayerZ;
                if (f.PlayerZ > playerMax) playerMax = f.PlayerZ;
                if (f.VehicleZ < vehicleMin) vehicleMin = f.VehicleZ;
                if (f.VehicleZ > vehicleMax) vehicleMax = f.VehicleZ;
            }
        }
        // Pad bounds 5% so the curve doesn't kiss the frame.
        Pad(ref playerMin, ref playerMax, 0.05f);
        Pad(ref vehicleMin, ref vehicleMax, 0.05f);

        string[] colors = { "#1f77b4", "#d62728", "#2ca02c", "#9467bd", "#ff7f0e" };

        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns='http://www.w3.org/2000/svg' width='{Width}' height='{Height}' font-family='monospace' font-size='12'>");
        sb.AppendLine($"<rect width='100%' height='100%' fill='white'/>");
        sb.AppendLine($"<text x='{Width / 2}' y='20' text-anchor='middle' font-size='16' font-weight='bold'>Player→Vehicle collision: position over time (Z, m). Forward = -Z.</text>");

        // Legend.
        int legendY = TitleH + 8;
        int legendX = Margin;
        for (int i = 0; i < traces.Length; i++)
        {
            sb.AppendLine($"<line x1='{legendX}' y1='{legendY + 6}' x2='{legendX + 24}' y2='{legendY + 6}' stroke='{colors[i]}' stroke-width='2.5'/>");
            sb.AppendLine($"<text x='{legendX + 30}' y='{legendY + 10}'>{Escape(traces[i].Name)}</text>");
            legendX += 8 + 24 + 8 + (traces[i].Name.Length * 8) + 16;
        }

        // Two subplots — top: player, bottom: vehicle.
        int plotTopY = TitleH + LegendH;
        DrawSubplot(sb, "Player Z (m)", plotTopY, Margin, Width - Margin, PlotH,
                    frameCount, playerMin, playerMax,
                    traces, accessor: f => f.PlayerZ, colors);
        int plotBottomY = plotTopY + PlotH + Margin;
        DrawSubplot(sb, "Vehicle Z (m)", plotBottomY, Margin, Width - Margin, PlotH,
                    frameCount, vehicleMin, vehicleMax,
                    traces, accessor: f => f.VehicleZ, colors);

        sb.AppendLine("</svg>");
        File.WriteAllText(svgPath, sb.ToString());
    }

    private static void DrawSubplot(StringBuilder sb, string yLabel, int top, int left, int right, int height,
        int frameCount, float yMin, float yMax,
        (string Name, Trace Trace)[] traces, Func<FrameSample, float> accessor, string[] colors)
    {
        int plotW = right - left;
        // Frame at y-axis label.
        sb.AppendLine($"<text x='{left - 8}' y='{top - 6}' text-anchor='end' font-weight='bold'>{Escape(yLabel)}</text>");
        sb.AppendLine($"<rect x='{left}' y='{top}' width='{plotW}' height='{height}' fill='none' stroke='#888'/>");

        // Y gridlines + labels — 5 divisions.
        for (int g = 0; g <= 5; g++)
        {
            float frac = g / 5f;
            int y = top + (int)(frac * height);
            float v = yMax - frac * (yMax - yMin);
            sb.AppendLine($"<line x1='{left}' y1='{y}' x2='{right}' y2='{y}' stroke='#eee'/>");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "<text x='{0}' y='{1}' text-anchor='end'>{2:0.00}</text>",
                left - 4, y + 4, v));
        }

        // X gridlines + labels — 6 divisions.
        for (int g = 0; g <= 6; g++)
        {
            float frac = g / 6f;
            int x = left + (int)(frac * plotW);
            int frame = (int)Math.Round(frac * (frameCount - 1));
            sb.AppendLine($"<line x1='{x}' y1='{top}' x2='{x}' y2='{top + height}' stroke='#eee'/>");
            sb.AppendLine($"<text x='{x}' y='{top + height + 14}' text-anchor='middle'>f={frame}</text>");
        }

        // Trace lines.
        for (int i = 0; i < traces.Length; i++)
        {
            var (_, trace) = traces[i];
            if (trace.Frames.Count == 0) continue;
            var path = new StringBuilder();
            for (int j = 0; j < trace.Frames.Count; j++)
            {
                float fx = j / (float)Math.Max(1, frameCount - 1);
                float fy = (yMax - accessor(trace.Frames[j])) / Math.Max(1e-6f, yMax - yMin);
                int x = left + (int)(fx * plotW);
                int y = top + (int)(fy * height);
                path.Append(j == 0 ? "M" : "L").Append(x).Append(' ').Append(y).Append(' ');
            }
            sb.AppendLine($"<path d='{path}' fill='none' stroke='{colors[i % colors.Length]}' stroke-width='1.6' opacity='0.9'/>");
        }
    }

    private static void Pad(ref float min, ref float max, float frac)
    {
        if (min == max) { min -= 0.5f; max += 0.5f; return; }
        float range = max - min;
        min -= range * frac;
        max += range * frac;
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // ── Data classes ───────────────────────────────────────────────────────────

    private struct FrameSample
    {
        public int Frame;
        public float PlayerZ;
        public float VehicleZ;
    }

    private class Trace
    {
        public string Description = "";
        public List<FrameSample> Frames = new();
    }
}
