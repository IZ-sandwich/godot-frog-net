using Godot;
using MonkeNet.Serializer;
using MonkeNet.Server;
using MonkeNet.Shared;

namespace YourGame;

// Authoritative server-side player. Receives input from the owning client each tick,
// advances physics, and reports state back for broadcast to all clients.
//
// Scene setup: CharacterBody3D with this script + a SharedPlayerMovement child node
// (or your own movement logic node) wired via the _playerMovement export.
public partial class ServerPlayer : CharacterBody3D, INetworkedEntity, IServerEntity
{
    // Wire to a child node that contains your shared movement logic.
    // Both ServerPlayer and LocalPlayer must use identical physics code so
    // the server and client simulations stay in sync.
    [Export] private Node _playerMovement;

    public int EntityId { get; set; }
    public byte EntityType { get; set; }
    public int Authority { get; set; }

    // Cached camera orientation so it can be included in the snapshot.
    private float _yaw;
    private float _pitch;

    public void OnProcessTick(int tick, IPackableElement genericInput)
    {
        var input = (PlayerInputMessage)genericInput;
        _yaw = input.CameraYaw;
        _pitch = input.CameraPitch;

        // TODO: call your movement logic here, e.g.:
        // _playerMovement.AdvancePhysics(input);
    }

    public IEntityStateData GenerateCurrentStateMessage()
    {
        return new PlayerStateMessage
        {
            EntityId  = EntityId,
            Position  = Position,
            Velocity  = Velocity,
            Yaw       = _yaw,
            Pitch     = _pitch,
        };
    }

    // Emit this signal each tick so GDScript children can react (animations, sounds, etc.)
    // without needing any C# in the visual layer.
    [Signal] public delegate void TickProcessedEventHandler(Vector3 position, Vector3 velocity, float yaw, float pitch);
}
