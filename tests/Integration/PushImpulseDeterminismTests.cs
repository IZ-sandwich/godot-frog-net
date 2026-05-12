using System.Threading.Tasks;
using GameDemo;
using GdUnit4;
using Godot;
using MonkeNet.Shared;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// PID-01..PID-02: Regression tests for the player-vs-rigidbody push impulse.
///
/// Context: PVC-style listen-server scenarios run two parallel pairs of bodies in one
/// physics space — a client pair (collision layer 2) and a server pair (collision layer
/// 32768). Each pair owns its own version of every networked rigid body (cube, vehicle,
/// ball) and Jolt simulates them independently. Even with identical input on both pairs,
/// the cube's *live* LinearVelocity diverges by tiny floating-point amounts every tick
/// because Jolt's contact-resolution order is not bit-identical across the two pair
/// instances.
///
/// PVC-01 catches divergence under steady-state walking. PID-01 catches a stricter
/// property the user's logs flagged: <c>SharedPlayerMovement.PushRigidBodies</c> must
/// be deterministic per pair even when the *other* pair's cube has accumulated different
/// physics noise. The formula <c>impulse = pushDir·player_input_vel · pushStrength</c>
/// has this property. The variant <c>impulse = pushDir·(player_input_vel − cube_vel)</c>
/// does not — it feeds cross-pair cube drift back into the player's impulse and the two
/// pairs' players diverge measurably after a few seconds of contact (as in
/// monke-net_2026-05-11_13-39-30_pid33672.log: player z-velocity 1.77 client vs 2.51
/// server while static cubes were unchanged).
///
/// PID-02 covers the same property with the player walking into the vehicle entity
/// instead of a small cube, since that's the original misprediction case from the user
/// report.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class PushImpulseDeterminismTests
{
    private const uint EnvironmentLayer = 1u << 0;
    private const uint ClientPlayersLayer = 1u << 1;
    private const uint ServerPlayersLayer = 1u << 15;
    private const uint ClientMask = EnvironmentLayer | ClientPlayersLayer;
    private const uint ServerMask = EnvironmentLayer | ServerPlayersLayer;

    // Position-tolerance: 5 cm² ≈ 22 cm radius. PVC-01 uses 10 cm; we relax a touch
    // because we deliberately seed the cube velocities with a small mismatch on the
    // two pairs to model Jolt nondeterminism, so the players' steady-state positions
    // can't be expected to be bit-identical — only "stay locked together within
    // visually indistinguishable distance under the deterministic push formula."
    private const float PositionToleranceSqr = 0.05f;

    private ISceneRunner _runner;

    [BeforeTest]
    public async Task SetUp()
    {
        MonkeNetConfig.Instance = null;
        // PVC tests showed that EntitySpawner on the MonkeNet autoload leaks server
        // entities across test scenes; clear before each PID test for the same reason.
        try { EntitySpawner.Instance?.ClearServerEntities(); } catch { }
        try { EntitySpawner.Instance?.ClearClientEntities(); } catch { }
        _runner = ISceneRunner.Load("res://demo/MainScene.tscn", autoFree: true);
        await _runner.AwaitIdleFrame();
    }

    [AfterTest]
    public void TearDown()
    {
        _runner?.Dispose();
        MonkeNetConfig.Instance = null;
    }

    // PID-01 ────────────────────────────────────────────────────────────────────
    // Player walks head-on into a rigidbody cube. The two pair cubes track
    // independent Jolt simulations whose velocities drift apart from each other every
    // tick. We model that drift directly: every tick before the player advances we
    // perturb the server-pair cube's z-velocity by 5 cm/s — small enough to be
    // realistic (cross-process Jolt drift is well below that), large enough that any
    // relative-velocity-based impulse formula amplifies it into a measurable player
    // displacement over a 2-second run. The deterministic (input-velocity-only)
    // formula must keep the two players visually locked together regardless.
    [TestCase]
    public async Task PushImpulse_CharacterBody_PlayerStaysInSyncDespiteCubeJoltDrift()
    {
        await RunInputOnlyImpulseDeterminismScenario(cubeSize: new Vector3(1, 1, 1), label: "PID-01 (cube)");
    }

    // PID-02 ────────────────────────────────────────────────────────────────────
    // Same property with a vehicle-sized box (2×1×4). The vehicle's larger mass via
    // shape makes Jolt contact response noisier; this catches a regression where the
    // deterministic formula handles small cubes but not larger bodies.
    [TestCase]
    public async Task PushImpulse_CharacterBody_PlayerStaysInSyncDespiteVehicleJoltDrift()
    {
        await RunInputOnlyImpulseDeterminismScenario(cubeSize: new Vector3(2, 1, 4), label: "PID-02 (vehicle)");
    }

    // Drives the player into the cube/vehicle for 120 ticks (2 s) with a per-tick
    // perturbation of the server-pair cube's z-velocity. With the input-only push
    // formula the two player positions stay within PositionToleranceSqr. With the
    // relative-velocity formula reverted in this commit, the perturbation feeds
    // straight into the player's impulse magnitude on every contact tick and the
    // players diverge by >0.3 m within ~60 ticks.
    private async Task RunInputOnlyImpulseDeterminismScenario(Vector3 cubeSize, string label)
    {
        var space = _runner.Scene().GetViewport().World3D.Space;
        var clientPair = CreatePair(space, ClientPlayersLayer, ClientMask, cubeSize);
        var serverPair = CreatePair(space, ServerPlayersLayer, ServerMask, cubeSize);

        await _runner.AwaitIdleFrame();
        PhysicsServer3D.SpaceStep(space, 1f / 60f);
        PhysicsServer3D.SpaceFlushQueries(space);

        var input = new CharacterInputMessage { MoveX = 0, MoveY = -1, CameraYaw = 0, Keys = 0 };
        const int Ticks = 120;
        // 5 cm/s per-tick perturbation. The actual cross-process Jolt drift in
        // production is sub-millimetre, but at that magnitude a 2 s test would
        // need an order of magnitude more ticks to surface the divergence; this
        // amplifies it so the regression test runs quickly.
        var perturbation = new Vector3(0, 0, 0.05f);
        for (int i = 0; i < Ticks; i++)
        {
            // Inject the model drift into the server pair's cube only. The body must
            // already be in the broadphase for the velocity write to take effect on
            // the next step — handled by the warm-up step above.
            serverPair.cube.LinearVelocity += perturbation;

            clientPair.movement.AdvancePhysics(input);
            serverPair.movement.AdvancePhysics(input);
            PhysicsServer3D.SpaceStep(space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(space);
        }

        AssertPositionsClose($"{label} player",
            clientPair.player.GlobalPosition, serverPair.player.GlobalPosition);

        // RigidbodyContactTicks is the user-surfaced counter for "how many ticks the
        // player was in contact with a rigidbody this run." The cube perturbation we
        // inject above synthetically advances the server-pair cube forward each tick,
        // so first contact between player and cube can happen one or two ticks earlier
        // on the server pair than on the client pair — that's an artifact of the test
        // model, not of the impulse formula. Real cross-process Jolt drift is sub-mm
        // and won't shift first-contact by entire ticks. Allow up to 2 ticks of
        // difference here; ensure both pairs DID make rigid-body contact (otherwise
        // the test isn't actually exercising the push path at all).
        AssertThat(clientPair.movement.RigidbodyContactTicks)
            .OverrideFailureMessage($"{label}: client never contacted rigidbody (count=0)")
            .IsGreater(0);
        AssertThat(serverPair.movement.RigidbodyContactTicks)
            .OverrideFailureMessage($"{label}: server never contacted rigidbody (count=0)")
            .IsGreater(0);
        int contactTickDelta = System.Math.Abs(
            clientPair.movement.RigidbodyContactTicks - serverPair.movement.RigidbodyContactTicks);
        AssertThat(contactTickDelta)
            .OverrideFailureMessage(
                $"{label}: contact-tick count diverged by more than 2: client={clientPair.movement.RigidbodyContactTicks} server={serverPair.movement.RigidbodyContactTicks}")
            .IsLessEqual(2);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private void AssertPositionsClose(string label, Vector3 a, Vector3 b)
    {
        Vector3 diff = a - b;
        AssertThat(diff.LengthSquared())
            .OverrideFailureMessage(
                $"{label}: positions diverged: a={a}, b={b}, diff={diff}, |diff|={diff.Length():F3}m " +
                $"(tolerance √{PositionToleranceSqr} = {Mathf.Sqrt(PositionToleranceSqr):F3}m)")
            .IsLess(PositionToleranceSqr);
    }

    private struct Pair
    {
        public CharacterBody3D player;
        public SharedPlayerMovement movement;
        public RigidBody3D cube;
    }

    // Build one client- or server-layer pair: a CharacterBody3D player + SharedPlayerMovement
    // wired to it + a single RigidBody3D in front. Mirrors PVC-01's helper layout so the
    // determinism comparison is apples-to-apples.
    private Pair CreatePair(Rid space, uint layer, uint mask, Vector3 cubeSize)
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
        _runner.Scene().AddChild(player);

        var movement = new SharedPlayerMovement();
        player.AddChild(movement);
        movement.Initialize(player);

        var cube = new RigidBody3D
        {
            CollisionLayer = layer,
            CollisionMask = mask,
            Mass = 1f,
            GravityScale = 0f,
            LinearDamp = 0f,
            AngularDamp = 0f,
            PhysicsInterpolationMode = interpOff,
            // 1.5 m in front of the player so it gets reached within a few ticks at MaxRunSpeed.
            Position = new Vector3(0, -1f, 0f),
        };
        cube.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = cubeSize },
        });
        _runner.Scene().AddChild(cube);

        return new Pair { player = player, movement = movement, cube = cube };
    }
}
