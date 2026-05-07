using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace GameDemo;

public partial class LocalRigidPlayerPrediction : ClientPredictedEntity
{
    // 20 cm hard-reconcile threshold (0.04 m²), matching LocalVehiclePrediction.
    [Export] private float _maxDeviationAllowedSquared = 0.04f;
    // Per-snapshot fraction of accumulated drift to absorb when below the hard reconcile
    // threshold. 0.1 = same as vehicle/ball. Position-only — see LocalVehiclePrediction
    // for why velocity/rotation lerps are deliberately omitted.
    [Export] private float _softCorrectionBlend = 0.1f;
    [Export] private PredictionRigidbody3D _predictionRb;

    public override void OnProcessTick(int tick, IPackableElement input)
    {
        if (input is CharacterInputMessage cmd)
            RigidPlayerPhysics.AdvancePhysics(_predictionRb, cmd);
    }

    public override Vector3 GetPosition() => _predictionRb.Body.GlobalPosition;

    public override Vector3 ExtractAuthoritativePosition(IEntityStateData state) =>
        ((EntityStateMessage)state).Position;

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

    public override void ResimulateTick(IPackableElement input)
    {
        if (input is CharacterInputMessage cmd)
            RigidPlayerPhysics.AdvancePhysics(_predictionRb, cmd);
    }

    public override void ApplySoftCorrection(IEntityStateData receivedState, Vector3 savedPositionAtTick)
    {
        if (_softCorrectionBlend <= 0f) return;
        var state = (EntityStateMessage)receivedState;
        Vector3 posError = savedPositionAtTick - state.Position;
        _predictionRb.Body.GlobalPosition -= posError * _softCorrectionBlend;
    }
}
