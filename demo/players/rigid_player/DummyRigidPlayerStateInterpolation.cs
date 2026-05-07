using Godot;
using MonkeNet.Client;
using MonkeNet.Shared;

namespace GameDemo;

public partial class DummyRigidPlayerStateInterpolation : ClientInterpolatedEntity
{
    [Export] private Node3D _parent;

    public override void HandleStateInterpolation(IEntityStateData past, IEntityStateData future, float interpolationFactor)
    {
        var pastState = (EntityStateMessage)past;
        var futureState = (EntityStateMessage)future;
        _parent.Position = pastState.Position.Lerp(futureState.Position, interpolationFactor);

        // Slerp on quaternions to avoid the ±π wrap that Vector3.Lerp on Euler angles
        // produces. Same rationale as DummyVehicleStateInterpolation.
        var pastQ = Quaternion.FromEuler(pastState.Rotation);
        var futureQ = Quaternion.FromEuler(futureState.Rotation);
        _parent.Quaternion = pastQ.Slerp(futureQ, interpolationFactor);
    }
}
