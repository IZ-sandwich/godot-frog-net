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

    // Track the vehicle we last rode so we can read its LinearVelocity at dismount
    // and inherit it. Mirrors the client-side field in LocalRigidPlayerPrediction.
    private int _lastRiddenVehicleEntityId;

    // Latest AdvancePhysics result from OnProcessTick, consumed by OnServerPostPhysicsTick
    // for the [PHYS-RIGIDPLAYER-POST] diagnostic. Mirrors the client-side fields in
    // LocalRigidPlayerPrediction. The pair is the only way to compute "what Jolt
    // applied during the step" without intrumenting every Jolt contact callback —
    // residual = postVel − (preVel + queuedImpulse + gravity*dt) isolates the
    // contact-resolution impulse.
    private RigidPlayerPhysics.AdvanceResult _lastAdvanceResult;
    private bool _hasLastAdvanceResult;

    private bool _signalSubscribed;
    private MonkeNet.Server.ServerManager _subscribedServer;

    private void EnsurePostPhysicsTickSubscription()
    {
        // Late-binding subscription. ServerManager.Instance may be null when this
        // syncronizer's _Ready runs (test setups that load a syncronizer scene before
        // the ServerManager scene), so subscribing here on the first OnProcessTick
        // sidesteps the ordering question — the syncronizer's OnProcessTick /
        // OnServerPostPhysicsTick are only meaningful once the entity has been spawned
        // into a live server session anyway.
        if (_signalSubscribed) return;
        _subscribedServer = MonkeNet.Server.ServerManager.Instance;
        if (_subscribedServer == null) return;
        _subscribedServer.PostPhysicsTick += OnServerPostPhysicsTick;
        _signalSubscribed = true;
    }

    public override void _ExitTree()
    {
        if (_signalSubscribed && _subscribedServer != null && IsInstanceValid(_subscribedServer))
            _subscribedServer.PostPhysicsTick -= OnServerPostPhysicsTick;
        _signalSubscribed = false;
        _subscribedServer = null;
        base._ExitTree();
    }

    private void OnServerPostPhysicsTick(int serverTick)
    {
        // Dismount-inheritance runs HERE (not in OnProcessTick) so that the vehicle's
        // LinearVelocity read for inheritance reflects this tick's post-step state —
        // the same value the server will ship in the vehicle's snapshot. Reading at
        // OnProcessTick (pre-step) got the previous tick's velocity, which differed
        // from the client's pre-step read by one tick of integration; the asymmetric
        // inheritance produced the chronic ~0.5 m/s post-dismount Y-velocity gap that
        // tripped RIDER-MISPREDICT-TRIP after a vehicle release.
        UpdateRideFreezeState(isPostStep: true);

        // Mirror LocalRigidPlayerPrediction.OnPostPhysicsTick: emit the post-step
        // contact + residual-velocity diagnostic so client and server side-by-side
        // logs can be compared for the same tick. Skipped when the body is anchored
        // or when no AdvancePhysics ran this tick (e.g. server tick with no client
        // input available).
        var body = _predictionRb?.Body;
        if (_hasLastAdvanceResult && body != null && !body.Freeze)
        {
            RigidPlayerPhysics.LogPostPhysics(body, _lastAdvanceResult.PreVel, _lastAdvanceResult.QueuedImpulse, phase: "live");
        }
        _hasLastAdvanceResult = false;
    }

    public override void OnProcessTick(int tick, IPackableElement input)
    {
        // Mirror LocalRigidPlayerPrediction: while this player owns a vehicle, pin the
        // body on top and skip RigidPlayerPhysics. Without this the server's rigid
        // player keeps walking each tick and PushRigidBodies-style collision impulses
        // get routed into the server's vehicle, drifting it away from the client's
        // predicted vehicle every snapshot.
        //
        // Mount-only here: freeze BEFORE this tick's SpaceStep so the body holds the
        // anchor pose. Dismount-with-velocity-inheritance fires from OnServerPostPhysicsTick
        // so we read the vehicle's just-integrated LinearVelocity instead of the
        // previous tick's stale value — see UpdateRideFreezeState for the rationale.
        EnsurePostPhysicsTickSubscription();
        UpdateRideFreezeState(isPostStep: false);
        if (TryAnchorToOwnedVehicle())
        {
            _hasLastAdvanceResult = false;
            return;
        }

        if (input is CharacterInputMessage cmd)
        {
            _lastAdvanceResult = RigidPlayerPhysics.AdvancePhysics(_predictionRb, cmd, phase: "live");
            _hasLastAdvanceResult = true;
        }
        else
        {
            _hasLastAdvanceResult = false;
        }
    }

    private NetworkBehaviour FindRiddenVehicleEntity()
    {
        foreach (var entity in EntitySpawner.Instance.Entities)
        {
            if (entity.EntityType == 2 && entity.Authority == this.Authority)
                return entity;
        }
        return null;
    }

    // Toggle Body.Freeze on ride entry/exit. On dismount, inherit the vehicle's
    // linear velocity so jumping off a moving vehicle preserves momentum on the
    // authoritative side as well — keeps the post-release snapshot consistent
    // with the client's locally-inherited velocity.
    //
    // isPostStep gates which transition fires this call:
    //   - false (called from OnProcessTick): handle MOUNT only. Freezing the body
    //     before this tick's SpaceStep keeps it pinned at the anchor pose for the
    //     integration. The mount-tracking _lastRiddenVehicleEntityId is updated
    //     here too so OnPostPhysicsTick can resolve the vehicle on dismount.
    //   - true (called from OnPostPhysicsTick): handle DISMOUNT only. Reading
    //     vehicle.LinearVelocity here gets this tick's post-step value, which is
    //     the same value packed into the vehicle's outgoing snapshot. The client
    //     mirror also reads at the post-step phase, so the inherited velocity
    //     agrees on both sides and the player doesn't enter free-fall with a
    //     ~0.5 m/s Y-axis gap that compounded over the next several gravity ticks.
    private void UpdateRideFreezeState(bool isPostStep)
    {
        var body = _predictionRb?.Body;
        if (body == null) return;
        var ridden = FindRiddenVehicleEntity();
        bool ridingNow = ridden != null;

        if (!isPostStep && ridingNow && !body.Freeze)
        {
            body.FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic;
            body.Freeze = true;
            _lastRiddenVehicleEntityId = ridden.EntityId;
            return;
        }

        if (isPostStep && !ridingNow && body.Freeze)
        {
            Vector3 inheritedVel = Vector3.Zero;
            if (_lastRiddenVehicleEntityId != 0)
            {
                foreach (var entity in EntitySpawner.Instance.Entities)
                {
                    if (entity.EntityId == _lastRiddenVehicleEntityId)
                    {
                        if (EntitySpawner.Instance.GetEntityRoot(entity) is RigidBody3D vehBody)
                            inheritedVel = vehBody.LinearVelocity;
                        break;
                    }
                }
            }
            body.Freeze = false;
            body.LinearVelocity = inheritedVel;
            body.AngularVelocity = Vector3.Zero;
            _lastRiddenVehicleEntityId = 0;
        }
    }

    private bool TryAnchorToOwnedVehicle()
    {
        var body = _predictionRb?.Body;
        if (body == null) return false;
        var ridden = FindRiddenVehicleEntity();
        if (ridden == null) return false;
        // Always snap the rider on top of the vehicle. The client mirror in
        // LocalRigidPlayerPrediction.TryAnchorToOwnedVehicle does the same so
        // both sides' rider poses stay in lockstep with the vehicle pose, and
        // the snapshot stream doesn't ship a divergent rider trajectory.
        var vRoot = EntitySpawner.Instance.GetEntityRoot(ridden);
        if (vRoot == null) return false;
        AnchorBodyToVehicle(body, vRoot.GlobalPosition + RideOffset);
        return true;
    }

    // Anchor a rigid body to a teleport target using the same atomic-transform-write
    // pattern PredictionRigidbody3D.Reconcile uses. Setting GlobalPosition alone on a
    // RigidBody3D doesn't reliably propagate to Jolt's internal state — the next
    // SpaceStep can integrate from the stale physics-server-side transform and the
    // visible body stays at its pre-teleport pose. ForceUpdateTransform commits the
    // C# transform write through to Jolt.
    //
    // NOTE: we deliberately do NOT call ResetPhysicsInterpolation here. The rider
    // body is anchored EVERY physics tick to (vehicle.GlobalPosition + RideOffset),
    // i.e. it moves smoothly with the vehicle. ResetPhysicsInterpolation tells
    // SceneTreeFTI "this was a teleport, collapse prev=curr, don't lerp" — which
    // is correct on a reconcile snap but wrong on a per-tick smooth-motion anchor.
    // Calling it here pinned the rider body's render pose to the un-interpolated
    // current-tick value, so the first-person camera attached to the rider body
    // rendered the world from a stair-stepped viewpoint while the vehicle mesh
    // (FTI-lerped) slid smoothly between ticks — the rider perceived the vehicle
    // oscillating ~3 cm against the camera every render frame even with
    // debug_collisions disabled. Leaving FTI's normal pump+lerp alone keeps the
    // rider's render pose on the same lerp window as the vehicle.
    internal static void AnchorBodyToVehicle(RigidBody3D body, Vector3 targetPos)
    {
        body.GlobalTransform = new Transform3D(body.GlobalTransform.Basis, targetPos);
        body.LinearVelocity = Vector3.Zero;
        body.AngularVelocity = Vector3.Zero;
        body.ConstantForce = Vector3.Zero;
        body.ConstantTorque = Vector3.Zero;
        body.ForceUpdateTransform();
    }

    public override IEntityStateData PackEntityState()
    {
        var state = _predictionRb.SnapshotState();
        var body = _predictionRb.Body;
        // When the body is anchored to a vehicle (Freeze=Kinematic), AnchorBodyToVehicle
        // rewrites the transform every tick. Jolt then derives a LinearVelocity from
        // (newTransform - oldTransform) / dt internally for the kinematic body so contacts
        // can react — and on the authority-change tick that delta is the teleport from
        // the standalone pose to the anchor pose, producing a velocity in the hundreds
        // of m/s. SnapshotState reads body.LinearVelocity AFTER SpaceStep, so the snapshot
        // ships that bogus derived velocity to clients, tripping a misprediction with
        // |velDiff|=205 m/s the moment the player boards a vehicle. The visible motion is
        // the anchor (driven by GlobalTransform), so we send zero velocity — both the
        // client mirror in LocalRigidPlayerPrediction.GetSnapshotState and observer
        // anchoring keep the rider locked to vehicle pose without needing a velocity hint.
        bool anchored = body != null && body.Freeze
                        && body.FreezeMode == RigidBody3D.FreezeModeEnum.Kinematic;
        return new EntityStateMessage
        {
            EntityId = this.EntityId,
            Yaw = 0,
            Position = state.Position,
            Rotation = state.Rotation,
            Velocity = anchored ? Vector3.Zero : state.LinearVelocity,
            AngularVelocity = anchored ? Vector3.Zero : state.AngularVelocity,
            ServerSleeping = body != null && body.Sleeping,
        };
    }
}
