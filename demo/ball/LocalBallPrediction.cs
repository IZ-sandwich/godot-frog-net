using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace GameDemo;

public partial class LocalBallPrediction : ClientPredictedEntity
{
    // Mirror LocalVehiclePrediction: 20 cm tolerance accommodates Jolt collision-response
    // nondeterminism between client and server (contact normals, friction, persistent-
    // contact caches all diverge by a few cm per impact). 3 cm causes every wall/player/
    // vehicle hit to trigger reconcile, which then desyncs the ball further.
    [Export] private float _maxDeviationAllowedSquared = 0.04f;
    // Linear-velocity divergence threshold (squared, m²/s²). Without this the ball can
    // share the server's position to within tolerance while carrying noticeably different
    // momentum — the wrong velocity then writes new wrong positions every tick until the
    // position threshold trips and a hard snap fires. ~0.5 m/s default.
    [Export] private float _maxVelocityDeviationSquared = 0.25f;
    // Per-snapshot fraction of accumulated drift to absorb when below the hard reconcile
    // threshold. 0 disables soft correction. Position-only — velocity/rotation lerp toward
    // a stale tick-T value pulls the body backward in time and disrupts ongoing dynamics.
    [Export] private float _softCorrectionBlend = 0.1f;
    [Export] private PredictionRigidbody3D _predictionRb;

    public override void OnProcessTick(int tick, IPackableElement input) { }

    public override Vector3 GetPosition()
    {
        return _predictionRb.Body.GlobalPosition;
    }

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
        MonkeLogger.Debug($"[ENTITY-RECONCILE] LocalBall eid={EntityId} auth={state}");
        _predictionRb.Reconcile(new RigidbodyState
        {
            Position = state.Position,
            Rotation = state.Rotation,
            LinearVelocity = state.Velocity,
            AngularVelocity = state.AngularVelocity,
        });
    }

    public override void ResimulateTick(IPackableElement input) { }

    public override void ApplySoftCorrection(IEntityStateData receivedState, RigidbodyState savedStateAtTick)
    {
        if (_softCorrectionBlend <= 0f) return;
        var state = (EntityStateMessage)receivedState;
        var smoothing = _predictionRb.Smoothing;
        Vector3 posError = savedStateAtTick.Position - state.Position;
        Vector3 visualShift = -posError * _softCorrectionBlend;
        MonkeLogger.Debug($"[PRED-SOFT] LocalBall eid={EntityId} posError=({posError.X:F4},{posError.Y:F4},{posError.Z:F4}) blend={_softCorrectionBlend} visualShift=({visualShift.X:F4},{visualShift.Y:F4},{visualShift.Z:F4}) smootherWired={(smoothing != null)}");
        // Visual-only nudge toward authoritative. The body itself stays untouched so its
        // contact cache (Jolt warm-start manifolds) is preserved across the next SpaceStep.
        // No-op when no smoother is wired — sub-threshold drift then just accumulates until
        // the hard reconcile threshold trips, which the smoother will hide as a snap.
        if (smoothing == null) return;
        smoothing.AddDriftCorrection(visualShift);
    }
}
