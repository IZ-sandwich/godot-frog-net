using Godot;
using MonkeNet.Server;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace GameDemo;

public partial class ServerRigidPlayerStateSyncronizer : ServerStateSyncronizer
{
    [Export] private PredictionRigidbody3D _predictionRb;

    // Vertical offset above the vehicle while riding. Mirrors LocalRigidPlayerPrediction
    // and PlayerStateSyncronizer so the authoritative anchor matches the client predicted
    // anchor — divergence here would cost a reconcile every snapshot.
    private static readonly Vector3 RideOffset = new(0, 1.5f, 0);

    public override void OnProcessTick(int tick, IPackableElement input)
    {
        // Mirror LocalRigidPlayerPrediction: while this player owns a vehicle, pin the
        // body on top and skip RigidPlayerPhysics. Without this the server's rigid
        // player keeps walking each tick and PushRigidBodies-style collision impulses
        // get routed into the server's vehicle, drifting it away from the client's
        // predicted vehicle every snapshot.
        if (TryAnchorToOwnedVehicle()) return;

        if (input is CharacterInputMessage cmd)
            RigidPlayerPhysics.AdvancePhysics(_predictionRb, cmd);
    }

    private bool TryAnchorToOwnedVehicle()
    {
        var body = _predictionRb?.Body;
        if (body == null) return false;
        foreach (var entity in EntitySpawner.Instance.Entities)
        {
            if (entity.EntityType == 2 && entity.Authority == this.Authority)
            {
                if (MainScene.AutoRideOnClaim)
                {
                    var vRoot = EntitySpawner.Instance.GetEntityRoot(entity);
                    if (vRoot == null) return false;
                    body.GlobalPosition = vRoot.GlobalPosition + RideOffset;
                }
                body.LinearVelocity = Vector3.Zero;
                body.AngularVelocity = Vector3.Zero;
                return true;
            }
        }
        return false;
    }

    public override IEntityStateData PackEntityState()
    {
        var state = _predictionRb.SnapshotState();
        return new EntityStateMessage
        {
            EntityId = this.EntityId,
            Yaw = 0,
            Position = state.Position,
            Rotation = state.Rotation,
            Velocity = state.LinearVelocity,
            AngularVelocity = state.AngularVelocity,
        };
    }
}
