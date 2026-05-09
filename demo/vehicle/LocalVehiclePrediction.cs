using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace GameDemo;

public partial class LocalVehiclePrediction : ClientPredictedEntity
{
    // Tolerance is ~20 cm because Jolt collision response is not bit-deterministic across
    // processes — contact normals, friction, and persistent-contact caches can each diverge
    // by a few cm per impact, and 3 cm (the player threshold) makes every wall hit trigger
    // a reconcile. 20 cm hides those without being visibly off authoritative; bigger drifts
    // still snap. This mirrors Fish-Net's LocalReconcileCorrectionType=None path.
    [Export] private float _maxDeviationAllowedSquared = 0.04f;
    // Linear-velocity divergence threshold (squared, m²/s²). Without this the vehicle can
    // share the server's position to within tolerance while carrying noticeably different
    // momentum — the wrong velocity writes new wrong positions every tick until the
    // position threshold trips and a hard snap fires.
    [Export] private float _maxVelocityDeviationSquared = 0.25f;
    // Per-snapshot fraction of accumulated drift to absorb when below the hard reconcile
    // threshold. 0 disables soft correction; 1 would full-snap every snapshot. 0.1 gives a
    // ~7-snapshot half-life: at 30 Hz snapshots, drift converges to zero in ~230 ms without
    // a visible jump. Position is corrected by relative shift (preserves momentum); velocity
    // and rotation are slerped toward authoritative.
    [Export] private float _softCorrectionBlend = 0.1f;
    [Export] private PredictionRigidbody3D _predictionRb;

    public override void OnProcessTick(int tick, IPackableElement input)
    {
        if (input is CharacterInputMessage cmd)
            VehiclePhysics.AdvancePhysics(_predictionRb, cmd);
    }

    public override Vector3 GetPosition() => _predictionRb.Body.GlobalPosition;

    public override RigidbodyState GetSnapshotState() => _predictionRb.SnapshotState();

    public override Vector3 ExtractAuthoritativePosition(IEntityStateData state) =>
        ((EntityStateMessage)state).Position;

    public override bool HasMisspredicted(int tick, IEntityStateData receivedState, RigidbodyState savedState)
    {
        EntityStateMessage state = (EntityStateMessage)receivedState;
        if ((state.Position - savedState.Position).LengthSquared() > _maxDeviationAllowedSquared)
            return true;
        if ((state.Velocity - savedState.LinearVelocity).LengthSquared() > _maxVelocityDeviationSquared)
            return true;
        return false;
    }

    public override void HandleReconciliation(IEntityStateData receivedState)
    {
        var state = (EntityStateMessage)receivedState;
        MonkeLogger.Debug($"[ENTITY-RECONCILE] LocalVehicle eid={EntityId} auth={state}");
        _predictionRb.Reconcile(new RigidbodyState
        {
            Position = state.Position,
            Rotation = state.Rotation,
            LinearVelocity = state.Velocity,
            AngularVelocity = state.AngularVelocity,
        });
    }

    public override void ResimulateTick(IPackableElement input)
    {
        if (input is CharacterInputMessage cmd)
            VehiclePhysics.AdvancePhysics(_predictionRb, cmd);
    }

    public override void ApplySoftCorrection(IEntityStateData receivedState, RigidbodyState savedStateAtTick)
    {
        if (_softCorrectionBlend <= 0f) return;
        var state = (EntityStateMessage)receivedState;

        // Visual-only nudge toward authoritative. We deliberately do NOT mutate body state
        // here. Mutating body.GlobalPosition every snapshot invalidates Jolt's persistent
        // contact cache and triggers depenetration on the next step — itself non-deterministic
        // across processes — which causes more drift than the correction removes.
        // We also do NOT correct velocity, angular velocity, or rotation: the snapshot
        // carries tick-T values (in the past) while the body is at tick T+latency on a
        // different part of the dynamics curve, and lerping toward stale values pulls the
        // body backward in time. The visual offset uses the error DIFF (not absolute target),
        // which preserves momentum.
        // No-op when no smoother is wired (sphere meshes, props that don't need a separate
        // visual root). The hard reconcile threshold + snap-then-smooth still catches drift.
        var smoothing = _predictionRb.Smoothing;
        Vector3 posError = savedStateAtTick.Position - state.Position;
        Vector3 visualShift = -posError * _softCorrectionBlend;
        MonkeLogger.Debug($"[PRED-SOFT] LocalVehicle eid={EntityId} posError=({posError.X:F4},{posError.Y:F4},{posError.Z:F4}) blend={_softCorrectionBlend} visualShift=({visualShift.X:F4},{visualShift.Y:F4},{visualShift.Z:F4}) smootherWired={(smoothing != null)}");
        if (smoothing == null) return;
        smoothing.AddDriftCorrection(visualShift);
    }
}
