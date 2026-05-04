using Godot;
using MonkeNet.Client;
using MonkeNet.Shared;

namespace GameDemo;

public partial class DummyBallStateInterpolation : ClientInterpolatedEntity
{
    [Export] private Node3D _parent;

    public override void HandleStateInterpolation(IEntityStateData past, IEntityStateData future, float interpolationFactor)
    {
        var pastState = (EntityStateMessage)past;
        var futureState = (EntityStateMessage)future;

        _parent.Position = pastState.Position.Lerp(futureState.Position, interpolationFactor);
        _parent.Rotation = pastState.Rotation.Lerp(futureState.Rotation, interpolationFactor);
    }
}
