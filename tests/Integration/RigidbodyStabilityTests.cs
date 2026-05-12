using System.Collections.Generic;
using System.Threading.Tasks;
using GdUnit4;
using Godot;
using MonkeNet.Shared;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// RBS-01..RBS-05: Rigidbody behaviour stability tests.
///
/// These cover physics scenarios that are particularly sensitive to determinism
/// and integration drift: stacking, sleep state, resting contact preservation,
/// high-speed collision response, and angular integration over time. Each test
/// runs the same setup twice (or compares to a known reference) and asserts a
/// concrete tolerance, so a regression that breaks Jolt's contact warm-start or
/// the rotational integrator will trip.
///
/// All tests use a single physics space; cross-instance comparisons live in
/// PhysicsDeterminismTests.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class RigidbodyStabilityTests
{
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

    // RBS-01 (suggestion #12) ───────────────────────────────────────────────────
    // Stacking repeatability: spawn 5 cubes in a tower at rest, run the simulation
    // forward 600 ticks (10 s) once. Capture the final stack pose. Spawn a fresh
    // tower (run 1's bodies are torn down), run the same 600 ticks again. The two
    // final stacks must agree within 1 cm per axis.
    //
    // This is same-space repeatability, not strict rollback determinism — the
    // contact-cache state from run 1 may bias run 2 since the static floor
    // persists. That's still the right shape of test for catching stack
    // collapse/lean regressions; for true reconcile + replay determinism see
    // PhysicsDeterminismTests PD-02.
    [TestCase]
    public void RigidbodyStability_StackHoldsAcrossReplay()
    {
        var space = _runner.Scene().GetViewport().World3D.Space;
        var floor1 = AddStaticFloor(yPosition: -1f);

        // Run 1.
        var (bodies1, prs1) = BuildTower(5, gapY: 1.05f, startY: 0.5f);
        for (int t = 0; t < 600; t++)
        {
            foreach (var pr in prs1) pr.Simulate();
            PhysicsServer3D.SpaceStep(space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(space);
        }
        var run1Pos = new Vector3[5];
        for (int i = 0; i < 5; i++) run1Pos[i] = bodies1[i].GlobalPosition;

        // Tear down run 1 — including the floor, so Jolt's contact-pair cache
        // for the floor↔cube manifolds doesn't leak warm-start state into run 2.
        // Without this, the second tower's settle trajectory inherits subtle
        // bias from run 1's resolved contacts and drifts ~1 cm differently.
        for (int i = 0; i < 5; i++)
        {
            bodies1[i].GetParent().RemoveChild(bodies1[i]);
            bodies1[i].Free();
        }
        floor1.GetParent().RemoveChild(floor1);
        floor1.Free();

        // Run 2 — fresh floor + fresh tower, identical inputs.
        AddStaticFloor(yPosition: -1f);
        var (bodies2, prs2) = BuildTower(5, gapY: 1.05f, startY: 0.5f);
        for (int t = 0; t < 600; t++)
        {
            foreach (var pr in prs2) pr.Simulate();
            PhysicsServer3D.SpaceStep(space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(space);
        }

        for (int i = 0; i < 5; i++)
        {
            float drift = (run1Pos[i] - bodies2[i].GlobalPosition).Length();
            AssertThat(drift)
                .OverrideFailureMessage($"stack drift cube[{i}]: {drift:F4} m exceeds 0.01 m budget (run1={run1Pos[i]} run2={bodies2[i].GlobalPosition})")
                .IsLess(0.01f);
        }
    }

    // RBS-02 (suggestion #13) ───────────────────────────────────────────────────
    // Sleep-state coherence: a body at rest must reach Sleeping=true. Run the
    // same setup twice; both runs must enter sleep within ±2 ticks of each
    // other. Catches wake-up-storm regressions where Reconcile or contact-cache
    // rebuild perturbs settle time, producing a sleeping body on one side and
    // an awake body on the other.
    [TestCase]
    public void RigidbodyStability_SleepStateConvergesAtSameTick()
    {
        var space = _runner.Scene().GetViewport().World3D.Space;
        AddStaticFloor(yPosition: -1f);

        int run1SleepTick = RunUntilSleep(space, 600);
        AssertThat(run1SleepTick)
            .OverrideFailureMessage($"body never reached Sleeping=true within 600 ticks (got {run1SleepTick}); cannot compare runs")
            .IsGreater(0);

        int run2SleepTick = RunUntilSleep(space, 600);
        AssertThat(run2SleepTick).IsGreater(0);

        int delta = Mathf.Abs(run1SleepTick - run2SleepTick);
        AssertThat(delta)
            .OverrideFailureMessage($"sleep-tick mismatch: run1={run1SleepTick} run2={run2SleepTick}")
            .IsLessEqual(2);
    }

    // RBS-03 (suggestion #14) ───────────────────────────────────────────────────
    // Resting-contact preservation: a body sitting still on the floor must not
    // drift more than 0.5 mm in Y over 300 quiet ticks. Tests warm-start manifold
    // preservation and contact-cache stability — the parts of Jolt most likely to
    // produce slow per-tick drift that accumulates to visible misprediction.
    [TestCase]
    public void RigidbodyStability_RestingContactNoDrift()
    {
        var space = _runner.Scene().GetViewport().World3D.Space;
        AddStaticFloor(yPosition: -1f);

        var (body, predictionRb) = MakeBody(gravityScale: 1f, startY: 0.5f);
        // Disallow sleep — a sleeping body literally cannot drift, which would
        // make the test trivially pass without exercising contact-cache stability.
        body.CanSleep = false;

        // Settle phase: run until the body is at rest on the floor.
        for (int t = 0; t < 120; t++)
        {
            predictionRb.Simulate();
            PhysicsServer3D.SpaceStep(space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(space);
        }
        float settledY = body.GlobalPosition.Y;
        AssertThat(body.LinearVelocity.LengthSquared())
            .OverrideFailureMessage($"body did not settle: vel={body.LinearVelocity}")
            .IsLess(0.001f);
        AssertThat(body.Sleeping)
            .OverrideFailureMessage("body slept despite CanSleep=false; resting-drift measurement would be meaningless")
            .IsFalse();

        // Quiet phase: 300 more ticks, no inputs. Track max Y deviation from settled.
        float maxDrift = 0f;
        for (int t = 0; t < 300; t++)
        {
            predictionRb.Simulate();
            PhysicsServer3D.SpaceStep(space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(space);
            float drift = Mathf.Abs(body.GlobalPosition.Y - settledY);
            if (drift > maxDrift) maxDrift = drift;
        }

        // 0.5 mm budget. Static-floor + sphere has a clean contact point so this
        // should be tight; a regression to 5 mm would still feel "stable" visually
        // but indicates contact-cache rebuild churn each tick.
        AssertThat(maxDrift)
            .OverrideFailureMessage($"resting drift {maxDrift:F5} m exceeds 0.5 mm budget")
            .IsLess(0.0005f);
    }

    // RBS-04 (suggestion #15) ───────────────────────────────────────────────────
    // High-speed collision repeatability: ball at 50 m/s into a static wall. Run
    // twice; assert reflection direction differs by < 1 degree between runs.
    // RigidBody3D.ContinuousCd defaults to Disabled, so at 50 m/s × 1/60 s ≈ 0.83 m
    // per step the ball tunnels past the wall face and Jolt resolves via
    // depenetration. The test catches depenetration repeatability regressions —
    // changes that make Jolt's substep solver produce subtly different reflection
    // vectors for the same tunneling scenario.
    [TestCase]
    public void RigidbodyStability_HighSpeedReflectionAngleStable()
    {
        var space = _runner.Scene().GetViewport().World3D.Space;
        AddStaticWall(xPosition: 5f); // wall at +X so a ball moving +X hits it head-on

        // Run 1.
        var v1 = LaunchAndCollect(space, initialVel: new Vector3(50, 0, 0), startPos: new Vector3(0, 0, 0));

        // Run 2.
        var v2 = LaunchAndCollect(space, initialVel: new Vector3(50, 0, 0), startPos: new Vector3(0, 0, 0));

        AssertThat(v1.LengthSquared()).IsGreater(1f); // sanity: ball actually bounced
        AssertThat(v2.LengthSquared()).IsGreater(1f);

        // Angle between the two reflection vectors, in degrees.
        float cosAngle = v1.Normalized().Dot(v2.Normalized());
        cosAngle = Mathf.Clamp(cosAngle, -1f, 1f);
        float angleDeg = Mathf.RadToDeg(Mathf.Acos(cosAngle));

        AssertThat(angleDeg)
            .OverrideFailureMessage($"reflection angle differs by {angleDeg:F3}° between runs (v1={v1} v2={v2})")
            .IsLess(1f);
    }

    // RBS-05 (suggestion #16) ───────────────────────────────────────────────────
    // Compound-rotation determinism: 5 torque impulses applied over 30 ticks must
    // produce the same final orientation when re-run. Angular integration
    // tolerance in Jolt is often laxer than linear — torque-impulse precession
    // accumulates faster than linear-impulse drift.
    [TestCase]
    public void RigidbodyStability_CompoundRotationReproducible()
    {
        var space = _runner.Scene().GetViewport().World3D.Space;
        var (body, predictionRb) = MakeBody();

        // 5 torques scheduled at ticks 0, 6, 12, 18, 24, each on a different axis
        // so the final orientation depends on non-commutative rotation order.
        Vector3[] torques = {
            new(2f, 0f, 0f), new(0f, 1.5f, 0f), new(0f, 0f, 1f),
            new(1f, 0.5f, 0f), new(0f, 1f, 0.5f),
        };
        int[] schedule = { 0, 6, 12, 18, 24 };

        // Run 1.
        for (int t = 0; t < 30; t++)
        {
            for (int i = 0; i < schedule.Length; i++)
                if (schedule[i] == t) predictionRb.AddTorqueImpulse(torques[i]);
            predictionRb.Simulate();
            PhysicsServer3D.SpaceStep(space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(space);
        }
        Quaternion run1Rot = body.Quaternion;

        // Sanity: body actually rotated.
        AssertThat(1f - Mathf.Abs(run1Rot.Dot(Quaternion.Identity)))
            .OverrideFailureMessage("body did not rotate; torques may not be reaching it")
            .IsGreater(0.01f);

        // Reset and re-run.
        predictionRb.Reconcile(new RigidbodyState
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            LinearVelocity = Vector3.Zero,
            AngularVelocity = Vector3.Zero,
        });
        for (int t = 0; t < 30; t++)
        {
            for (int i = 0; i < schedule.Length; i++)
                if (schedule[i] == t) predictionRb.AddTorqueImpulse(torques[i]);
            predictionRb.Simulate();
            PhysicsServer3D.SpaceStep(space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(space);
        }

        // Quaternion difference: the dot product magnitude is 1 for identical
        // rotations and decreases as they diverge. 0.001 corresponds to ~2.5°.
        float dot = run1Rot.Dot(body.Quaternion);
        float diff = 1f - Mathf.Abs(dot);
        AssertThat(diff)
            .OverrideFailureMessage($"compound-rotation drift: 1-|dot|={diff:F6} (run1={run1Rot} run2={body.Quaternion})")
            .IsLess(0.001f);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    // Runs the body until Sleeping=true (or maxTicks elapses). Returns the tick
    // count when it slept, or -1 if it never did. Spawns a fresh body each call.
    private int RunUntilSleep(Rid space, int maxTicks)
    {
        var (body, predictionRb) = MakeBody(gravityScale: 1f, startY: 0.5f);
        // Letting Jolt go to sleep requires CanSleep=true (default); explicit for clarity.
        body.CanSleep = true;
        for (int t = 0; t < maxTicks; t++)
        {
            predictionRb.Simulate();
            PhysicsServer3D.SpaceStep(space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(space);
            if (body.Sleeping)
            {
                // Tear down so subsequent runs don't share the space with this body.
                body.GetParent().RemoveChild(body);
                body.Free();
                return t;
            }
        }
        body.GetParent().RemoveChild(body);
        body.Free();
        return -1;
    }

    private Vector3 LaunchAndCollect(Rid space, Vector3 initialVel, Vector3 startPos)
    {
        var (body, predictionRb) = MakeBody(gravityScale: 0f, startY: startPos.Y);
        // Default RigidBody3D + StaticBody3D has bounce=0, so a ball into a wall
        // simply stops on impact. Add a bouncy material so reflection actually
        // produces a non-zero return velocity (the property the test measures).
        body.PhysicsMaterialOverride = new PhysicsMaterial { Bounce = 0.9f, Friction = 0.1f };
        body.GlobalPosition = startPos;
        body.LinearVelocity = initialVel;
        // Step until ball has reflected and is moving away (or 60 ticks elapsed).
        for (int t = 0; t < 60; t++)
        {
            predictionRb.Simulate();
            PhysicsServer3D.SpaceStep(space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(space);
            if (body.LinearVelocity.X < -1f) break; // bounced — heading back
        }
        Vector3 v = body.LinearVelocity;
        body.GetParent().RemoveChild(body);
        body.Free();
        return v;
    }

    private (List<RigidBody3D> bodies, List<PredictionRigidbody3D> prs)
        BuildTower(int count, float gapY, float startY)
    {
        var bodies = new List<RigidBody3D>(count);
        var prs = new List<PredictionRigidbody3D>(count);
        for (int i = 0; i < count; i++)
        {
            var body = new RigidBody3D
            {
                Position = new Vector3(0, startY + i * gapY, 0),
                GravityScale = 1f,
                Mass = 1f,
                LinearDamp = 0.1f,
                AngularDamp = 0.1f,
            };
            body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One } });
            _runner.Scene().AddChild(body);

            var pr = new PredictionRigidbody3D();
            body.AddChild(pr);
            pr.Initialize(body);
            bodies.Add(body);
            prs.Add(pr);
        }
        // Warm-up step (see MakeBody) so all bodies are fully registered with
        // the physics server before any impulse is applied.
        var space = _runner.Scene().GetViewport().World3D.Space;
        PhysicsServer3D.SpaceStep(space, 0f);
        PhysicsServer3D.SpaceFlushQueries(space);
        return (bodies, prs);
    }

    private (RigidBody3D body, PredictionRigidbody3D predictionRb) MakeBody(
        float gravityScale = 0f, float startY = 0f)
    {
        var body = new RigidBody3D
        {
            GravityScale = gravityScale,
            Mass = 1f,
            LinearDamp = 0f,
            AngularDamp = 0f,
            Position = new Vector3(0, startY, 0),
        };
        body.AddChild(new CollisionShape3D { Shape = new SphereShape3D() });
        _runner.Scene().AddChild(body);

        var predictionRb = new PredictionRigidbody3D();
        body.AddChild(predictionRb);
        predictionRb.Initialize(body);

        // Warm-up step: the FIRST ApplyTorqueImpulse on a freshly-AddChild'd
        // RigidBody3D is silently dropped by Jolt (body not yet fully
        // registered with the physics server). One no-op SpaceStep registers
        // it so subsequent impulses take effect on the very first tick.
        var space = _runner.Scene().GetViewport().World3D.Space;
        PhysicsServer3D.SpaceStep(space, 0f);
        PhysicsServer3D.SpaceFlushQueries(space);
        return (body, predictionRb);
    }

    private StaticBody3D AddStaticFloor(float yPosition)
    {
        var floor = new StaticBody3D { Position = new Vector3(0, yPosition, 0) };
        var shape = new BoxShape3D { Size = new Vector3(20, 0.5f, 20) };
        floor.AddChild(new CollisionShape3D { Shape = shape });
        _runner.Scene().AddChild(floor);
        return floor;
    }

    private void AddStaticWall(float xPosition)
    {
        var wall = new StaticBody3D { Position = new Vector3(xPosition, 0, 0) };
        var shape = new BoxShape3D { Size = new Vector3(0.5f, 20, 20) };
        wall.AddChild(new CollisionShape3D { Shape = shape });
        _runner.Scene().AddChild(wall);
    }
}
