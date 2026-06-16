using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace GameDemo;

/// <summary>
/// Generic client-side prediction wrapper for a server-authoritative rigid prop
/// (ball, cube, or any other RigidBody3D-backed entity). The body simulates
/// locally every physics tick and is reconciled against the server's snapshot
/// once per tick; sub-threshold drift is absorbed by <see cref="PredictionVisualSmoothing3D"/>,
/// and steady-state rest is anchored via <c>SyncSleepState</c> →
/// <see cref="PredictionRigidbody3D.SnapToRest"/> so persistent contact manifolds
/// don't get invalidated every snapshot.
///
/// Used by <c>LocalBall.tscn</c>, <c>LocalCube.tscn</c>, and <c>DummyVehicle.tscn</c>.
/// Originally lived as <c>LocalBallPrediction</c> in <c>demo/ball/</c>; renamed
/// once it was clear the script applied to any rigid prop, not just balls.
/// </summary>
public partial class LocalRigidPropPrediction : ClientPredictedEntity
{
    // Mirror LocalVehiclePrediction: 20 cm tolerance accommodates Jolt collision-response
    // nondeterminism between client and server (contact normals, friction, persistent-
    // contact caches all diverge by a few cm per impact). 3 cm causes every wall/player/
    // vehicle hit to trigger reconcile, which then desyncs the prop further.
    [Export] private float _maxDeviationAllowedSquared = 0.04f;
    // Linear-velocity divergence threshold (squared, m²/s²). Without this the ball can
    // share the server's position to within tolerance while carrying noticeably different
    // momentum — the wrong velocity then writes new wrong positions every tick until the
    // position threshold trips and a hard snap fires.
    //
    // Threshold = 1.0 m/s (squared = 1.0). When a vehicle pushes a cube, the
    // observer's prediction of the cube lags the server's by one input
    // application — snapshot.Inputs[] only carries the latest per-entity
    // input, not a tick-indexed history, so the observer can't re-apply the
    // exact tick-T input at its own tick T. That structural one-tick lag
    // produces ≈ 0.5–0.7 m/s of contact-tick velocity divergence even when
    // the broader simulation is in lockstep. 1.0 m/s tolerance absorbs that
    // natural lag while still catching gross Jolt-nondeterminism deltas.
    [Export] private float _maxVelocityDeviationSquared = 1.0f;
    // Rotation divergence threshold (degrees). Catches the case where position and linear
    // velocity stay within bounds but cross-process Jolt drift accumulates yaw / tumble
    // error during a push. Without this, rotation only ever gets corrected by SyncSleepState
    // at rest — visible to the player as a flip at end-of-motion (the visual smoother now
    // hides this on the rendered mesh, but the body's contact basis still snaps). 5° is
    // loose enough not to fire on per-tick contact noise (typical drift during a 1-2 s
    // push is ~3° on a knocked-around vehicle, well under the threshold) but tight enough
    // to catch real divergence before it compounds into a player-visible orientation flip.
    [Export] private float _maxRotationDeviationDegrees = 5f;
    [Export] private PredictionRigidbody3D _predictionRb;

    // Per-process toggle for the on-body tier indicator (Label3D hovering at
    // the prop's centre). Off by default in gameplay so the UI isn't littered
    // with diagnostic glyphs; the multi-process test harness flips it on via
    // the "set-tier-icons" cmd so recorded videos make tier transitions
    // self-evident — a watcher can see at a glance which props are blending
    // and which are simulating locally without grepping logs.
    //
    // RefreshAllIcons() walks _allProps to repaint every live prop after the
    // static flips. Without it, props spawned BEFORE the toggle would never
    // pick up the change (event-driven repaint only fires on actual tier
    // transitions; the toggle itself isn't a tier transition).
    public static bool ShowTierIcons = false;
    private static readonly System.Collections.Generic.List<LocalRigidPropPrediction> _allProps = new();
    public static void RefreshAllIcons()
    {
        // Snapshot the list — RefreshTierIcon may create a Label3D as a child of
        // the body, which can theoretically reparent / dispose nodes during a
        // catastrophic teardown. Iterating a copy is cheap (< 100 props in any
        // realistic scene) and avoids "collection modified during enumeration"
        // surprises.
        var snapshot = _allProps.ToArray();
        foreach (var p in snapshot)
        {
            if (Godot.GodotObject.IsInstanceValid(p)) p.RefreshTierIcon();
        }
    }
    private Label3D _tierIcon;

