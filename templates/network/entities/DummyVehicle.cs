using Godot;
using MonkeNet.Client;
using MonkeNet.Shared;

namespace YourGame;

// Remote vehicle entity (observed by non-driving clients). Purely visual —
// no physics body. Position and rotation are smoothly interpolated between
// the two most recent server snapshots.
//
// Scene setup: Node3D root with this script. Add your vehicle mesh as a child.
// Connect the StateInterpolated signal to GDScript nodes for wheel spin,
// exhaust particles, engine sound, etc.
public partial class DummyVehicle : Node3D, INetworkedEntity, IInterpolatedEntity
{
    public int EntityId { get; set; }
    public byte EntityType { get; set; }
    public int Authority { get; set; }

    public void HandleStateInterpolation(IEntityStateData past, IEntityStateData future, float interpolationFactor)
    {
        var p = (PhysicsStateMessage)past;
        var f = (PhysicsStateMessage)future;

        GlobalPosition = p.Position.Lerp(f.Position, interpolationFactor);

        // Slerp quaternions for smooth rotation without gimbal-lock artifacts.
        GlobalBasis = new Basis(p.Rotation.Slerp(f.Rotation, interpolationFactor));

        Vector3 velocity = p.LinearVelocity.Lerp(f.LinearVelocity, interpolationFactor);
        EmitSignal(SignalName.StateInterpolated, GlobalPosition, GlobalBasis.GetRotationQuaternion(), velocity);
    }

    // GDScript children connect here to animate wheels, play engine audio, etc.
    [Signal] public delegate void StateInterpolatedEventHandler(Vector3 position, Quaternion rotation, Vector3 velocity);
}
