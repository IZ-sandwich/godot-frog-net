using Godot;
using MonkeNet.Server;
using MonkeNet.Shared;

namespace GameDemo;

public partial class ServerBallStateSyncronizer : ServerStateSyncronizer
{
    [Export] private RigidBody3D _rigidBody;

    public override void OnEntitySpawned()
    {
        GetParent<Node3D>().Position = new Vector3(0, 10, 0);
    }

    public override IEntityStateData PackEntityState()
    {
        return new EntityStateMessage
        {
            EntityId = this.EntityId,
            Yaw = 0,
            Position = _rigidBody.Position,
            Rotation = _rigidBody.Rotation,
            Velocity = _rigidBody.LinearVelocity,
            AngularVelocity = _rigidBody.AngularVelocity,
        };
    }
}
