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

    public override bool HasMisspredicted(int tick, IEntityStateData receivedState, Vector3 savedState)
    {
        EntityStateMessage state = (EntityStateMessage)receivedState;
        return (state.Position - savedState).LengthSquared() > _maxDeviationAllowedSquared;
    }

    public override void HandleReconciliation(IEntityStateData receivedState)
    {
        var state = (EntityStateMessage)receivedState;
        _predictionRb.Reconcile(new RigidbodyState
        {
            Position = state.Position,
            Rotation = Quaternion.FromEuler(state.Rotation),
            LinearVelocity = state.Velocity,
            AngularVelocity = state.AngularVelocity,
        });
    }

    public override void ResimulateTick(IPackableElement input) { }

    public override void ApplySoftCorrection(IEntityStateData receivedState, Vector3 savedPositionAtTick)
    {
        if (_softCorrectionBlend <= 0f) return;
        var state = (EntityStateMessage)receivedState;
        // Position-only error correction: shift body's CURRENT pos backward by a fraction
        // of the misprediction error at tick T. Preserves the body's motion since tick T
        // while gradually undoing the error itself. See LocalVehiclePrediction for why
        // velocity/rotation lerps are deliberately omitted.
        Vector3 posError = savedPositionAtTick - state.Position;
        _predictionRb.Body.GlobalPosition -= posError * _softCorrectionBlend;
    }
}
