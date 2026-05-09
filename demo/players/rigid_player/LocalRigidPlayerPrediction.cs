using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace GameDemo;

public partial class LocalRigidPlayerPrediction : ClientPredictedEntity
{
    // 20 cm hard-reconcile threshold (0.04 m²), matching LocalVehiclePrediction.
    [Export] private float _maxDeviationAllowedSquared = 0.04f;
    // Linear-velocity divergence threshold (squared, m²/s²). Catches walking-into-wall
    // mismatches where position holds but momentum is wrong on one side.
    [Export] private float _maxVelocityDeviationSquared = 0.25f;
    // Per-snapshot fraction of accumulated drift to absorb when below the hard reconcile
    // threshold. 0.1 = same as vehicle/ball. Position-only — see LocalVehiclePrediction
    // for why velocity/rotation lerps are deliberately omitted.
    [Export] private float _softCorrectionBlend = 0.1f;
    [Export] private PredictionRigidbody3D _predictionRb;

    // Vertical offset above the vehicle while riding. Mirrors LocalPlayerPrediction and
    // PlayerStateSyncronizer so the predicted and authoritative anchor agree.
    private static readonly Vector3 RideOffset = new(0, 1.5f, 0);

    public override void OnProcessTick(int tick, IPackableElement input)
    {
        if (TryAnchorToOwnedVehicle()) return;
        if (input is CharacterInputMessage cmd)
            RigidPlayerPhysics.AdvancePhysics(_predictionRb, cmd);
    }

    public override Vector3 GetPosition() => _predictionRb.Body.GlobalPosition;

    public override RigidbodyState GetSnapshotState() => _predictionRb.SnapshotState();

    public override Vector3 ExtractAuthoritativePosition(IEntityStateData state) =>
        ((EntityStateMessage)state).Position;

    public override bool HasMisspredicted(int tick, IEntityStateData receivedState, RigidbodyState savedState)
    {
        EntityStateMessage state = (EntityStateMessage)receivedState;
        if ((state.Position - savedState.Position).LengthSquared() > _maxDeviationAllowedSquared)
            return true;
        if ((state.Velocity - savedState.LinearVelocity).LengthSquared() > _maxVelocityDeviationSquared)
            return true;
        return false;
    }

    public override void HandleReconciliation(IEntityStateData receivedState)
    {
        var state = (EntityStateMessage)receivedState;
        MonkeLogger.Debug($"[ENTITY-RECONCILE] LocalRigidPlayer eid={EntityId} auth={state}");
        _predictionRb.Reconcile(new RigidbodyState
        {
            Position = state.Position,
            Rotation = state.Rotation,
            LinearVelocity = state.Velocity,
            AngularVelocity = state.AngularVelocity,
        });
    }

    public override void ResimulateTick(IPackableElement input)
    {
        if (TryAnchorToOwnedVehicle()) return;
        if (input is CharacterInputMessage cmd)
            RigidPlayerPhysics.AdvancePhysics(_predictionRb, cmd);
    }

    // While the local client owns a vehicle, skip player physics and pin the rigid body
    // on top of the vehicle so WASD drives the vehicle alone. Without this, the rigid
    // player and the vehicle both consume the same CharacterInputMessage every tick —
    // the player accelerates forward via RigidPlayerPhysics while the vehicle accelerates
    // forward via VehiclePhysics, the two collide and shove each other around, and
    // because their positions diverge cross-process Jolt nondeterminism reconciles
    // continuously. Visible as the vehicle being unsteerable / unresponsive.
    private bool TryAnchorToOwnedVehicle()
    {
        var body = _predictionRb?.Body;
        if (body == null) return false;
        foreach (var entity in EntitySpawner.Instance.ClientEntities)
        {
            if (entity is LocalVehiclePrediction vehiclePred)
            {
                if (MainScene.AutoRideOnClaim)
                {
                    var vehicleRoot = EntitySpawner.Instance.GetEntityRoot(vehiclePred);
                    if (vehicleRoot == null) return false;
                    body.GlobalPosition = vehicleRoot.GlobalPosition + RideOffset;
                }
                body.LinearVelocity = Vector3.Zero;
                body.AngularVelocity = Vector3.Zero;
                return true;
            }
        }
        return false;
    }

    public override void ApplySoftCorrection(IEntityStateData receivedState, RigidbodyState savedStateAtTick)
    {
        if (_softCorrectionBlend <= 0f) return;
        var state = (EntityStateMessage)receivedState;
        // Visual-only — see LocalBallPrediction/LocalVehiclePrediction for rationale.
        // Mutating body.GlobalPosition would invalidate Jolt contact caches on the player's
        // ground/wall contacts and feed depenetration drift back into the next prediction.
        var smoothing = _predictionRb.Smoothing;
        Vector3 posError = savedStateAtTick.Position - state.Position;
        Vector3 visualShift = -posError * _softCorrectionBlend;
        MonkeLogger.Debug($"[PRED-SOFT] LocalRigidPlayer eid={EntityId} posError=({posError.X:F4},{posError.Y:F4},{posError.Z:F4}) blend={_softCorrectionBlend} visualShift=({visualShift.X:F4},{visualShift.Y:F4},{visualShift.Z:F4}) smootherWired={(smoothing != null)}");
        if (smoothing == null) return;
        smoothing.AddDriftCorrection(visualShift);
    }
}
