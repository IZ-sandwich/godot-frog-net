using Godot;
using MonkeNet.Serializer;
using MonkeNet.Server;
using MonkeNet.Shared;

namespace YourGame;

// Authoritative server-side physics prop (barrel, crate, ragdoll, etc.).
// Jolt simulates this body in the shared PhysicsSpace each tick; this class
// just reads the result and packages it for broadcast.
//
// Scene setup: RigidBody3D root with this script + CollisionShape3D child.
// OnProcessTick receives no input (props are not player-controlled); the
// input parameter will be null — do not cast it.
public partial class ServerProp : RigidBody3D, INetworkedEntity, IServerEntity
{
    public int EntityId { get; set; }
    public byte EntityType { get; set; }
    public int Authority { get; set; }

    public void OnProcessTick(int tick, IPackableElement input)
    {
        // Physics is stepped automatically by ServerManager via SpaceStep.
        // No input to apply — props are driven by the simulation only.
    }

    public IEntityStateData GenerateCurrentStateMessage()
    {
        return new PhysicsStateMessage
        {
            EntityId        = EntityId,
            Position        = GlobalPosition,
            Rotation        = GlobalBasis.GetRotationQuaternion(),
            LinearVelocity  = LinearVelocity,
            AngularVelocity = AngularVelocity,
            // Stop sending corrections when Jolt has put the body to sleep — saves bandwidth.
            IsAwake         = !PhysicsServer3D.BodyGetDirectState(GetRid()).IsSleeping(),
        };
    }
}
