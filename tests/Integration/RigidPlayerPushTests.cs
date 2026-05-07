using System.Threading.Tasks;
using GameDemo;
using GdUnit4;
using Godot;
using MonkeNet.Shared;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// RP-PUSH-01..RP-PUSH-04: <see cref="RigidPlayerPhysics"/> collision-response tests.
///
/// The bug these tests guard against is that the original <c>AdvancePhysics</c>
/// implementation called <c>SetLinearVelocity(target)</c> every tick. That sets the
/// body's velocity outright — overwriting any post-collision velocity Jolt's contact
/// solver computed in the previous step. Visible symptoms:
///   * The player phases through the vehicle/ball at full speed each tick.
///   * The other body picks up only a tiny per-tick contact impulse (whatever Jolt
///     can apply in one step) and lags far behind the player.
///   * In multi-process play the sustained "pressure" amplifies cross-Jolt
///     nondeterminism into 25-30 cm mispredictions per impact.
///
/// The fix: use <c>AddImpulse</c> with a max-acceleration cap. The horizontal impulse
/// is at most <c>MaxHorizontalAcceleration * dt * mass</c>, so when collision response
/// reduces the body's velocity it is *not* fully restored on the next tick. The pair
/// instead converges to a shared velocity — the natural rigid-rigid response a player
/// would expect from a heavy character pushing a similarly-massed object.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class RigidPlayerPushTests
{
    private ISceneRunner _runner;
    private Rid _space;
    private RigidBody3D _player;
    private PredictionRigidbody3D _predictionRb;
    private RigidBody3D _vehicle;
    private RigidBody3D _ball;

    [BeforeTest]
    public async Task SetUp()
    {
        // MainScene sets up MonkeNetConfig and a viewport with a Jolt-backed physics space.
        // MonkeNetManager._EnterTree calls SpaceSetActive(false) so we step manually.
        MonkeNetConfig.Instance = null;
        _runner = ISceneRunner.Load("res://demo/MainScene.tscn", autoFree: true);
        await _runner.AwaitIdleFrame();
        _space = _runner.Scene().GetViewport().World3D.Space;

        var interpOff = Node.PhysicsInterpolationModeEnum.Off;

        // RigidBody3D player. GravityScale = 0 isolates horizontal collision physics; lock
        // rotation so the body stays upright without leaning. Layers/mask mirror
        // LocalRigidPlayer.tscn (2 = ClientPlayers, 3 = Environment + ClientPlayers).
        _player = new RigidBody3D
        {
            CollisionLayer = 2,
            CollisionMask = 3,
            Mass = 1f,
            GravityScale = 0f,
            LinearDamp = 0f,
            AngularDamp = 0f,
            LockRotation = true,
            PhysicsInterpolationMode = interpOff,
            Position = new Vector3(0, -1f, 0),
        };
        _player.AddChild(new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.5f, Height = 2f },
        });
        _runner.Scene().AddChild(_player);

        _predictionRb = new PredictionRigidbody3D();
        _player.AddChild(_predictionRb);
        _predictionRb.Initialize(_player);

        // Vehicle: equal mass, idle (no input). With current code it should pick up only a
        // small velocity per tick of contact; with the fix it should converge to the
        // player's velocity (momentum exchange + sustained engine drive).
        _vehicle = new RigidBody3D
        {
            CollisionLayer = 2,
            CollisionMask = 3,
            Mass = 1f,
            GravityScale = 0f,
            LinearDamp = 0f,
            AngularDamp = 0f,
            PhysicsInterpolationMode = interpOff,
            Position = new Vector3(50, 50, 50),
        };
        _vehicle.AddChild(new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(2, 1, 4) },
        });
        _runner.Scene().AddChild(_vehicle);

        _ball = new RigidBody3D
        {
            CollisionLayer = 2,
            CollisionMask = 3,
            Mass = 1f,
            GravityScale = 0f,
            LinearDamp = 0f,
            PhysicsInterpolationMode = interpOff,
            Position = new Vector3(50, 50, 50),
        };
        _ball.AddChild(new CollisionShape3D { Shape = new SphereShape3D { Radius = 0.5f } });
        _runner.Scene().AddChild(_ball);

        await _runner.AwaitIdleFrame();
        PhysicsServer3D.SpaceStep(_space, 1f / 60f);
        PhysicsServer3D.SpaceFlushQueries(_space);
    }

    // Teleport-style position set for a RigidBody3D; same helper as PlayerPushTests.
    private void SetBodyPosition(RigidBody3D body, Vector3 pos)
    {
        var t = body.GlobalTransform;
        t.Origin = pos;
        PhysicsServer3D.BodySetState(body.GetRid(), PhysicsServer3D.BodyState.Transform, t);
        body.GlobalPosition = pos;
        body.LinearVelocity = Vector3.Zero;
        body.AngularVelocity = Vector3.Zero;
    }

    [AfterTest]
    public void TearDown()
    {
        _runner?.Dispose();
        MonkeNetConfig.Instance = null;
    }

    // RP-PUSH-01 ────────────────────────────────────────────────────────────
    // Head-on push into the vehicle. Player and vehicle have equal mass; after
    // sustained contact the pair must converge to a shared velocity (rigid-rigid
    // physics). Under the bug, the player snaps back to MaxRunSpeed each tick while
    // the vehicle accumulates only small per-tick impulses, so the velocity gap
    // never closes and the vehicle visually lags.
    [TestCase]
    public void RigidPlayerPush_HeadOnIntoVehicle_PlayerAndVehicleConvergeInVelocity()
    {
        SetBodyPosition(_player, new Vector3(0, -1f, 0));
        SetBodyPosition(_vehicle, new Vector3(0, -1f, -3f));
        SetBodyPosition(_ball, new Vector3(50, 50, 50));
        PhysicsServer3D.SpaceFlushQueries(_space);

        // MoveY = -1 → forward (-Z). Drive long enough for any transient to decay.
        var input = new CharacterInputMessage { MoveX = 0, MoveY = -1, CameraYaw = 0 };

        for (int i = 0; i < 90; i++)
        {
            RigidPlayerPhysics.AdvancePhysics(_predictionRb, input);
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }

        float velGapZ = Mathf.Abs(_player.LinearVelocity.Z - _vehicle.LinearVelocity.Z);
        AssertThat(velGapZ)
            .OverrideFailureMessage(
                $"Player and vehicle velocity diverge after sustained contact — collision " +
                $"response is being overridden each tick. " +
                $"player.vel={_player.LinearVelocity}, vehicle.vel={_vehicle.LinearVelocity}, " +
                $"|gap.Z|={velGapZ:F2} m/s (expected < 1 m/s).")
            .IsLess(1f);

        AssertThat(_vehicle.LinearVelocity.Z)
            .OverrideFailureMessage(
                $"Vehicle did not pick up significant forward velocity from sustained contact. " +
                $"vehicle.vel={_vehicle.LinearVelocity} (expected vel.Z < -2 m/s).")
            .IsLess(-2f);

        AssertThat(_player.LinearVelocity.Z)
            .OverrideFailureMessage(
                $"Player not advancing forward — body may be stuck against vehicle. " +
                $"player.vel={_player.LinearVelocity}.")
            .IsLess(-1f);
    }

    // RP-PUSH-02 ────────────────────────────────────────────────────────────
    // Head-on push into the ball: simpler check that the rigid player's collision
    // response actually transfers momentum. Mirrors PUSH-01 from PlayerPushTests but
    // with the rigid-body player driving the contact via PredictionRigidbody3D.
    [TestCase]
    public void RigidPlayerPush_HeadOnIntoBall_PushesBall()
    {
        SetBodyPosition(_player, new Vector3(0, -1f, 0));
        SetBodyPosition(_ball, new Vector3(0, -1f, -1.5f));
        SetBodyPosition(_vehicle, new Vector3(50, 50, 50));
        PhysicsServer3D.SpaceFlushQueries(_space);

        var input = new CharacterInputMessage { MoveX = 0, MoveY = -1, CameraYaw = 0 };

        for (int i = 0; i < 30; i++)
        {
            RigidPlayerPhysics.AdvancePhysics(_predictionRb, input);
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }

        AssertThat(_ball.LinearVelocity.LengthSquared())
            .OverrideFailureMessage($"Ball did not move after head-on push; velocity={_ball.LinearVelocity}.")
            .IsGreater(0.1f);
        AssertThat(_ball.LinearVelocity.Z)
            .OverrideFailureMessage($"Ball pushed in wrong direction; velocity={_ball.LinearVelocity}.")
            .IsLess(-0.5f);
    }

    // RP-PUSH-03 ────────────────────────────────────────────────────────────
    // No input ⇒ no push. With the player at rest in contact with the vehicle, the
    // vehicle must remain at rest — guards against false positives from the impulse
    // path applying motion that wasn't requested.
    [TestCase]
    public void RigidPlayerPush_NoInput_DoesNotPushVehicle()
    {
        SetBodyPosition(_player, new Vector3(0, -1f, 0));
        SetBodyPosition(_vehicle, new Vector3(0, -1f, -1.5f));
        SetBodyPosition(_ball, new Vector3(50, 50, 50));
        PhysicsServer3D.SpaceFlushQueries(_space);

        var input = new CharacterInputMessage { MoveX = 0, MoveY = 0, CameraYaw = 0 };

        for (int i = 0; i < 30; i++)
        {
            RigidPlayerPhysics.AdvancePhysics(_predictionRb, input);
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }

        AssertThat(_vehicle.LinearVelocity.LengthSquared())
            .OverrideFailureMessage($"Vehicle moved without input; velocity={_vehicle.LinearVelocity}.")
            .IsLess(0.01f);
    }

    // RP-JUMP-01 ────────────────────────────────────────────────────────────
    // Jumping must work when the player rests on a flat floor. This test
    // replicates the LocalRigidPlayer.tscn geometry where the CollisionShape3D
    // is offset by Y=+1 from the body — so when the player settles on the floor,
    // `body.GlobalPosition.Y` equals the floor's top surface. Under the bug, the
    // ground-probe ray started AT body.GlobalPosition.Y, which sat exactly on the
    // floor surface; PhysicsServer3D.IntersectRay returns no hit when the origin
    // is on/inside a body, so IsOnGround silently returned false and jump input
    // was ignored on flat ground. The fix lifts the ray origin a few cm above
    // the body so the start is unambiguously in free space.
    [TestCase]
    public async Task RigidPlayer_OnFlatFloor_JumpsWhenSpacePressed()
    {
        // Park the existing _player far away — this test builds a fresh body that
        // mirrors the scene's CollisionShape3D offset.
        SetBodyPosition(_player, new Vector3(60, 60, 60));
        SetBodyPosition(_vehicle, new Vector3(50, 50, 50));
        SetBodyPosition(_ball, new Vector3(50, 50, 50));

        var interpOff = Node.PhysicsInterpolationModeEnum.Off;

        // Body.Position.Y = -2 puts body's origin on the floor's top surface (the
        // floor in MainScene is a CSGBox3D centered at Y=-2.5 with size Y=1, so
        // its top is at Y=-2). The CollisionShape3D is offset Y=+1 to match the
        // scene, so the capsule extends from body.Y to body.Y + 2 — bottom flush
        // with the floor.
        var body = new RigidBody3D
        {
            CollisionLayer = 2,
            CollisionMask = 3,
            Mass = 1f,
            LinearDamp = 0f,
            AngularDamp = 0f,
            LockRotation = true,
            PhysicsInterpolationMode = interpOff,
            Position = new Vector3(0, -2f, 0),
        };
        var shape = new CollisionShape3D
        {
            Shape = new CapsuleShape3D { Radius = 0.5f, Height = 2f },
            Position = new Vector3(0, 1f, 0),
        };
        body.AddChild(shape);
        _runner.Scene().AddChild(body);

        var predictionRb = new PredictionRigidbody3D();
        body.AddChild(predictionRb);
        predictionRb.Initialize(body);

        await _runner.AwaitIdleFrame();

        // Settle the body so any spawn-time penetration resolution finishes.
        for (int i = 0; i < 5; i++)
        {
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }

        float yBeforeJump = body.GlobalPosition.Y;
        var jumpInput = new CharacterInputMessage
        {
            MoveX = 0,
            MoveY = 0,
            CameraYaw = 0,
            Keys = (byte)InputFlags.Space,
        };

        // One tick of jump input. AdvancePhysics should detect ground, set
        // LinearVelocity.Y to JumpVelocity, and the body should leave the floor
        // immediately on the next SpaceStep.
        RigidPlayerPhysics.AdvancePhysics(predictionRb, jumpInput);
        PhysicsServer3D.SpaceStep(_space, 1f / 60f);
        PhysicsServer3D.SpaceFlushQueries(_space);

        // After one tick at JumpVelocity = 6 m/s the body should have moved up by
        // ~6/60 = 0.1 m (less Jolt's gravity tick, ~0.083 m). Anything above
        // a couple cm rules out "jump silently ignored" while remaining loose
        // enough not to assume an exact integration scheme.
        float yAfterJump = body.GlobalPosition.Y;
        AssertThat(yAfterJump - yBeforeJump)
            .OverrideFailureMessage(
                $"Jump on flat floor did not lift the body — IsOnGround likely returned " +
                $"false because the ground probe ray started on the floor's top surface. " +
                $"yBeforeJump={yBeforeJump:F4}, yAfterJump={yAfterJump:F4}, " +
                $"delta={yAfterJump - yBeforeJump:F4} m (expected > 0.05 m).")
            .IsGreater(0.05f);
    }

    // RP-PUSH-05 ────────────────────────────────────────────────────────────
    // After a head-on push the vehicle's POSITION must visibly advance. In
    // ~60 ticks of contact at MaxRunSpeed an equal-mass pair should travel
    // a noticeable distance forward.
    [TestCase]
    public void RigidPlayerPush_HeadOnIntoVehicle_VehicleAdvancesPromptly()
    {
        SetBodyPosition(_player, new Vector3(0, -1f, 0));
        Vector3 vehicleStart = new Vector3(0, -1f, -3f);
        SetBodyPosition(_vehicle, vehicleStart);
        SetBodyPosition(_ball, new Vector3(50, 50, 50));
        PhysicsServer3D.SpaceFlushQueries(_space);

        var input = new CharacterInputMessage { MoveX = 0, MoveY = -1, CameraYaw = 0 };

        for (int i = 0; i < 60; i++)
        {
            RigidPlayerPhysics.AdvancePhysics(_predictionRb, input);
            PhysicsServer3D.SpaceStep(_space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(_space);
        }

        float vehicleAdvance = vehicleStart.Z - _vehicle.GlobalPosition.Z; // forward = -Z, so positive when advanced
        AssertThat(vehicleAdvance)
            .OverrideFailureMessage(
                $"Vehicle did not advance promptly under sustained push. " +
                $"start.Z={vehicleStart.Z:F2}, end.Z={_vehicle.GlobalPosition.Z:F2}, " +
                $"advance={vehicleAdvance:F2} m (expected > 1 m).")
            .IsGreater(1f);
    }
}
