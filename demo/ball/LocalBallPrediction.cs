using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace GameDemo;

public partial class LocalBallPrediction : ClientPredictedEntity
{
    // Mirror LocalVehiclePrediction: 20 cm tolerance accommodates Jolt collision-response
    // nondeterminism between client and server (contact normals, friction, persistent-
    // contact caches all diverge by a few cm per impact). 3 cm causes every wall/player/
    // vehicle hit to trigger reconcile, which then desyncs the ball further.
    [Export] private float _maxDeviationAllowedSquared = 0.04f;
    // Linear-velocity divergence threshold (squared, m²/s²). Without this the ball can
    // share the server's position to within tolerance while carrying noticeably different
    // momentum — the wrong velocity then writes new wrong positions every tick until the
    // position threshold trips and a hard snap fires. ~0.5 m/s default.
    [Export] private float _maxVelocityDeviationSquared = 0.25f;
    // Per-snapshot fraction of accumulated drift to absorb when below the hard reconcile
    // threshold. 0 disables soft correction. Position-only — velocity/rotation lerp toward
    // a stale tick-T value pulls the body backward in time and disrupts ongoing dynamics.
    [Export] private float _softCorrectionBlend = 0.1f;
    [Export] private PredictionRigidbody3D _predictionRb;

    public override void OnProcessTick(int tick, IPackableElement input) { }

    public override Vector3 GetPosition()
    {
        return _predictionRb.Body.GlobalPosition;
    }

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
        MonkeLogger.Debug($"[ENTITY-RECONCILE] LocalBall eid={EntityId} auth={state}");
        _predictionRb.Reconcile(new RigidbodyState
        {
            Position = state.Position,
            Rotation = state.Rotation,
            LinearVelocity = state.Velocity,
            AngularVelocity = state.AngularVelocity,
        });
        SyncSleepState(state);
    }

    public override void ResimulateTick(IPackableElement input) { }

    public override void ApplySoftCorrection(IEntityStateData receivedState, RigidbodyState savedStateAtTick)
    {
        var state = (EntityStateMessage)receivedState;
        // Keep the client body's sleep state aligned with the server's. If the
        // server cube has gone to sleep (vel and angvel both ~zero), force the
        // client cube to sleep too — otherwise cross-process Jolt micro-impulses
        // slowly wake it locally and the predicted state then diverges from the
        // sleeping server state every tick of "contact" with neighbouring cubes
        // in a stack, tripping the misprediction threshold every few ticks.
        // Conversely, if the server cube has woken (player just pushed it),
        // wake the client cube so it can simulate the contact response in
        // lockstep with the server.
        SyncSleepState(state);

        if (_softCorrectionBlend <= 0f) return;
        var smoothing = _predictionRb.Smoothing;
        Vector3 posError = savedStateAtTick.Position - state.Position;
        Vector3 visualShift = -posError * _softCorrectionBlend;
        MonkeLogger.Debug($"[PRED-SOFT] LocalBall eid={EntityId} posError=({posError.X:F4},{posError.Y:F4},{posError.Z:F4}) blend={_softCorrectionBlend} visualShift=({visualShift.X:F4},{visualShift.Y:F4},{visualShift.Z:F4}) smootherWired={(smoothing != null)}");
        // Visual-only nudge toward authoritative. The body itself stays untouched so its
        // contact cache (Jolt warm-start manifolds) is preserved across the next SpaceStep.
        // No-op when no smoother is wired — sub-threshold drift then just accumulates until
        // the hard reconcile threshold trips, which the smoother will hide as a snap.
        if (smoothing == null) return;
        smoothing.AddDriftCorrection(visualShift);
    }

    // Server is considered "asleep" if its reported velocity is at floating-point
    // noise levels. The server uses Jolt's default sleep threshold (~0.03 m/s)
    // and broadcasts vel=0 once asleep, so a squared threshold of 1e-4 (= 1e-2 m/s
    // linear) reliably distinguishes "actually sleeping" from "barely moving".
    private const float ServerSleepVelocitySquared = 0.0001f;
    // Client body must also be at near-rest BEFORE we force it to sleep — this
    // prevents the locally-predicted player from being frozen the instant it
    // hits a cube. When the player contacts a cube on the client, Jolt wakes
    // the cube and gives it contact-response velocity; the matching snapshot
    // from the server for that tick (latency ticks behind real time) still
    // reports the cube as sleeping, so without this guard SyncSleepState would
    // re-sleep the cube every snapshot — turning the cube into an infinite-mass
    // wall and stopping the player dead in its tracks. 0.25 m/s linear is well
    // above contact-impulse jitter but well below any "actually moving cube"
    // velocity (cubes pushed by the player accelerate past 1 m/s in a tick).
    private const float ClientNearRestVelocitySquared = 0.0625f;

    private void SyncSleepState(EntityStateMessage state)
    {
        var body = _predictionRb?.Body;
        if (body == null) return;
        bool serverSleeping = state.Velocity.LengthSquared() < ServerSleepVelocitySquared
                              && state.AngularVelocity.LengthSquared() < ServerSleepVelocitySquared;
        bool clientNearRest = body.LinearVelocity.LengthSquared() < ClientNearRestVelocitySquared
                              && body.AngularVelocity.LengthSquared() < ClientNearRestVelocitySquared;
        if (serverSleeping && clientNearRest)
        {
            // Both sides agree the body is essentially at rest, so re-anchor
            // the client body to the server's authoritative pose and zero
            // velocity. This kills the cross-process Jolt micro-drift that
            // otherwise wakes a "stationary" cube on the client even though
            // it's permanently asleep on the server.
            //
            // We do NOT call Body.Sleeping = true. Putting the body in Jolt's
            // sleeping state makes it respond to contact as if it were
            // briefly kinematic until Jolt's solver wakes it on a later tick,
            // which slammed the predicted player to a dead stop the moment it
            // touched a cube. Leaving the body awake-but-at-rest lets Jolt's
            // normal contact resolution run on the first impact (cube absorbs
            // momentum, player keeps some forward velocity), and Jolt's own
            // sleep threshold + timer puts the body back to sleep cleanly
            // between contacts.
            //
            // Route the hard-set through PredictionRigidbody3D.Reconcile so
            // the visual smoother captures the pre-snap visible pose and
            // runs offset-decay over it. Writing body.GlobalTransform
            // directly bypasses the smoother and produces a visible teleport
            // even when a smoothing target is wired.
            _predictionRb.Reconcile(new RigidbodyState
            {
                Position = state.Position,
                Rotation = state.Rotation.Normalized(),
                LinearVelocity = Vector3.Zero,
                AngularVelocity = Vector3.Zero,
            });
        }
    }
}