    // Blend state for HandleInterpolateReconciliation. When a snapshot
    // mispredicts in Interpolate tier the body is NOT teleported — instead
    // the snapshot pose/vel becomes a target and OnProcessTick lerps toward
    // it over BlendDurationTicks ticks. After the window expires the body
    // resumes pure local simulation until the next miss. Chosen 3 ticks
    // (50 ms at 60 Hz) so a small cross-process Jolt drift on an idle prop
    // is reabsorbed inside a single snapshot interval — slower than a
    // teleport but visually invisible, and crucially no full-scene resim
    // fires so the player isn't paying the rollback cost for prop drift.
    private const int BlendDurationTicks = 3;
    private bool _blendActive;
    private int _blendTicksRemaining;
    private Vector3 _blendTargetPos;
    private Quaternion _blendTargetRot;
    private Vector3 _blendTargetLinVel;
    private Vector3 _blendTargetAngVel;

    public override void _Ready()
    {
        base._Ready();
        // Props default to Interpolate tier so cross-process Jolt drift on
        // idle bodies doesn't trigger full-scene rollback. The locally-owned
        // player upgrades any prop it contacts to effective Resim for the
        // duration of the interaction via RequestResimUpgrade — so pushing
        // a cube still produces immediate, crisp response.
        BaseTier = PredictionTier.Interpolate;
        _allProps.Add(this);
        // Initial paint. Deferred so the body's PredictionRigidbody3D._Ready
        // (which wires contact-monitor + signal handlers) has finished by the
        // time we AddChild to the body — direct AddChild from within our own
        // _Ready races those handlers when both _Readys land in the same
        // batch, and the Label3D ends up unattached / invisible. The
        // previous OnProcessTick-driven version sidestepped this because the
        // first tick fires AFTER all _Readys complete.
        CallDeferred(MethodName.RefreshTierIcon);
        // KinematicInterpolation steady state is frozen-kinematic — the
        // body's pose is driven by OnPostPhysicsTick reading snapshot
        // history. Other policies leave the body as authored (dynamic).
        CallDeferred(MethodName.MaybeBecomeKinematic);
    }

    /// <summary>Apply the body-freeze state that corresponds to the current
    /// <see cref="ClientPredictedEntity.Policy"/>. Idempotent — calling it
    /// when the body is already in the right state is a no-op. Used by both
    /// the deferred _Ready path and the runtime
    /// <c>set-policy-for-props</c> harness command so a mid-scenario policy
    /// flip takes effect immediately.</summary>
    public void MaybeBecomeKinematic()
    {
        var body = _predictionRb?.Body;
        if (body == null) return;
        if (Policy == InterpolationPolicy.KinematicInterpolation
            && BaseTier == PredictionTier.Interpolate
            && EffectiveTier == PredictionTier.Interpolate)
        {
            BecomeKinematic();
        }
        else if (Policy != InterpolationPolicy.KinematicInterpolation
            && body.Freeze)
        {
            // Policy was flipped AWAY from KinematicInterpolation; release
            // the freeze so the body resumes dynamic simulation.
            BecomeSimulated();
        }
    }

    // Freeze the body into kinematic mode. Jolt treats it as immovable
    // from the solver's perspective — gravity is suppressed, integration is
    // skipped — but the body still participates in collision and we can
    // write its transform every tick to drive it from the snapshot history.
    // This is the KinematicInterpolation steady state.
    private void BecomeKinematic()
    {
        var body = _predictionRb?.Body;
        if (body == null) return;
        body.FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic;
        body.Freeze = true;
        body.LinearVelocity = Vector3.Zero;
        body.AngularVelocity = Vector3.Zero;
    }

