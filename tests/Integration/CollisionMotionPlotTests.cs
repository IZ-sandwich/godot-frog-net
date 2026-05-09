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
///   * The listen_cb / host_cb scenarios DO NOT route through
///     <see cref="SharedPlayerMovement.AdvancePhysics"/>'s MoveAndSlide — see
///     <see cref="CharacterMoveSlide"/>. In this test class CharacterBody3D.MoveAndSlide
///     reports redundant floor contacts and zeros velocity at every call, so the body
///     never moves forward (the same code in <see cref="PlayerPushTests"/> works fine;
///     cause unisolated). The workaround translates the body kinematically and applies
///     the framework's PushRigidBodies impulse via shape query — same observable
///     vehicle motion as the real path, just without the locked-up MoveAndSlide.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class CollisionMotionPlotTests
{
    // Scenario knobs — keep identical across all five scenarios so traces are comparable.
    private const int FrameCount = 360;          // 6 s at 60 Hz.
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
    private StaticBody3D _testFloor;

    [BeforeTest]
    public async Task SetUp()
    {
        MonkeNetConfig.Instance = null;
        // Use MainScene only to inherit MonkeNetConfig wiring + the SpaceSetActive(false)
        // setup. The MainScene Floor is a CSGBox3D whose triangle-mesh collision causes
        // CharacterBody3D.MoveAndSlide to lock up in this harness — replace it with a
        // clean StaticBody3D + BoxShape3D before any test bodies are added.
        _runner = ISceneRunner.Load("res://demo/MainScene.tscn", autoFree: true);
        await _runner.AwaitIdleFrame();
        _space = _runner.Scene().GetViewport().World3D.Space;

        // Free the CSG floor (triangle-mesh collision triggers the MoveAndSlide
        // lockup in this class) and ALL the MainScene map obstacles. With FrameCount=360
        // (6 s) the player advances ~30 m at MaxRunSpeed; without removing the borders
        // and ramps, body collisions with map geometry would dominate the trace.
        foreach (string mapChildName in new[] {
            "Map/Floor", "Map/Wall1", "Map/Wall2",
            "Map/Border1", "Map/Border2", "Map/Border3", "Map/Border4",
            "Map/Ramp1", "Map/Ramp2", "Map/CSGCylinder3D", "Map/OfflineBall",
        })
        {
            var n = _runner.Scene().GetNodeOrNull(mapChildName);
            if (n != null) n.Free();
        }

        _testFloor = new StaticBody3D
        {
            CollisionLayer = 1u, // Environment
            CollisionMask = 0u,
            PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off,
            // Wide enough to cover 6 s of forward motion (~30 m) with margin.
            Position = new Vector3(0, -2.5f, -20f),
        };
        _testFloor.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(30, 1, 80) },
        });
        _runner.Scene().AddChild(_testFloor);
        // Pump physics a few times so the new floor is fully settled in Jolt's broadphase
        // before any test body is added on top of it.
        await _runner.AwaitIdleFrame();
        for (int i = 0; i < 3; i++)
        {
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }
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
                VehicleVZ = box.LinearVelocity.Z,
            });
        }

        cylinder.Free();
        box.Free();
        return trace;
    }

    // ── Scenario 2: listen-server CharacterBody3D player + vehicle ─────────────
    // Single physics space (listen-server shares it). Drives the equivalent of the
    // library's CharacterBody3D path: input → CalculateVelocity → manual kinematic
    // translate (bypassing the test-harness MoveAndSlide lockup, see CharacterMoveSlide
    // helper) → manual push impulse mirroring SharedPlayerMovement.PushRigidBodies.
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
            CharacterMoveSlide(player, input);
            VehiclePhysics.AdvancePhysics(vehiclePred, default(CharacterInputMessage));

            PhysicsServer3D.SpaceStep(_space, Dt);
            PhysicsServer3D.SpaceFlushQueries(_space);

            trace.Frames.Add(new FrameSample
            {
                Frame = f,
                PlayerZ = player.GlobalPosition.Z,
                VehicleZ = vehicle.GlobalPosition.Z,
                VehicleVZ = vehicle.LinearVelocity.Z,
            });
        }

        // Top-level nodes only — Free() recursively frees children (movement, vehiclePred).
        player.Free();
        vehicle.Free();
        return trace;
    }

    // Stand-in for SharedPlayerMovement.AdvancePhysics that does NOT use MoveAndSlide.
    // Why: in this test class, CharacterBody3D.MoveAndSlide reports redundant floor
    // contacts and zeros the body's velocity at every call, so the player never moves
    // forward — see the [BeforeTest] comment about the CSG floor. The same code in
    // PlayerPushTests works fine; the lifecycle difference hasn't been isolated. As a
    // workaround, drive the body kinematically (GlobalPosition += velocity*dt) and
    // manually apply the framework's PushRigidBodies impulse to any RigidBody3D that
    // overlaps the player's collision shape after the move.
    private const float PushStrength = 1.5f;
    private void CharacterMoveSlide(CharacterBody3D body, CharacterInputMessage input)
    {
        Vector3 velocity = SharedPlayerMovement.CalculateVelocity(body, input);
        body.Velocity = velocity;

        // Translate kinematically. Y is left at the body's current position so the
        // capsule doesn't gradually drift through the floor across many frames; the
        // scenarios run at constant Y for the head-on push.
        Vector3 newPos = body.GlobalPosition + new Vector3(velocity.X, 0, velocity.Z) * Dt;
        body.GlobalPosition = new Vector3(newPos.X, BodyY, newPos.Z);

        // Mirror SharedPlayerMovement.PushRigidBodies: shape-query for any RigidBody3D
        // overlapping the player's collision and apply an impulse along the player→body
        // direction proportional to how fast we're moving into it.
        var space = body.GetWorld3D().DirectSpaceState;
        CollisionShape3D shape = null;
        foreach (Node child in body.GetChildren())
            if (child is CollisionShape3D cs && cs.Shape != null) { shape = cs; break; }
        if (shape == null) return;

        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = shape.Shape,
            Transform = shape.GlobalTransform,
            CollisionMask = body.CollisionMask,
            CollideWithBodies = true,
            CollideWithAreas = false,
            Exclude = new Godot.Collections.Array<Rid> { body.GetRid() },
        };
        var hits = space.IntersectShape(query, maxResults: 8);
        foreach (var hit in hits)
        {
            if (!hit.TryGetValue("collider", out var colVar)) continue;
            if (colVar.AsGodotObject() is not RigidBody3D rb) continue;

            Vector3 toBody = rb.GlobalPosition - body.GlobalPosition;
            toBody.Y = 0;
            if (toBody.LengthSquared() < 0.0001f) continue;
            Vector3 dir = toBody.Normalized();

            float speedIntoBody = velocity.Dot(dir);
            if (speedIntoBody <= 0f) continue;

            Vector3 impulse = dir * speedIntoBody * PushStrength;
            rb.ApplyImpulse(impulse, body.GlobalPosition - rb.GlobalPosition);

            // Resolve overlap by pulling the player back to the contact surface so we
            // don't tunnel through the vehicle on subsequent frames.
            float bodyRadius = (shape.Shape is CapsuleShape3D cap) ? cap.Radius : 0.5f;
            // Approximate: nudge player back along the push direction so we sit just
            // outside the vehicle's bounding extent in that direction.
            Vector3 vehicleHalfExtents = (rb.GetChild<CollisionShape3D>(0).Shape is BoxShape3D box)
                ? box.Size * 0.5f
                : new Vector3(0.5f, 0.5f, 0.5f);
            float vehHalfAlongDir = Mathf.Abs(dir.X) * vehicleHalfExtents.X
                                   + Mathf.Abs(dir.Y) * vehicleHalfExtents.Y
                                   + Mathf.Abs(dir.Z) * vehicleHalfExtents.Z;
            Vector3 surfacePoint = rb.GlobalPosition - dir * vehHalfAlongDir;
            Vector3 fromBodyToSurface = surfacePoint - body.GlobalPosition;
            float along = fromBodyToSurface.Dot(dir);
            if (along < bodyRadius)
            {
                float pushBack = bodyRadius - along;
                body.GlobalPosition -= dir * pushBack;
            }
        }
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
                VehicleVZ = vehicle.LinearVelocity.Z,
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
            CharacterMoveSlide(clientPlayer, input);
            // Server simulates authoritative player + vehicle with the same input.
            CharacterMoveSlide(serverPlayer, input);
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
                // Velocity is captured from the SERVER body (the authoritative source);
                // the visible dummy on the real client interpolates between snapshots
                // and would just lag this trace by SnapshotLagFrames.
                VehicleVZ = serverVehicle.LinearVelocity.Z,
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
                VehicleVZ = serverVehicle.LinearVelocity.Z,
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
        sb.AppendLine("frame,player_z,vehicle_z,vehicle_vz");
        foreach (var f in trace.Frames)
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "{0},{1:0.######},{2:0.######},{3:0.######}",
                f.Frame, f.PlayerZ, f.VehicleZ, f.VehicleVZ));
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

    // Inline SVG plot — dependency-free, opens in any browser. Three subplots
    // stacked vertically: player Z, collided-rigidbody Z, collided-rigidbody Vz
    // (the velocity panel reveals impulse jerk that's hard to read off positions).
    private static void WritePlotSvg(string svgPath, (string Name, Trace Trace)[] traces)
    {
        const int Width = 1280;
        const int PlotH = 280;
        const int LegendH = 40;
        const int TitleH = 30;
        const int Margin = 60;
        const int Height = TitleH + LegendH + 3 * (PlotH + Margin) + Margin;

        // Find global bounds across all scenarios (player Z, vehicle Z, vehicle Vz).
        float playerMin = float.PositiveInfinity, playerMax = float.NegativeInfinity;
        float vehicleMin = float.PositiveInfinity, vehicleMax = float.NegativeInfinity;
        float vzMin = float.PositiveInfinity, vzMax = float.NegativeInfinity;
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
                if (f.VehicleVZ < vzMin) vzMin = f.VehicleVZ;
                if (f.VehicleVZ > vzMax) vzMax = f.VehicleVZ;
            }
        }
        // Pad bounds 5% so the curve doesn't kiss the frame.
        Pad(ref playerMin, ref playerMax, 0.05f);
        Pad(ref vehicleMin, ref vehicleMax, 0.05f);
        Pad(ref vzMin, ref vzMax, 0.05f);

        string[] colors = { "#1f77b4", "#d62728", "#2ca02c", "#9467bd", "#ff7f0e" };

        var sb = new StringBuilder();
        sb.AppendLine($"<svg xmlns='http://www.w3.org/2000/svg' width='{Width}' height='{Height}' font-family='monospace' font-size='12'>");
        sb.AppendLine($"<rect width='100%' height='100%' fill='white'/>");
        sb.AppendLine($"<text x='{Width / 2}' y='20' text-anchor='middle' font-size='16' font-weight='bold'>Player→Vehicle collision (6 s): player + collided-rigidbody pos &amp; velocity (Z, m | m/s). Forward = -Z.</text>");

        // Legend.
        int legendY = TitleH + 8;
        int legendX = Margin;
        for (int i = 0; i < traces.Length; i++)
        {
            sb.AppendLine($"<line x1='{legendX}' y1='{legendY + 6}' x2='{legendX + 24}' y2='{legendY + 6}' stroke='{colors[i]}' stroke-width='2.5'/>");
            sb.AppendLine($"<text x='{legendX + 30}' y='{legendY + 10}'>{Escape(traces[i].Name)}</text>");
            legendX += 8 + 24 + 8 + (traces[i].Name.Length * 8) + 16;
        }

        // Three subplots — top: player Z, middle: collided rigidbody Z, bottom: collided rigidbody Vz.
        int row0 = TitleH + LegendH;
        DrawSubplot(sb, "Player Z (m)", row0, Margin, Width - Margin, PlotH,
                    frameCount, playerMin, playerMax,
                    traces, accessor: f => f.PlayerZ, colors);
        int row1 = row0 + PlotH + Margin;
        DrawSubplot(sb, "Collided rigidbody Z (m)", row1, Margin, Width - Margin, PlotH,
                    frameCount, vehicleMin, vehicleMax,
                    traces, accessor: f => f.VehicleZ, colors);
        int row2 = row1 + PlotH + Margin;
        DrawSubplot(sb, "Collided rigidbody Vz (m/s)", row2, Margin, Width - Margin, PlotH,
                    frameCount, vzMin, vzMax,
                    traces, accessor: f => f.VehicleVZ, colors);

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
        // Z-component of the collided rigidbody's linear velocity at the END of this
        // frame (after SpaceStep). For host_* the visible body is the lag-buffered
        // dummy; we still capture the SERVER vehicle's velocity here because that's
        // what drives the "perceived jerk" the user wants to investigate.
        public float VehicleVZ;
    }

    private class Trace
    {
        public string Description = "";
        public List<FrameSample> Frames = new();
    }
}
