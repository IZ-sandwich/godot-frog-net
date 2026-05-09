using System.Collections.Generic;
using System.Threading.Tasks;
using GameDemo;
using GdUnit4;
using Godot;
using MonkeNet.Shared;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// PD-01..PD-03: Cross-instance / cross-run physics determinism.
///
/// Existing PR-03 / V-01 / V-02 verify same-space single-body determinism. These tests
/// extend that guarantee in two directions: (PD-01) two distinct physics spaces fed
/// identical inputs must produce identical trajectories — the strict server-vs-client
/// determinism contract; (PD-02) reconciling back to a captured state and re-running
/// reproduces the exact same final state — the contract HandleReconciliation depends
/// on; (PD-03) replaying a recorded input list from a cold body twice must match,
/// catching solver-state leakage between runs.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class PhysicsDeterminismTests
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

    // PD-01 ─────────────────────────────────────────────────────────────────────
    // Sequential-replay determinism with mixed linear + angular impulses over 60
    // ticks. Two runs on the same body in the same space must produce identical
    // final pose AND rotation. Tighter than V-01/PR-03 (1e-8 / 1e-6 vs 1e-4) and
    // exercises angular integration as well as translation — catches per-axis
    // float-order regressions and rotation-specific Jolt drift.
    //
    // Caveat: this is same-space, not truly cross-instance. Two World3Ds in one
    // Godot test process is impractical with ISceneRunner; the contract a server
    // and client process actually need is "same function of state + input",
    // which this validates.
    [TestCase]
    public void Determinism_TwoRunsWithIdenticalInputs_ProduceIdenticalState()
    {
        var space = _runner.Scene().GetViewport().World3D.Space;
        var (body, predictionRb) = MakeBody();

        // 60-tick input sequence, varying force direction so the body actually moves
        // in 3D rather than along a single axis (catches axis-specific determinism bugs).
        // Pair each linear impulse with a torque impulse so rotation is also exercised —
        // angular integration determinism is checked at the end via quaternion dot.
        var linearImpulses = new Vector3[60];
        var torqueImpulses = new Vector3[60];
        for (int i = 0; i < 60; i++)
        {
            linearImpulses[i] = new Vector3(0.3f * Mathf.Cos(i * 0.4f), 0.05f, 0.3f * Mathf.Sin(i * 0.4f));
            torqueImpulses[i] = new Vector3(0.05f * Mathf.Sin(i * 0.3f), 0.05f * Mathf.Cos(i * 0.5f), 0.05f);
        }

        // Run 1.
        for (int t = 0; t < 60; t++)
        {
            predictionRb.AddImpulse(linearImpulses[t]);
            predictionRb.AddTorqueImpulse(torqueImpulses[t]);
            predictionRb.Simulate();
            PhysicsServer3D.SpaceStep(space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(space);
        }
        Vector3 run1Pos = body.GlobalPosition;
        Vector3 run1Vel = body.LinearVelocity;
        Vector3 run1AngVel = body.AngularVelocity;
        Quaternion run1Rot = body.Quaternion;

        // Sanity — body actually moved AND rotated, otherwise the comparison is trivial.
        AssertThat(run1Pos.LengthSquared()).IsGreater(0.01f);
        AssertThat(run1AngVel.LengthSquared()).IsGreater(0.001f);
        AssertThat(1f - Mathf.Abs(run1Rot.Dot(Quaternion.Identity))).IsGreater(1e-4f);

        // Reset to the EXACT initial state.
        predictionRb.Reconcile(new RigidbodyState
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            LinearVelocity = Vector3.Zero,
            AngularVelocity = Vector3.Zero,
        });

        // Run 2.
        for (int t = 0; t < 60; t++)
        {
            predictionRb.AddImpulse(linearImpulses[t]);
            predictionRb.AddTorqueImpulse(torqueImpulses[t]);
            predictionRb.Simulate();
            PhysicsServer3D.SpaceStep(space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(space);
        }

        AssertThat((run1Pos - body.GlobalPosition).LengthSquared())
            .OverrideFailureMessage($"position determinism: run1={run1Pos} run2={body.GlobalPosition}")
            .IsLess(1e-8f);
        AssertThat((run1Vel - body.LinearVelocity).LengthSquared())
            .OverrideFailureMessage($"velocity determinism: run1={run1Vel} run2={body.LinearVelocity}")
            .IsLess(1e-6f);
        AssertThat((run1AngVel - body.AngularVelocity).LengthSquared())
            .IsLess(1e-6f);
        // Quaternion comparison via dot product — sign-flipped quaternions represent the
        // same rotation, so |1 - |dot|| should be near zero.
        AssertThat(1f - Mathf.Abs(run1Rot.Dot(body.Quaternion))).IsLess(1e-4f);
    }

    // PD-02 ─────────────────────────────────────────────────────────────────────
    // Reconciliation must be idempotent under contact: place the body on a static
    // floor with gravity, capture state at tick T, run forward 30 ticks (resting
    // contact + lateral motion), Reconcile back to T, run forward 30 ticks again.
    // The two runs must match. The floor + gravity ensures a non-trivial contact
    // manifold is active when state is captured, so this actually exercises Reconcile's
    // ResetPhysicsInterpolation + Jolt warm-start invalidation paths — the parts
    // most likely to silently leak state across the rollback.
    [TestCase]
    public void Determinism_ReconciliationIsIdempotent()
    {
        var space = _runner.Scene().GetViewport().World3D.Space;
        AddStaticFloor(yPosition: -1f);
        var (body, predictionRb) = MakeBody(gravityScale: 1f, startY: 0.5f);

        var inputs = new Vector3[30];
        for (int i = 0; i < 30; i++)
            inputs[i] = new Vector3((i % 5 - 2) * 0.4f, 0f, ((i + 2) % 4 - 2) * 0.4f);

        // Phase 1: drive 10 ticks so the body settles onto the floor with sliding contact.
        for (int t = 0; t < 10; t++)
        {
            predictionRb.AddImpulse(inputs[t]);
            predictionRb.Simulate();
            PhysicsServer3D.SpaceStep(space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(space);
        }
        RigidbodyState captured = predictionRb.SnapshotState();

        // Phase 2: forward 30 ticks.
        for (int t = 0; t < 30; t++)
        {
            predictionRb.AddImpulse(inputs[t]);
            predictionRb.Simulate();
            PhysicsServer3D.SpaceStep(space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(space);
        }
        Vector3 firstFinalPos = body.GlobalPosition;
        Vector3 firstFinalVel = body.LinearVelocity;

        // Phase 3: Reconcile back to T, forward 30 ticks again with the SAME inputs.
        predictionRb.Reconcile(captured);
        for (int t = 0; t < 30; t++)
        {
            predictionRb.AddImpulse(inputs[t]);
            predictionRb.Simulate();
            PhysicsServer3D.SpaceStep(space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(space);
        }

        AssertThat((firstFinalPos - body.GlobalPosition).LengthSquared())
            .OverrideFailureMessage($"reconcile-replay position drift: first={firstFinalPos} second={body.GlobalPosition}")
            .IsLess(1e-4f);
        AssertThat((firstFinalVel - body.LinearVelocity).LengthSquared())
            .IsLess(1e-4f);
    }

    // PD-03 ─────────────────────────────────────────────────────────────────────
    // Replay determinism with a cold start: spawn a fresh body, run a recorded input
    // list. Spawn another fresh body, run the same list. Final states must match.
    // Catches solver-state leakage between bodies (e.g. shared persistent buffers).
    [TestCase]
    public void Determinism_ColdStartReplayMatches()
    {
        var space = _runner.Scene().GetViewport().World3D.Space;

        var inputs = new Vector3[60];
        for (int i = 0; i < 60; i++)
            inputs[i] = new Vector3(Mathf.Sin(i * 0.3f), Mathf.Cos(i * 0.5f), Mathf.Sin(i * 0.7f)) * 0.4f;

        // Cold body 1.
        var (body1, pr1) = MakeBody();
        for (int t = 0; t < 60; t++)
        {
            pr1.AddImpulse(inputs[t]);
            pr1.Simulate();
            PhysicsServer3D.SpaceStep(space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(space);
        }
        Vector3 body1Pos = body1.GlobalPosition;
        Vector3 body1Vel = body1.LinearVelocity;

        // Remove body1 synchronously so its body doesn't sit at origin and overlap
        // body2's spawn. QueueFree is deferred to the next idle frame which is too
        // late — body2's first SpaceStep would have body1 still in the space.
        body1.GetParent().RemoveChild(body1);
        body1.Free();

        // Cold body 2 — same setup, replay the same inputs.
        var (body2, pr2) = MakeBody();
        for (int t = 0; t < 60; t++)
        {
            pr2.AddImpulse(inputs[t]);
            pr2.Simulate();
            PhysicsServer3D.SpaceStep(space, 1f / 60f);
            PhysicsServer3D.SpaceFlushQueries(space);
        }

        AssertThat((body1Pos - body2.GlobalPosition).LengthSquared())
            .OverrideFailureMessage($"cold-start replay drift: body1={body1Pos} body2={body2.GlobalPosition}")
            .IsLess(1e-4f);
        AssertThat((body1Vel - body2.LinearVelocity).LengthSquared())
            .IsLess(1e-4f);
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
        // RigidBody3D is silently dropped by Jolt (the body isn't fully
        // registered in the physics server until at least one SpaceStep runs).
        // Doing one no-op step here makes determinism test setups match what
        // production would see (where bodies live in the scene tree across
        // idle frames before any impulse is applied).
        var space = _runner.Scene().GetViewport().World3D.Space;
        PhysicsServer3D.SpaceStep(space, 0f);
        PhysicsServer3D.SpaceFlushQueries(space);
        return (body, predictionRb);
    }

    private void AddStaticFloor(float yPosition)
    {
        var floor = new StaticBody3D { Position = new Vector3(0, yPosition, 0) };
        var shape = new BoxShape3D { Size = new Vector3(20, 0.5f, 20) };
        floor.AddChild(new CollisionShape3D { Shape = shape });
        _runner.Scene().AddChild(floor);
    }
}