    // Unfreeze the body so Jolt resumes simulating it. Caller has already
    // written LinearVelocity/AngularVelocity for the unfreeze tick (see
    // OnEffectiveTierChanged Interpolate→Resim branch below).
    private void BecomeSimulated()
    {
        var body = _predictionRb?.Body;
        if (body == null) return;
        body.Freeze = false;
    }

    public override void _ExitTree()
    {
        _allProps.Remove(this);
        base._ExitTree();
    }

    // Event-driven icon repaint. Called from:
    //   1. _Ready, once, so a prop that spawns into a session with
    //      ShowTierIcons already on picks up the glyph immediately.
    //   2. OnEffectiveTierChanged, when the prop transitions
    //      Interpolate↔Resim. That's the ONLY state-change that affects the
    //      glyph's text/colour, so polling every frame would burn 60 Hz for
    //      no new signal.
    //   3. RefreshAllIcons via the harness "set-tier-icons" cmd, so props
    //      spawned BEFORE the toggle flipped on also pick up the new state.
    //
    // Critical: do NOT call this from OnProcessTick. The Label3D is parented
    // to the body, so per-frame positioning is handled automatically by
    // Godot's scene graph; the glyph's appearance only needs touching on
    // actual transitions.
    public void RefreshTierIcon()
    {
        if (!ShowTierIcons)
        {
            if (_tierIcon != null) _tierIcon.Visible = false;
            return;
        }
        var body = _predictionRb?.Body;
        if (body == null) return;
        if (_tierIcon == null)
        {
            // Label3D parented to the BODY so it follows the body's pose
            // through every reconcile/blend write. Billboard+NoDepthTest so
            // it's legible from any camera angle and never hidden behind
            // geometry (a tower of stacked cubes would otherwise hide the
            // lower glyphs).
            _tierIcon = new Label3D
            {
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                NoDepthTest = true,
                FontSize = 64,
                OutlineSize = 12,
                PixelSize = 0.004f,
                OutlineModulate = new Color(0, 0, 0, 1),
            };
            body.AddChild(_tierIcon);
        }
        _tierIcon.Visible = true;
        if (EffectiveTier == PredictionTier.Resim)
        {
            _tierIcon.Text = "R";
            _tierIcon.Modulate = new Color(1f, 0.55f, 0.2f);
        }
        else
        {
            _tierIcon.Text = "I";
            _tierIcon.Modulate = new Color(0.35f, 1f, 0.45f);
        }
    }

    protected override void OnEffectiveTierChanged(PredictionTier from, PredictionTier to)
    {
        if (Policy == InterpolationPolicy.KinematicInterpolation)
        {
            var body = _predictionRb?.Body;
            if (body != null)
            {
                if (from == PredictionTier.Interpolate && to == PredictionTier.Resim)
                {
                    // Upgrade: seed velocity from the freshest snapshot — the
                    // contact that triggered this upgrade just happened
                    // locally, so the server's counterpart was hit a few ticks
                    // ago and its most recent snapshot already reflects the
                    // post-hit velocity. Reading the delayed (interpolated)
                    // sample would seed from the pre-hit at-rest pose and
                    // produce a one-tick velocity gap on the first sim step.
                    var mgr = MonkeNet.Client.ClientManager.Instance?
                        .GetNodeOrNull<ClientPredictionManager>("PredictionManager");
                    if (mgr != null)
                    {
                        var latest = mgr.TryGetLatestSnapshot(EntityId);
                        if (latest != null)
                        {
                            var em = (EntityStateMessage)latest;
                            // Clamp the seed to anti-divergence ceilings.
                            Vector3 vel = em.Velocity;
                            Vector3 angVel = em.AngularVelocity;
                            const float MaxUpgradeVelocity = 12.0f;
                            const float MaxUpgradeAngularVelocity = 30.0f;
                            if (vel.LengthSquared() > MaxUpgradeVelocity * MaxUpgradeVelocity)
                                vel = vel.Normalized() * MaxUpgradeVelocity;
                            if (angVel.LengthSquared() > MaxUpgradeAngularVelocity * MaxUpgradeAngularVelocity)
                                angVel = angVel.Normalized() * MaxUpgradeAngularVelocity;
                            body.LinearVelocity = vel;
                            body.AngularVelocity = angVel;
                        }
                    }
                    BecomeSimulated();
                }
                else if (from == PredictionTier.Resim && to == PredictionTier.Interpolate)
                {
                    // Demote: freeze the body at its current sim pose; the
                    // OnPostPhysicsTick kinematic driver below blends from
                    // that pose toward the snapshot-interp pose over
                    // BlendInDurationTicks so the body doesn't teleport
                    // backwards in time to the delayed snapshot pose.
                    BecomeKinematic();
                    _interpBlendinTicksRemaining = BlendInDurationTicks;
                }
            }
        }
        RefreshTierIcon();
    }

