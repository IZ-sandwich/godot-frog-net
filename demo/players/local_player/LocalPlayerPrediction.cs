using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace GameDemo;

public partial class LocalPlayerPrediction : ClientPredictedEntity
{
    // Mirror LocalVehicle/LocalBall: 20 cm tolerance accommodates Jolt collision-response
    // nondeterminism. The player's own MoveAndSlide is mostly deterministic, but contact
    // with networked rigid bodies (vehicles, balls) feeds Jolt-driven divergence back into
    // the player via PushRigidBodies' reaction impulses. 3 cm caused every push to trigger
    // a reconcile chain.
    [Export] private float _maxDeviationAllowedSquared = 0.04f;
    // Per-snapshot fraction of accumulated drift to absorb when below the hard reconcile
    // threshold. 0 disables. Position-only — see LocalVehiclePrediction for why velocity
    // and rotation lerps are deliberately omitted.
    [Export] private float _softCorrectionBlend = 0.1f;
    [Export] private SharedPlayerMovement _playerMovement;
    [Export] private CharacterBody3D _characterBody;

    // Vertical offset above the vehicle while riding. Matches the server-side anchor in
    // MainScene so prediction and the authoritative state agree, avoiding rollbacks.
    private static readonly Vector3 RideOffset = new(0, 1.5f, 0);

    // Called every physics tick (but synced to network clock)
    public override void OnProcessTick(int tick, IPackableElement input)
    {
        if (TryAnchorToOwnedVehicle()) return;
        _playerMovement.AdvancePhysics((CharacterInputMessage)input);
    }

    // We have misspredicted, return player back to authoritative position
    public override void HandleReconciliation(IEntityStateData receivedState)
    {
        EntityStateMessage state = (EntityStateMessage)receivedState;
        _characterBody.Position = state.Position;
        _characterBody.Velocity = state.Velocity;
    }

    // Check if we have misspredicted
    public override bool HasMisspredicted(int tick, IEntityStateData receivedState, Vector3 savedPosition)
    {
        EntityStateMessage state = (EntityStateMessage)receivedState;
        return (state.Position - savedPosition).LengthSquared() > _maxDeviationAllowedSquared;
    }

    // When the client is re-simulating inputs, what should we do with it? usually the same we do on process tick
    public override void ResimulateTick(IPackableElement input)
    {
        if (TryAnchorToOwnedVehicle()) return;
        _playerMovement.AdvancePhysics((CharacterInputMessage)input);
    }

    public override Vector3 GetPosition()
    {
        return _characterBody.Position;
    }

    public override Vector3 ExtractAuthoritativePosition(IEntityStateData state) =>
        ((EntityStateMessage)state).Position;

    public override void ApplySoftCorrection(IEntityStateData receivedState, Vector3 savedPositionAtTick)
    {
        if (_softCorrectionBlend <= 0f) return;
        // While anchored to a vehicle this is effectively a no-op: TryAnchorToOwnedVehicle
        // re-pins the body to the vehicle each tick, which overwrites any shift here. That
        // is the correct behaviour — when riding, the player's "real" position is the
        // vehicle's, and soft-correcting it would just fight the anchor.
        var state = (EntityStateMessage)receivedState;
        Vector3 posError = savedPositionAtTick - state.Position;
        _characterBody.GlobalPosition -= posError * _softCorrectionBlend;
    }

    // While the local client owns a vehicle, skip MoveAndSlide on the player so WASD
    // drives the vehicle instead. By default also pin the player on top of the vehicle
    // (rider mode); when MainScene.AutoRideOnClaim is false the player keeps its own
    // position — useful for testing pure vehicle control without the player-on-top
    // collision artifact.
    private bool TryAnchorToOwnedVehicle()
    {
        foreach (var entity in EntitySpawner.Instance.ClientEntities)
        {
            if (entity is LocalVehiclePrediction vehiclePred)
            {
                if (MainScene.AutoRideOnClaim)
                {
                    var vehicleRoot = EntitySpawner.Instance.GetEntityRoot(vehiclePred);
                    if (vehicleRoot == null) return false;
                    _characterBody.GlobalPosition = vehicleRoot.GlobalPosition + RideOffset;
                }
                _characterBody.Velocity = Vector3.Zero;
                return true;
            }
        }
        return false;
    }
}
