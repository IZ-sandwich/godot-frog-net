using System.Threading.Tasks;
using GameDemo;
using GdUnit4;
using Godot;
using MonkeNet.Shared;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// PVC-01..PVC-03: Player-vs-vehicle collision determinism tests.
///
/// User reports continuous mispredictions when the player walks into the vehicle in
/// listen-server mode. The remaining suspect after the tick-ordering fix is that
/// <see cref="MonkeNet.Client.ClientPredictionManager"/>'s rollback resim runs extra
/// SpaceSteps on the SHARED physics space — which advances server-side rigid bodies
/// too (collision layers filter contact, not stepping). Server's body therefore keeps
/// drifting away from where the server simulation believes it is, making every snapshot
/// look diverged to the client.
///
/// PVC-01 verifies the baseline: with no resim, the two parallel pairs (client layers,
///   server layers) stay locked together — Jolt is deterministic per-pair within one
///   instance.
/// PVC-02 verifies the resim cascade: a server-side body that experiences extra
///   SpaceSteps from a client resim WITHOUT protection drifts measurably.
/// PVC-03 verifies the fix: wrapping the server body with
///   <see cref="OfflineRigidbody3D"/> + Snapshot/Restore around the resim restores the
///   body to its pre-resim pose, so the cascade is suppressed.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class PlayerVehicleCollisionTests
{
    private const uint ClientPlayersLayer = 1u << 1;   // 2 (project's "ClientPlayers")
    private const uint ServerPlayersLayer = 1u << 15;  // 32768 (project's "ServerPlayers")
    private const uint EnvironmentLayer = 1u << 0;     // 1
    private const uint ClientPlayersMask = EnvironmentLayer | ClientPlayersLayer;
    private const uint ServerPlayersMask = EnvironmentLayer | ServerPlayersLayer;

    private const float PositionToleranceSqr = 0.01f; // 10 cm

    private ISceneRunner _runner;

    [BeforeTest]
    public async Task SetUp()
    {
        MonkeNetConfig.Instance = null;
        _runner = ISceneRunner.Load("res://demo/MainScene.tscn", autoFree: true);
        await _runner.AwaitIdleFrame();
    }

    [AfterTest]
    public void TearDown()
    {
        _runner?.Dispose();
        MonkeNetConfig.Instance = null;
    }

    // PVC-01 ────────────────────────────────────────────────────────────────────
    // Baseline listen-server determinism: client and server pairs share the physics
    // space (different layers); identical input drives both each tick. Both pairs
    // advance through one SpaceStep per frame (no resim) and must stay locked together.
    [TestCase]
    public async Task PlayerVehicle_SharedSpace_StaysInSyncUnderCollision()
    {
        var space = _runner.Scene().GetViewport().World3D.Space;

        var clientPair = CreatePair(_runner, space, ClientPlayersLayer, ClientPlayersMask);
        var serverPair = CreatePair(_runner, space, ServerPlayersLayer, ServerPlayersMask);

        await _runner.AwaitIdleFrame();
        PhysicsServer3D.SpaceStep(space, 1f / 60f);
        PhysicsServer3D.SpaceFlushQueries(space);

        var input = new CharacterInputMessage { MoveX = 0, MoveY = -1, CameraYaw = 0, Keys = 0 };

        for (int i = 0; i < 60; i++)
        {
            clientPair.movement.AdvancePhysics(input);
            serverPair.movement.AdvancePhysics(input);
            PhysicsServer3D.SpaceStep(space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(space);
        }

        AssertPositionsClose("player", clientPair.player.GlobalPosition, serverPair.player.GlobalPosition);
        AssertPositionsClose("vehicle", clientPair.vehicle.GlobalPosition, serverPair.vehicle.GlobalPosition);
    }

    // PVC-02 ────────────────────────────────────────────────────────────────────
    // Demonstrates the resim cascade bug: a moving server-side rigid body that
    // experiences extra SpaceSteps from a simulated client rollback drifts forward
    // by exactly that many ticks of free flight, instead of staying at the post-
    // server-tick state it was supposed to.
    //
    // This isolates the bug to its core mechanic: SpaceStep advances ALL bodies in
    // the space, not just the client-predicted ones. PVC-03 verifies the fix.
    [TestCase]
    public async Task ServerBody_WithoutResimProtection_DriftsForwardEachResim()
    {
        var space = _runner.Scene().GetViewport().World3D.Space;
        var body = CreateRollingBody(_runner, position: Vector3.Zero, velocity: new Vector3(5, 0, 0));

        await _runner.AwaitIdleFrame();
        PhysicsServer3D.SpaceStep(space, 1f / 60f);
        PhysicsServer3D.SpaceFlushQueries(space);

        // One "server tick": the body advances by velocity * delta.
        PhysicsServer3D.SpaceStep(space, 1f / 60f);
        PhysicsServer3D.SpaceFlushQueries(space);
        Vector3 posAfterServerTick = body.GlobalPosition;

        // Simulate a 5-tick client resim: extra SpaceSteps on the same space, with
        // NO snapshot/restore protecting this body. Each SpaceStep integrates its
        // velocity again — body drifts forward by ~5 × velocity × delta.
        for (int r = 0; r < 5; r++)
        {
            PhysicsServer3D.SpaceStep(space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(space);
        }

        // Drift should be close to 5 ticks of motion at 5 m/s = 5/12 ≈ 0.417 m.
        // Assert it actually drifted (sanity check the bug is reproduced).
        Vector3 driftFromExpected = body.GlobalPosition - posAfterServerTick;
        AssertThat(driftFromExpected.X)
            .OverrideFailureMessage(
                $"Expected the body to drift forward (X+) by ~0.4 m due to 5 extra resim SpaceSteps; " +
                $"actual drift={driftFromExpected}. If this test fails it means SpaceStep no longer " +
                $"advances bodies, in which case the bug premise is wrong.")
            .IsGreater(0.3f);
    }

    // PVC-03 ────────────────────────────────────────────────────────────────────
    // The fix: wrapping the server body with OfflineRigidbody3D and calling
    // SnapshotAll/RestoreAll around the resim makes the body invariant to extra
    // SpaceSteps. After resim, the body is at the pose it had after the server tick —
    // exactly the pose the snapshot just sent reflects.
    [TestCase]
    public async Task ServerBody_WithResimProtection_StaysAtServerTickPose()
    {
        var space = _runner.Scene().GetViewport().World3D.Space;
        var body = CreateRollingBody(_runner, position: Vector3.Zero, velocity: new Vector3(5, 0, 0));
        var offlineRb = new OfflineRigidbody3D();
        body.AddChild(offlineRb);
        offlineRb.Body = body;

        await _runner.AwaitIdleFrame();
        PhysicsServer3D.SpaceStep(space, 1f / 60f);
        PhysicsServer3D.SpaceFlushQueries(space);

        // Server tick.
        PhysicsServer3D.SpaceStep(space, 1f / 60f);
        PhysicsServer3D.SpaceFlushQueries(space);
        Vector3 posAfterServerTick = body.GlobalPosition;

        // Client resim, bracketed by Snapshot/Restore.
        OfflineRigidbody3D.SnapshotAll();
        for (int r = 0; r < 5; r++)
        {
            PhysicsServer3D.SpaceStep(space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(space);
        }
        OfflineRigidbody3D.RestoreAll();

        // Body must be back at its post-server-tick pose, not drifted forward.
        Vector3 diff = body.GlobalPosition - posAfterServerTick;
        AssertThat(diff.LengthSquared())
            .OverrideFailureMessage(
                $"Server body drifted across resim: posAfterServerTick={posAfterServerTick}, " +
                $"posAfterResim={body.GlobalPosition}, diff={diff}")
            .IsLess(0.0001f); // 1 cm² — tight, since Snapshot/Restore should be exact
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private void AssertPositionsClose(string label, Vector3 a, Vector3 b)
    {
        Vector3 diff = a - b;
        AssertThat(diff.LengthSquared())
            .OverrideFailureMessage(
                $"{label} positions diverged: a={a}, b={b}, diff={diff}, |diff|={diff.Length():F3}m " +
                $"(tolerance √{PositionToleranceSqr} = {Mathf.Sqrt(PositionToleranceSqr):F3}m)")
            .IsLess(PositionToleranceSqr);
    }

    private struct Pair
    {
        public CharacterBody3D player;
        public SharedPlayerMovement movement;
        public RigidBody3D vehicle;
    }

    private RigidBody3D CreateRollingBody(ISceneRunner runner, Vector3 position, Vector3 velocity)
    {
        var body = new RigidBody3D
        {
            Position = position,
            Mass = 1f,
            GravityScale = 0f,
            LinearDamp = 0f,
            AngularDamp = 0f,
            PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off,
        };
        body.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.5f } });
        runner.Scene().AddChild(body);
        body.LinearVelocity = velocity;
        return body;
    }

    private Pair CreatePair(ISceneRunner runner, Rid space, uint layer, uint mask)
    {
        var interpOff = Node.PhysicsInterpolationModeEnum.Off;

        var player = new CharacterBody3D
        {
            CollisionLayer = layer,
            CollisionMask = mask,
            PhysicsInterpolationMode = interpOff,
            Position = new Vector3(0, -1f, 2f),
        };
        player.AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.5f, Height = 2f },
        });
        runner.Scene().AddChild(player);

        var movement = new SharedPlayerMovement();
        player.AddChild(movement);
        movement.Initialize(player);

        var vehicle = new RigidBody3D
        {
            CollisionLayer = layer,
            CollisionMask = mask,
            Mass = 1f,
            GravityScale = 0f,
            LinearDamp = 0f,
            AngularDamp = 0f,
            PhysicsInterpolationMode = interpOff,
            Position = new Vector3(0, -1f, -3f),
        };
        vehicle.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(2, 1, 4) },
        });
        runner.Scene().AddChild(vehicle);

        return new Pair { player = player, movement = movement, vehicle = vehicle };
    }
}
