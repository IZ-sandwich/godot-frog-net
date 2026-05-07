using Godot;
using MonkeNet.Server;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace GameDemo;

public partial class ServerRigidPlayerStateSyncronizer : ServerStateSyncronizer
{
    [Export] private PredictionRigidbody3D _predictionRb;

    public override void OnProcessTick(int tick, IPackableElement input)
    {
        if (input is CharacterInputMessage cmd)
            RigidPlayerPhysics.AdvancePhysics(_predictionRb, cmd);
    }

    public override IEntityStateData PackEntityState()
    {
        var state = _predictionRb.SnapshotState();
        return new EntityStateMessage
        {
            EntityId = this.EntityId,
            Yaw = 0,
            Position = state.Position,
            Rotation = state.Rotation.GetEuler(),
            Velocity = state.LinearVelocity,
            AngularVelocity = state.AngularVelocity,
        };
    }
}
