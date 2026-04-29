using Godot;
using MonkeNet.Client;
using MonkeNet.Shared;

namespace YourGame;

// Client-side physics prop. Unlike DummyPlayer this is a real RigidBody3D in the
// shared PhysicsSpace, so the local player's CharacterBody3D collides with it
// during client-side prediction. The server's authoritative state is applied each
// snapshot as a correction rather than a hard teleport.
//
// Scene setup: RigidBody3D root with this script + matching CollisionShape3D child.
// The collision shape MUST match the server prop's shape exactly.
//
// Rollback note: when PredictionManager rolls back the player and re-simulates,
// this prop re-simulates from its *current* position (not its tick-T position).
// For a co-op game this is acceptable; perfect rollback would require saving prop
// state history in PredictionManager.
public partial class ClientProp : RigidBody3D, INetworkedEntity, IInterpolatedEntity
{
    // If the server position differs by more than this distance (squared), teleport
    // the body instead of nudging it. Tune to taste.
    [Export] private float _teleportThresholdSquared = 1.0f;

    // How strongly to blend the body's velocity toward the server-corrected position
    // each physics tick. 0 = no correction, 1 = instant snap.
    [Export] private float _correctionStrength = 8.0f;

    public int EntityId { get; set; }
    public byte EntityType { get; set; }
    public int Authority { get; set; }

    private PhysicsStateMessage _pendingCorrection;
    private bool _hasPendingCorrection;
    private bool _serverIsAwake = true;

    // Called by SnapshotInterpolator every render frame with the two nearest snapshots.
    // We store the target state and apply it on the next physics step to avoid
    // conflicting with Jolt mid-step.
    public void HandleStateInterpolation(IEntityStateData past, IEntityStateData future, float interpolationFactor)
    {
        _pendingCorrection = (PhysicsStateMessage)future;
        _hasPendingCorrection = true;
        _serverIsAwake = _pendingCorrection.IsAwake;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (!_hasPendingCorrection) return;
        _hasPendingCorrection = false;

        if (!_serverIsAwake)
        {
            // Server body is sleeping — stop local simulation too.
            Sleeping = true;
            return;
        }

        Sleeping = false;

        float deviationSq = (GlobalPosition - _pendingCorrection.Position).LengthSquared();

        if (deviationSq > _teleportThresholdSquared)
        {
            // Large deviation: hard snap to authoritative state.
            GlobalPosition   = _pendingCorrection.Position;
            GlobalBasis      = new Basis(_pendingCorrection.Rotation);
            LinearVelocity   = _pendingCorrection.LinearVelocity;
            AngularVelocity  = _pendingCorrection.AngularVelocity;
        }
        else
        {
            // Small deviation: nudge velocity toward the correct position so Jolt
            // smoothly converges without visual popping.
            Vector3 positionError = _pendingCorrection.Position - GlobalPosition;
            LinearVelocity  = LinearVelocity.Lerp(_pendingCorrection.LinearVelocity + positionError * _correctionStrength, 0.3f);
            AngularVelocity = AngularVelocity.Lerp(_pendingCorrection.AngularVelocity, 0.3f);
        }
    }

    [Signal] public delegate void CorrectionAppliedEventHandler(Vector3 position);
}
