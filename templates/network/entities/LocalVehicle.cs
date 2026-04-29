using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace YourGame;

// Client-side vehicle owned by the local player. Runs the same physics as
// ServerVehicle each tick (prediction) then reconciles against server state.
//
// Scene setup: identical structure to ServerVehicle — CharacterBody3D root +
// SharedVehiclePhysics child node.
public partial class LocalVehicle : CharacterBody3D, IPredictableEntity
{
    [Export] private float _mispredictionThresholdSquared = 0.01f;

    // Wire to the same physics node used by ServerVehicle.
    [Export] private Node _vehiclePhysics;

    public int EntityId { get; set; }
    public byte EntityType { get; set; }
    public int Authority { get; set; }

    public void OnProcessTick(int tick, int remoteTick, IPackableElement input)
    {
        // TODO: call your vehicle physics node, e.g.:
        // _vehiclePhysics.AdvancePhysics((VehicleInputMessage)input);
    }

    public bool HasMisspredicted(IEntityStateData receivedState, Vector3 savedPosition)
    {
        var state = (PhysicsStateMessage)receivedState;
        return (state.Position - savedPosition).LengthSquared() > _mispredictionThresholdSquared;
    }

    public void HandleReconciliation(IEntityStateData receivedState)
    {
        var state = (PhysicsStateMessage)receivedState;
        GlobalPosition = state.Position;
        GlobalBasis    = new Basis(state.Rotation);
        Velocity       = state.LinearVelocity;
    }

    public void ResimulateTick(IPackableElement input)
    {
        // Must be identical to OnProcessTick — called for every stored tick during rollback.
        // TODO: call your vehicle physics node, e.g.:
        // _vehiclePhysics.AdvancePhysics((VehicleInputMessage)input);
    }

    [Signal] public delegate void TickProcessedEventHandler(Vector3 position, Quaternion rotation, Vector3 velocity);
}