    // KinematicInterpolation: blend-in window after a Resim → Interpolate
    // demote. Body is kinematic but its OnPostPhysicsTick writes lerp from
    // current sim-end pose toward the snapshot-interp pose over this many
    // ticks rather than snapping there. Linear 1/N decay closes the full
    // remaining gap by the final tick.
    private int _interpBlendinTicksRemaining;
    private const int BlendInDurationTicks = 6;

    // Per-tick kinematic-write delta above which the write is treated as a
    // teleport and the smoother is explicitly told via AbsorbBodyTeleport.
    // 0.1 m² = (~32 cm)² — comfortably exceeds a 5 m/s body's 8 cm/tick
    // smooth motion while catching the meaningful snap-stream jumps.
    private const float KinematicTeleportThresholdSq = 0.1f;

    // Maximum extrapolation distance (ticks) past the newest snapshot. Under
    // bad-network conditions (C3/C4) the snapshot stream can fall 20+ ticks
    // behind the client clock; without a cap the kinematic driver would
    // happily extrapolate a falling cube straight through the floor (the
    // S3-CubeStackPushT2KI.C3 tick-594 incident — body extrapolated 22 ticks
    // along a -4.7 m/s velocity, landing 1.7 m below floor before the next
    // snapshot snapped it back). Capping at 3 ticks = one snapshot period
    // means the visual freezes at the last known pose during long dropouts
    // rather than diverging unboundedly; when the next snapshot finally
    // arrives the snap-overflow PVB blends it smoothly back to the truth.
    private const int MaxExtrapolationTicks = 3;

    private void ResolveLerpedPose(
        IEntityStateData s0, IEntityStateData s1, float alpha, bool extrapolate,
        out Vector3 pos, out Quaternion rot, out Vector3 vel)
    {
        var m0 = (EntityStateMessage)s0;
        if (s1 == null)
        {
            if (extrapolate)
            {
                // Cap the extrapolation window so a stale snapshot stream
                // doesn't drive the kinematic body arbitrarily far along
                // its last-known velocity. Beyond MaxExtrapolationTicks the
                // body just freezes at the projected pose until the next
                // snapshot arrives.
                float clampedAlpha = Mathf.Min(alpha, MaxExtrapolationTicks);
                float dtExtrap = clampedAlpha * (1f / 60f);
                pos = m0.Position + m0.Velocity * dtExtrap;
            }
            else
            {
                pos = m0.Position;
            }
            rot = m0.Rotation;
            vel = m0.Velocity;
            return;
        }
        var m1 = (EntityStateMessage)s1;
        pos = m0.Position.Lerp(m1.Position, alpha);
        rot = m0.Rotation.Slerp(m1.Rotation, alpha);
        vel = m0.Velocity.Lerp(m1.Velocity, alpha);
    }

