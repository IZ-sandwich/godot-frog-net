using Godot;
using MonkeNet.Serializer;
using MonkeNet.Server;
using MonkeNet.Shared;

namespace YourGame;

// Authoritative server-side vehicle. The owning client's VehicleInputMessage drives
// physics each tick; the result is broadcast to all clients as PhysicsStateMessage.
//
// Scene setup: CharacterBody3D (or VehicleBody3D) root with this script + a
// SharedVehiclePhysics child node. Both ServerVehicle and LocalVehicle must use
// identical physics code so their simulations stay in sync.
//
// If you use VehicleBody3D, change the base class and update Position/Rotation/
// LinearVelocity/AngularVelocity reads accordingly.
public partial class ServerVehicle : CharacterBody3D, INetworkedEntity, IServerEntity
{
    // Wire to a child node containing shared vehicle physics logic.
    [Export] private Node _vehiclePhysics;

    public int EntityId { get; set; }
    public byte EntityType { get; set; }
    public int Authority { get; set; }

    public void OnProcessTick(int tick, IPackableElement genericInput)
    {
        var input = (VehicleInputMessage)genericInput;

        // TODO: pass input to your vehicle physics node, e.g.:
        // _vehiclePhysics.AdvancePhysics(input);
    }

    public IEntityStateData GenerateCurrentStateMessage()
    {
        return new PhysicsStateMessage
        {
            EntityId        = EntityId,
            Position        = GlobalPosition,
            Rotation        = GlobalBasis.GetRotationQuaternion(),
            LinearVelocity  = Velocity,
            AngularVelocity = Vector3.Zero, // Update if your physics node tracks angular velocity.
            IsAwake         = true,
        };
    }

    [Signal] public delegate void TickProcessedEventHandler(Vector3 position, Quaternion rotation, Vector3 velocity);
}
