using Godot;
using MonkeNet.Client;
using MonkeNet.Shared;

namespace YourGame;

// Remote player entity. Has no physics body — it is purely visual, driven by
// interpolation between the two most recent server snapshots.
//
// Scene setup: Node3D root with this script. Add your character mesh, AnimationTree,
// etc. as children and connect them via the exported properties or signals below.
public partial class DummyPlayer : Node3D, INetworkedEntity, IInterpolatedEntity
{
    // Optional: wire an AnimationTree to drive locomotion animations from interpolated velocity.
    [Export] private AnimationTree _animTree;

    public int EntityId { get; set; }
    public byte EntityType { get; set; }
    public int Authority { get; set; }

    public void HandleStateInterpolation(IEntityStateData past, IEntityStateData future, float interpolationFactor)
    {
        var p = (PlayerStateMessage)past;
        var f = (PlayerStateMessage)future;

        Position = p.Position.Lerp(f.Position, interpolationFactor);

        // Lerp yaw via LerpAngle to handle the 0/2π wrap correctly.
        float yaw = Mathf.LerpAngle(p.Yaw, f.Yaw, interpolationFactor);
        Rotation = Vector3.Up * yaw;

        // TODO: interpolate pitch and apply to a head/camera bone if needed.

        Vector3 velocity = p.Velocity.Lerp(f.Velocity, interpolationFactor);
        EmitSignal(SignalName.StateInterpolated, Position, velocity, yaw);

        if (_animTree != null)
            UpdateAnimationTree(velocity);
    }

    // GDScript children can connect to this instead of overriding C# logic.
    [Signal] public delegate void StateInterpolatedEventHandler(Vector3 position, Vector3 velocity, float yaw);

    private void UpdateAnimationTree(Vector3 velocity)
    {
        bool moving = !(velocity * new Vector3(1, 0, 1)).IsZeroApprox();
        _animTree.Set("parameters/conditions/idle", !moving);
        _animTree.Set("parameters/conditions/run", moving);
    }
}