    public override void OnPostPhysicsTick(int tick, IPackableElement input)
    {
        if (Policy != InterpolationPolicy.KinematicInterpolation) return;
        if (EffectiveTier != PredictionTier.Interpolate) return;
        var body = _predictionRb?.Body;
        if (body == null) return;

        var mgr = MonkeNet.Client.ClientManager.Instance?
                        .GetNodeOrNull<ClientPredictionManager>("PredictionManager");
        if (mgr == null) return;
        if (!mgr.TryGetInterpolatedSnapshot(EntityId, tick, out var s0, out var s1, out float alpha, out bool extrapolate))
            return;
        ResolveLerpedPose(s0, s1, alpha, extrapolate, out var pos, out var rot, out _);

        // Blend-in window after a Resim → Interpolate demote. Linear 1/N
        // decay closes the full remaining gap by the final tick; then pure
        // snapshot-driven from there.
        if (_interpBlendinTicksRemaining > 0)
        {
            float t = 1f / _interpBlendinTicksRemaining;
            pos = body.GlobalPosition.Lerp(pos, t);
            rot = body.Quaternion.Slerp(rot, t);
            _interpBlendinTicksRemaining--;
        }

        // Smoother-aware teleport detection. CaptureUnexplainedJump in the
        // smoother bails out on Freeze=true so it won't accumulate offset
        // from per-tick kinematic writes, but genuine snap-stream teleports
        // still want a smoother handoff so the visual doesn't jump with
        // the body.
        Vector3 currentPos = body.GlobalPosition;
        Quaternion currentRot = body.Quaternion;
        if ((pos - currentPos).LengthSquared() > KinematicTeleportThresholdSq)
        {
            var smoother = _predictionRb?.Smoothing;
            if (smoother?.Visual != null)
                smoother.AbsorbBodyTeleport(currentPos, currentRot, pos, rot);
        }

        body.GlobalTransform = new Transform3D(new Basis(rot.Normalized()), pos);
        // Push the new pose into the physics server BEFORE the next
        // SpaceStep so awake Resim bodies that collide with this kinematic
        // prop see the up-to-date pose.
        body.ForceUpdateTransform();
    }

    public override void OnProcessTick(int tick, IPackableElement input)
    {
        // KinematicInterpolation drives the body via the kinematic-pose
        // driver in OnPostPhysicsTick (reading from snapshot history),
        // NOT via the per-snapshot blend-step Reconcile loop. Running both
        // produces double-writes per tick — the blend-step's Reconcile
        // teleports the kinematic body via PredictionRigidbody3D.Reconcile
        // and then OnPostPhysicsTick overwrites it from the snapshot lerp.
        // Each Reconcile also fires a short PVB on the smoother, which is
        // exactly the AbsorbBodyTeleport-driven freeze-then-sprint pattern
        // KinematicInterpolation was meant to avoid.
        if (Policy == InterpolationPolicy.KinematicInterpolation) return;
        if (!_blendActive) return;

        var body = _predictionRb?.Body;
        if (body == null) { _blendActive = false; return; }

        // Linear-decay blend: 1/N first tick, 1/(N-1) second, … 1/1 final.
        // Each tick closes the same fraction of the REMAINING error, which
        // is geometrically equivalent to an exponential decay sampled at
        // discrete ticks AND lands exactly on the target on the final tick
        // (where t=1) — no residual error to flush at the end.
        float t = 1f / _blendTicksRemaining;
        var cur = _predictionRb.SnapshotState();
        Vector3 newPos = cur.Position.Lerp(_blendTargetPos, t);
        Quaternion newRot = cur.Rotation.Slerp(_blendTargetRot, t);
        Vector3 newLin = cur.LinearVelocity.Lerp(_blendTargetLinVel, t);
        Vector3 newAng = cur.AngularVelocity.Lerp(_blendTargetAngVel, t);
        // useShortPvb=true: each blend tick restarts a 50 ms PVB instead of
        // capturing a position offset that freezes the visual for one render
        // frame per call. Under continuous-push scenarios (S7-C0 eid=14)
        // this kills the 20 Hz saw-tooth pattern that the AbsorbBodyTeleport
        // path produces; the visual slides through the body's motion at the
        // blended velocity instead of catching up in single-frame jumps.
        _predictionRb.Reconcile(new RigidbodyState
        {
            Position = newPos,
            Rotation = newRot,
            LinearVelocity = newLin,
            AngularVelocity = newAng,
        }, useShortPvb: true);

        _blendTicksRemaining--;
        if (_blendTicksRemaining <= 0)
        {
            _blendActive = false;
            MonkeLogger.Debug($"[BLEND-DONE] eid={EntityId}");
        }
    }

