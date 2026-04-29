using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace YourGame;

// Client-side player owned by this client. Runs the same physics as ServerPlayer
// each tick (prediction), then reconciles against the server's authoritative state.
//
// Scene setup: same structure as ServerPlayer — CharacterBody3D + movement child node.
public partial class LocalPlayer : CharacterBody3D, IPredictableEntity
{
    [Export] private float _mispredictionThresholdSquared = 0.001f;

    // Wire to the same movement logic node used by ServerPlayer.
    [Export] private Node _playerMovement;

    public int EntityId { get; set; }
    public byte EntityType { get; set; }
    public int Authority { get; set; }

    public void OnProcessTick(int tick, int remoteTick, IPackableElement input)
    {
        // TODO: call your movement logic here, e.g.:
        // _playerMovement.AdvancePhysics((PlayerInputMessage)input);
    }

    public bool HasMisspredicted(IEntityStateData receivedState, Vector3 savedPosition)
    {
        var state = (PlayerStateMessage)receivedState;
        return (state.Position - savedPosition).LengthSquared() > _mispredictionThresholdSquared;
    }

    public void HandleReconciliation(IEntityStateData receivedState)
    {
        var state = (PlayerStateMessage)receivedState;
        Position = state.Position;
        Velocity = state.Velocity;
        // TODO: restore any additional state your movement logic tracks (e.g. is_on_floor cache).
    }

    public void ResimulateTick(IPackableElement input)
    {
        // Called for every stored tick during rollback — must be identical to OnProcessTick.
        // TODO: call your movement logic here, e.g.:
        // _playerMovement.AdvancePhysics((PlayerInputMessage)input);
    }

    [Signal] public delegate void TickProcessedEventHandler(Vector3 position, Vector3 velocity, float yaw, float pitch);
}