    public override void HandleInterpolateReconciliation(IEntityStateData receivedState)
    {
        // KinematicInterpolation policy: the kinematic-pose driver in
        // OnPostPhysicsTick already reads the per-tick interpolated pose
        // from the snapshot-history buffer (which the manager populates on
        // every snapshot receive via CacheSnapshotsFromMessage). Arming
        // the legacy blend here would produce a competing per-tick Reconcile
        // chain that fights the kinematic driver.
        if (Policy == InterpolationPolicy.KinematicInterpolation) return;
        var state = (EntityStateMessage)receivedState;
        // Re-arm the blend with the latest snapshot pose. If a blend is
        // already in progress the new target supersedes the old one and the
        // tick budget resets — the body always converges toward the most
        // recent authoritative snapshot rather than chasing a stale target.
        _blendTargetPos = state.Position;
        _blendTargetRot = state.Rotation;
        _blendTargetLinVel = state.Velocity;
        _blendTargetAngVel = state.AngularVelocity;
        _blendTicksRemaining = BlendDurationTicks;
        _blendActive = true;
        // Throttle the log to meaningful deltas only. Under always-blend
        // semantics this method fires on EVERY snapshot for every Interpolate
        // prop; logging unconditionally produces ~120 lines/sec on a 6-prop
        // scene at 20 Hz snapshot rate. Suppress when the new target is
        // within 5 mm of the body's current pose — those are settled-state
        // blends that converge in one tick to a near-identity Reconcile.
        var body = _predictionRb?.Body;
        if (body != null)
        {
            float targetDeltaSq = (state.Position - body.GlobalPosition).LengthSquared();
            if (targetDeltaSq > BlendLogThresholdSq)
                MonkeLogger.Debug($"[BLEND-START] eid={EntityId} targetPos=({state.Position.X:F3},{state.Position.Y:F3},{state.Position.Z:F3}) Δ={Mathf.Sqrt(targetDeltaSq):F3}m ticks={BlendDurationTicks}");
        }
    }

    // Log-throttle threshold for BLEND-START — (5 mm)² in m².
    private const float BlendLogThresholdSq = 2.5e-5f;

    public override Vector3 GetPosition()
    {
        return _predictionRb.Body.GlobalPosition;
    }

    public override RigidbodyState GetSnapshotState() => _predictionRb.SnapshotState();

    public override Vector3 ExtractAuthoritativePosition(IEntityStateData state) =>
        ((EntityStateMessage)state).Position;

    public override Vector3 ExtractAuthoritativeVelocity(IEntityStateData state) =>
        ((EntityStateMessage)state).Velocity;

    public override Quaternion ExtractAuthoritativeRotation(IEntityStateData state) =>
        ((EntityStateMessage)state).Rotation;

    public override bool HasMisspredicted(int tick, IEntityStateData receivedState, RigidbodyState savedState)
    {
        EntityStateMessage state = (EntityStateMessage)receivedState;
        if ((state.Position - savedState.Position).LengthSquared() > _maxDeviationAllowedSquared)
            return true;
        if ((state.Velocity - savedState.LinearVelocity).LengthSquared() > _maxVelocityDeviationSquared)
            return true;
        if (state.Rotation.AngleTo(savedState.Rotation) > Mathf.DegToRad(_maxRotationDeviationDegrees))
            return true;
        return false;
    }

    public override void HandleReconciliation(IEntityStateData receivedState)
    {
        var state = (EntityStateMessage)receivedState;
        MonkeLogger.Debug($"[ENTITY-RECONCILE] LocalRigidProp eid={EntityId} auth={state}");
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

    public override void RestoreBodyState(RigidbodyState state) => _predictionRb.Reconcile(state);

    public override void ApplyAuthoritativeNonPoseState(IEntityStateData receivedState)
    {
        // Keep the client body's sleep state aligned with the server's even
        // when no full reconcile fires. If the server cube has settled to
        // sleep (vel and angvel both ~zero) and the client's Jolt instance
        // has not yet, the client's body keeps producing tiny per-tick
        // contact micro-impulses that drift it away from the sleeping
        // server state. Without this per-snapshot sync the drift takes
        // many ticks to cross the hard reconcile threshold, by which time
        // sleep state on the two processes can be more than 5 ticks apart
        // — exactly the budget the CubeRest test enforces. Conversely if
        // the server cube has woken (player pushed it), wake the client
        // cube so contact response simulates in lockstep.
        SyncSleepState((EntityStateMessage)receivedState);
    }

    // Client body must be at near-rest BEFORE we force it to sleep — this prevents
    // the locally-predicted player from being frozen the instant it hits a cube.
    // When the player contacts a cube on the client, Jolt wakes the cube and gives
    // it contact-response velocity; the matching snapshot from the server for that
    // tick (latency ticks behind real time) still reports the cube as sleeping, so
    // without this guard SyncSleepState would re-sleep the cube every snapshot —
    // turning the cube into an infinite-mass wall and stopping the player dead in
    // its tracks. 0.25 m/s linear is well above contact-impulse jitter but well
    // below any "actually moving cube" velocity (cubes pushed by the player
    // accelerate past 1 m/s in a tick).
    private const float ClientNearRestVelocitySquared = 0.0625f;

    private void SyncSleepState(EntityStateMessage state)
    {
        var body = _predictionRb?.Body;
        if (body == null) return;

        bool clientNearRest = body.LinearVelocity.LengthSquared() < ClientNearRestVelocitySquared
                              && body.AngularVelocity.LengthSquared() < ClientNearRestVelocitySquared;

        // Server-driven sleep hint replaces velocity inference. RigidBody3D.Sleeping
        // is the authoritative bit Jolt itself toggles when its sleep timer expires;
        // mirroring it directly removes flicker on the wake-up tick (where velocity
        // can briefly be zero on a body that's been impulsed but not yet integrated)
        // and on the going-to-sleep tick (where velocity can still be 0.02 m/s on a
        // body Jolt has already decided is asleep).
        if (!state.ServerSleeping || !clientNearRest)
        {
            // Server is awake, or the client body is moving — leave the body alone
            // and let the normal predict / misprediction path handle the rest.
            return;
        }

        // Surgical re-anchor. SnapToRest does its own sub-mm / sub-0.1° / sub-μm/s
        // idempotency check (see PredictionRigidbody3D.SnapToRest) and only writes
        // the deltas that actually differ. When the body is already sitting on the
        // server pose this is a complete no-op — no transform write, no broadphase
        // invalidation, no manifold churn — so calling it every snapshot while
        // both sides are at rest is essentially free. An external "is the body
        // already at rest here" latch in this caller would just duplicate
        // SnapToRest's own tolerance check and create a separate source of truth
        // for "are we currently anchored" that has to be invalidated whenever the
        // server re-poses an otherwise-sleeping body (teleport, scripted nudge);
        // letting SnapToRest decide every time keeps a single source of truth.
        //
        // We do NOT call Body.Sleeping = true. Putting the body in Jolt's sleeping
        // state makes it respond to contact as if it were briefly kinematic until
        // Jolt's solver wakes it on a later tick, which slammed the predicted player
        // to a dead stop the moment it touched a cube. Leaving the body awake-but-
        // at-rest lets Jolt's normal contact resolution run on the first impact (cube
        // absorbs momentum, player keeps some forward velocity), and Jolt's own sleep
        // threshold + timer puts the body back to sleep cleanly between contacts.
        _predictionRb.SnapToRest(state.Position, state.Rotation.Normalized());
    }
}
