using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace GameDemo;

/// <summary>
/// Client-side prediction wrapper for a server-authoritative rigid prop (ball,
/// cube, or any other RigidBody3D-backed entity). T2 kinematic-interp model:
///
/// Default state is <see cref="PredictionTier.Interpolate"/>, where the body is
/// FROZEN-KINEMATIC and its pose is lerped from the per-entity snapshot history
/// buffer in <c>OnPostPhysicsTick</c>. No local Jolt simulation runs, no
/// per-tick Reconcile writes, no contact-manifold churn. On contact with a
/// player/vehicle the entity is upgraded to <see cref="PredictionTier.Resim"/>
/// — the body unfreezes, seeds its velocity from the current snapshot, and
/// simulates locally for a hysteresis window. On hysteresis expiry it
/// re-freezes; the visual hand-back is masked by
/// <see cref="PredictionVisualSmoothing3D"/> via
/// <c>AbsorbBodyTeleport(prePose, freezeTargetPose)</c>.
///
/// Used by <c>LocalBall.tscn</c>, <c>LocalCube.tscn</c>, and <c>DummyVehicle.tscn</c>.
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
    // 2.25 = (1.5 m/s)². Was 1.0 (1 m/s) under T1's always-blend, where the cube
    // velocity was continuously corrected toward server every snapshot so the
    // local-vs-server velocity gap stayed sub-threshold by construction. T2 lets
    // cubes simulate purely locally during the Resim hysteresis window. The seed
    // velocity at Interpolate→Resim upgrade comes from the latest snapshot, which
    // is N×latency ticks behind server's actual cube velocity — for a cube
    // accelerating at ~20 m/s² under contact response, even a 2-3 tick lag puts
    // the seed ~0.7-1.0 m/s behind server. 1.5 m/s gives enough headroom that the
    // first few Resim ticks (where the seed gap is widest) don't gratuitously
    // trip rollbacks while still catching genuine multi-m/s divergence.
    [Export] private float _maxVelocityDeviationSquared = 2.25f;
    // Rotation divergence threshold (degrees). Catches the case where position and linear
    // velocity stay within bounds but cross-process Jolt drift accumulates yaw / tumble
    // error during a push.
    [Export] private float _maxRotationDeviationDegrees = 5f;
    [Export] private PredictionRigidbody3D _predictionRb;

    // Anti-divergence clamps applied when an Interpolate→Resim upgrade seeds the
    // body's velocity from the latest snapshot. The snapshot velocity has been
    // through quantisation and interpolation; an unbounded re-seed could in
    // principle hand the now-simulated body a runaway speed that would cascade
    // into nearby Resim-tier neighbours on the very first tick. 30 m/s linear
    // and 20 rad/s angular are well above any legitimate prop velocity in the
    // current demos (cubes pushed by a player rarely exceed 8 m/s) while still
    // catching pathological seeds.
    private const float MaxUpgradeVelocity = 30f;
    private const float MaxUpgradeAngularVelocity = 20f;

    // Per-process toggle for the on-body tier indicator (Label3D hovering at
    // the prop's centre). Off by default in gameplay so the UI isn't littered
    // with diagnostic glyphs; the multi-process test harness flips it on via
    // the "set-tier-icons" cmd so recorded videos make tier transitions
    // self-evident — a watcher can see at a glance which props are kinematic-
    // interpolated and which are simulating locally without grepping logs.
    public static bool ShowTierIcons = false;
    private static readonly System.Collections.Generic.List<LocalRigidPropPrediction> _allProps = new();
    public static void RefreshAllIcons()
    {
        var snapshot = _allProps.ToArray();
        foreach (var p in snapshot)
        {
            if (Godot.GodotObject.IsInstanceValid(p)) p.RefreshTierIcon();
        }
    }
    private Label3D _tierIcon;

    public override void _Ready()
    {
        base._Ready();
        // BaseTier and Policy are inspector-authoritative. The shipped
        // LocalCube.tscn and LocalBall.tscn set BaseTier=Interpolate
        // explicitly (saved as `BaseTier = 1` on the ClientPredictedEntity
        // node) so cross-process Jolt drift on idle bodies doesn't trigger
        // full-scene rollback by default. The locally-owned player upgrades
        // any prop it contacts to effective Resim for the duration of the
        // interaction via RequestResimUpgrade — so pushing a cube still
        // produces immediate, crisp response. Override via the scene file
        // or the inspector to opt a specific prop type into Resim/AlwaysPredict.
        //
        // Runtime-only policy override. When EntityEventMessage.Metadata
        // carries one of the recognised markers, replace the inspector-set
        // Policy (and BaseTier when applicable) for this single spawn:
        //
        //   "resim-only"               → BaseTier=Resim,       Policy=AlwaysPredict
        //   "policy=hysteresis"        → BaseTier=Interpolate, Policy=Hysteresis (the default)
        //   "policy=always-predict"    → BaseTier=Resim,       Policy=AlwaysPredict
        //   "policy=authority-transfer"→ BaseTier=Interpolate, Policy=AuthorityTransfer
        //   "policy=blended-velocity"  → BaseTier=Interpolate, Policy=BlendedVelocity
        //
        // Note: BlendedVelocity uses BaseTier=Interpolate (NOT Resim) so the
        // body stays kinematic and snapshot-driven outside contact windows.
        // Resim — and thus locally-simulated dynamic physics — only kicks in
        // when the local player contacts the entity (RequestResimUpgrade
        // arms the hysteresis counter). This matches UE5's
        // PredictiveInterpolation model where the local client interpolates
        // observed entities and only predicts ones it actively touches.
        // Setting BaseTier=Resim previously made observers simulate the
        // entire tower locally while racing a lagged input stream — every
        // contact tick produced an "external-force" mispredict.
        //
        // Used by the misprediction plot tests to spawn the same scenario
        // under each prediction policy without authoring multiple .tscn
        // files. The "resim-only" form is retained as an alias for back-
        // compat with the sleep-coherence tests, which read RigidBody3D.Sleeping
        // on the client (kinematic bodies never report Sleeping=true because
        // Jolt doesn't auto-sleep them). Substring matching means multiple
        // hints can coexist in one metadata blob.
        if (Metadata != null)
        {
            if (Metadata.Contains("policy=always-predict") || Metadata.Contains("resim-only"))
            {
                BaseTier = PredictionTier.Resim;
                Policy = InterpolationPolicy.AlwaysPredict;
            }
            else if (Metadata.Contains("policy=authority-transfer"))
            {
                BaseTier = PredictionTier.Interpolate;
                Policy = InterpolationPolicy.AuthorityTransfer;
            }
            else if (Metadata.Contains("policy=blended-velocity"))
            {
                BaseTier = PredictionTier.Interpolate;
                Policy = InterpolationPolicy.BlendedVelocity;
            }
            else if (Metadata.Contains("policy=hysteresis"))
            {
                BaseTier = PredictionTier.Interpolate;
                Policy = InterpolationPolicy.Hysteresis;
            }
        }
        _allProps.Add(this);
        // Defer kinematic freeze + icon paint to next frame so the wrapped
        // PredictionRigidbody3D._Ready has wired _body + signal handlers
        // before we touch RigidBody3D.Freeze. AddChild-from-_Ready races
        // those handlers when both _Readys land in the same batch.
        CallDeferred(MethodName.OnReadyDeferred);
    }

    private void OnReadyDeferred()
    {
        // Initial Freeze state mirrors BaseTier, NOT Policy. A body is born
        // kinematic-frozen iff its BaseTier is Interpolate; Resim-tier bodies
        // are born dynamic so the local sim can run.
        //
        // Per-policy mapping:
        //   - Hysteresis        (BaseTier=Interpolate) → kinematic; OnEffectiveTierChanged
        //                                                flips Freeze on contact upgrades.
        //   - AlwaysPredict     (BaseTier=Resim)       → dynamic; never freezes (lets
        //                                                Jolt's auto-sleep run, required
        //                                                by sleep-coherence tests).
        //   - AuthorityTransfer (BaseTier=Interpolate) → kinematic until the server flips
        //                                                Authority to the local client;
        //                                                EffectiveTier then reports Resim
        //                                                and OnEffectiveTierChanged thaws.
        //   - BlendedVelocity   (BaseTier=Resim)       → dynamic; the velocity-blend
        //                                                correction needs an actively-
        //                                                simulating body to converge.
        //                                                Previously gated on (Policy != AlwaysPredict),
        //                                                which froze BlendedVelocity bodies
        //                                                kinematic and broke the algorithm —
        //                                                cubes stayed put while server
        //                                                snapshots showed them falling, every
        //                                                snapshot tripped HasMisspredicted,
        //                                                and ApplyBlendedVelocity's
        //                                                LinearVelocity writes had no effect
        //                                                on a frozen body.
        if (BaseTier == PredictionTier.Interpolate) BecomeKinematic();
        RefreshTierIcon();
    }

    public override void _ExitTree()
    {
        _allProps.Remove(this);
        base._ExitTree();
    }

    public void RefreshTierIcon()
    {
        // Smoother mute toggle: AuthorityTransfer-owned cubes are the local
        // truth and are never reconciled, so the smoother's only contribution
        // is FALSE-positive offsets from Jolt's contact-time position
        // corrections (visible as visual lagging body during sustained
        // contact). Mute the smoother while owned; restore on release.
        // Called from here because RefreshTierIcon already runs on every
        // event that could flip ownership state (spawn, AuthorityChanged,
        // tier transitions).
        var smoother = _predictionRb?.GetParent()?.GetNodeOrNull<MonkeNet.Shared.PredictionVisualSmoothing3D>("PredictionVisualSmoothing");
        if (smoother != null)
        {
            bool ownedAtTransfer = Policy == InterpolationPolicy.AuthorityTransfer
                && ClientManager.Instance != null
                && Authority == ClientManager.Instance.GetNetworkId();
            smoother.SetMuted(ownedAtTransfer);
        }

        if (!ShowTierIcons)
        {
            if (_tierIcon != null) _tierIcon.Visible = false;
            return;
        }
        var body = _predictionRb?.Body;
        if (body == null) return;
        if (_tierIcon == null)
        {
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
            // AuthorityTransfer-specific override: when this client currently
            // holds State Authority for the entity (the server flipped Authority
            // to our network id via AuthorityChangedMessage), show "A" instead
            // of "R" — the local sim isn't just a prediction, it IS the truth
            // for this entity until we release. Different glyph so an observer
            // watching the recorded video can tell at a glance which cubes are
            // currently owned by the driver vs. which are being predicted in
            // the regular Resim sense.
            bool isAuthClaim = Policy == InterpolationPolicy.AuthorityTransfer
                && ClientManager.Instance != null
                && Authority == ClientManager.Instance.GetNetworkId();
            if (isAuthClaim)
            {
                _tierIcon.Text = "A";
                _tierIcon.Modulate = new Color(0.95f, 0.85f, 0.25f); // amber — distinct from R (orange) and I (green)
            }
            else if (Policy == InterpolationPolicy.BlendedVelocity)
            {
                // BlendedVelocity: body is locally simulated (Resim tier) but
                // never rolled back / reconciled. Convergence is per-tick
                // velocity blend toward latest snapshot. Distinct glyph so the
                // recorded video reads truthfully — "R" implies rollback-able,
                // which BV bodies aren't.
                _tierIcon.Text = "B";
                _tierIcon.Modulate = new Color(0.4f, 0.7f, 1f); // cyan — distinct from R/A/I
            }
            else
            {
                _tierIcon.Text = "R";
                _tierIcon.Modulate = new Color(1f, 0.55f, 0.2f);
            }
        }
        else
        {
            _tierIcon.Text = "I";
            _tierIcon.Modulate = new Color(0.35f, 1f, 0.45f);
        }
    }

    // Freeze the body into kinematic mode. Jolt then treats it as immovable
    // from the solver's perspective — gravity is suppressed, integration is
    // skipped — but the body still participates in collision and we can write
    // its transform every tick to drive it from the snapshot history. This
    // is the steady-state for an Interpolate-tier prop.
    private void BecomeKinematic()
    {
        var body = _predictionRb?.Body;
        if (body == null) return;
        body.FreezeMode = RigidBody3D.FreezeModeEnum.Kinematic;
        body.Freeze = true;
        body.LinearVelocity = Vector3.Zero;
        body.AngularVelocity = Vector3.Zero;
    }

    // Unfreeze the body so Jolt resumes simulating it. Caller must have
    // already written LinearVelocity/AngularVelocity for the unfreeze tick
    // (see OnEffectiveTierChanged Interpolate→Resim branch).
    private void BecomeSimulated()
    {
        var body = _predictionRb?.Body;
        if (body == null) return;
        body.Freeze = false;
    }

    protected override void OnEffectiveTierChanged(PredictionTier from, PredictionTier to)
    {
        var body = _predictionRb?.Body;
        if (body == null)
        {
            RefreshTierIcon();
            return;
        }

        if (from == PredictionTier.Interpolate && to == PredictionTier.Resim)
        {
            // Upgrade: keep the body's current pose (the just-rendered
            // interpolated pose, where the player just contacted) so
            // collision continuity is preserved across the unfreeze, but
            // seed velocity from the LATEST snapshot rather than the
            // delayed-interpolation one. The contact that triggered this
            // upgrade just happened on the client; the server's
            // counterpart was hit a few ticks ago and its most recent
            // snapshot already reflects the post-hit velocity. Reading
            // the delayed sample would seed from the pre-hit at-rest
            // pose and produce a one-tick velocity gap on the first
            // simulated step.
            var mgr = ClientPredictionManager.Instance;
            if (mgr != null)
            {
                var latest = mgr.TryGetLatestSnapshot(EntityId);
                if (latest != null)
                {
                    Vector3 vel = ExtractAuthoritativeVelocity(latest);
                    Vector3 angVel = ((EntityStateMessage)latest).AngularVelocity;

                    // Clamp velocity seed to anti-divergence ceilings; an
                    // unbounded snapshot vel handed straight into the solver
                    // could cascade into Resim neighbours on the first tick.
                    if (vel.LengthSquared() > MaxUpgradeVelocity * MaxUpgradeVelocity)
                        vel = vel.Normalized() * MaxUpgradeVelocity;
                    if (angVel.LengthSquared() > MaxUpgradeAngularVelocity * MaxUpgradeAngularVelocity)
                        angVel = angVel.Normalized() * MaxUpgradeAngularVelocity;

                    // Position stays at the kinematic interpolated pose —
                    // the player just contacted there, so teleporting the
                    // cube to the latest snapshot pose would either lose
                    // the contact (if server already pushed it forward)
                    // or interpenetrate the player. Only velocity (and
                    // angular vel) are seeded from the freshest snapshot
                    // so the cube continues with the right momentum.
                    body.LinearVelocity = vel;
                    body.AngularVelocity = angVel;
                }
            }
            BecomeSimulated();
            // UE5 PostResimWaitForUpdate equivalent for tier-transitions: after
            // a Resim re-entry, suppress BV correction writes until the next
            // snapshot for this entity arrives. Without this, the per-tick
            // blend immediately fights the seeded velocity using stale snapshot
            // data (the snapshot is N ticks behind the local tick) and pulls
            // the body backward. Set the suppress-until tick to the latest
            // received snapshot tick — incoming snapshots' ticks are monotonic,
            // so the next one will satisfy `latestSnapshotTick > suppressUntil`.
            _bvSuppressUntilSnapshotTick = ClientPredictionManager.Instance?.LastReceivedTick ?? 0;

            // Synchronous cascade: any prop THIS body is currently in contact
            // with (typically the next cube up in a stack) needs to be Resim
            // BEFORE the SpaceStep that's about to run, otherwise the just-
            // unfrozen body tries to push an immovable kinematic neighbour
            // and bounces back instead of transferring momentum up the stack.
            // The recursive RequestResimUpgrade chain propagates through the
            // entire pile in one call frame — for a 6-cube stack that's 6
            // nested calls, all completing before the player's OnProcessTick
            // returns. OnPostPhysicsTick also runs the same query (defence
            // in depth for contacts that only show up AFTER the step).
            var contacts = RigidPlayerPhysics.QueryContactBodies(body);
            foreach (var cb in contacts)
            {
                var cpe = LocalRigidPlayerPrediction.FindOwningPredictedEntity(cb);
                if (cpe != null && cpe != this) cpe.RequestResimUpgrade();
            }
        }
        else if (from == PredictionTier.Resim && to == PredictionTier.Interpolate)
        {
            // Demote: freeze the body at its current sim pose, then arm a
            // brief blend-back window in OnPostPhysicsTick that lerps the
            // body's pose from "current sim pose" toward "snapshot-interp
            // pose" over InterpDelayTicks ticks. WITHOUT this window, the
            // body teleports backwards in time to the InterpDelayTicks-
            // delayed snapshot pose — which is wildly wrong if the cube has
            // been actively pushed during the Resim window. With a 5 m/s
            // pushed cube and 3-tick interp delay, the snap target is 25 cm
            // behind plus any cross-process drift, producing visible
            // "jumping backwards" every time hysteresis expires. The blend
            // window pulls the body forward into the snapshot timeline
            // gradually so the body's render pose stays continuous.
            BecomeKinematic();
            _interpBlendinTicksRemaining = BlendInDurationTicks;
        }
        RefreshTierIcon();
    }

    // T2 blend-in window after Resim → Interpolate. Body is kinematic but its
    // OnPostPhysicsTick writes lerp from current sim-end pose toward the
    // snapshot-interpolation pose over BlendInDurationTicks ticks rather than
    // snapping there. Counter is decremented each kinematic write in
    // OnPostPhysicsTick; once it reaches zero, pure snapshot interpolation
    // takes over.
    private int _interpBlendinTicksRemaining;
    private const int BlendInDurationTicks = 6;

    // Per-tick kinematic write delta above which we treat the write as a
    // teleport and explicitly notify the smoother. Sized to comfortably
    // exceed the largest delta a smooth snapshot-interp lerp produces in
    // normal play (cubes at ~5 m/s × 1/60 s ≈ 8 cm/tick, plus a small
    // safety margin) while still catching the small-but-meaningful jumps
    // a sleep-state sync or a low-magnitude server reposition would make.
    // 0.1 m² = (≈0.32 m)². Below this, the smoother's offset isn't
    // touched and the visual just follows the body — which is correct
    // for normal interp motion.
    private const float KinematicTeleportThresholdSq = 0.1f;

    // Lerp the bracketing snapshot states by alpha, or extrapolate from the
    // single snapshot when alpha indicates a snapshot dropout. Shared between
    // OnEffectiveTierChanged and OnPostPhysicsTick so both paths use exactly
    // the same lerp math.
    private void ResolveLerpedPose(
        IEntityStateData s0,
        IEntityStateData s1,
        float alpha,
        bool extrapolate,
        out Vector3 pos,
        out Quaternion rot,
        out Vector3 vel)
    {
        var m0 = (EntityStateMessage)s0;
        if (s1 == null)
        {
            if (extrapolate)
            {
                // alpha carries the tick distance past the newest snapshot;
                // multiply by physics dt to extrapolate position along velocity.
                float dtExtrap = alpha * (1f / 60f);
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
        var body = _predictionRb?.Body;
        if (body == null) return;

        // UE5 PredictiveInterpolation-style continuous correction. When the
        // entity is locally simulating (Resim tier) under BlendedVelocity
        // policy, apply a small velocity adjustment EVERY tick toward the
        // latest snapshot — instead of one big pulse when HasMisspredicted
        // trips. The per-tick magnitude is scaled by dt/PosInterpolationTime
        // so the velocity blend integrates smoothly with the contact solver
        // rather than fighting it. See ApplyContinuousBlendedVelocity for
        // the full per-tick math + hard-snap fallback.
        if (Policy == InterpolationPolicy.BlendedVelocity
            && EffectiveTier == PredictionTier.Resim)
        {
            ApplyContinuousBlendedVelocity(tick);
        }

        // AuthorityTransfer relay: when this client owns the entity, send the
        // body's current pose+velocity to the server so the server can mirror
        // it on its frozen-kinematic copy (and forward to other clients via the
        // normal snapshot stream). The server stops simulating the body while
        // owned — sees only the relay writes — so its eventual snapshot to
        // the owner matches what the owner already had, eliminating the
        // on-release pose snap. Sent each physics tick (Unreliable; latest-wins).
        if (Policy == InterpolationPolicy.AuthorityTransfer
            && EffectiveTier == PredictionTier.Resim
            && !body.Freeze)
        {
            var clientMgr = MonkeNet.Client.ClientManager.Instance;
            if (clientMgr != null)
            {
                clientMgr.SendCommandToServer(
                    new MonkeNet.NetworkMessages.EntityStateRelayMessage
                    {
                        EntityId = EntityId,
                        Position = body.GlobalPosition,
                        Rotation = body.Quaternion,
                        LinearVelocity = body.LinearVelocity,
                        AngularVelocity = body.AngularVelocity,
                    },
                    MonkeNet.Shared.INetworkManager.PacketModeEnum.Unreliable,
                    (int)MonkeNet.NetworkMessages.ChannelEnum.GameUnreliable);
            }
        }

        if (EffectiveTier == PredictionTier.Interpolate)
        {
            // Kinematic snapshot driver. Read the interpolated pose for the
            // current tick and write it to the body's GlobalTransform. The
            // body is frozen so this is a kinematic pose write — no solver
            // forces, no contact-manifold invalidation beyond what Jolt
            // naturally does for a moving kinematic body.
            var mgr = ClientPredictionManager.Instance;
            if (mgr == null) return;
            if (!mgr.TryGetInterpolatedSnapshot(EntityId, tick, out var s0, out var s1, out float alpha, out bool extrapolate))
                return;
            ResolveLerpedPose(s0, s1, alpha, extrapolate, out var pos, out var rot, out _);

            // Blend-in window after a Resim → Interpolate demote. Lerps the
            // body's current sim-end pose toward the snapshot-interp pose
            // over BlendInDurationTicks (one InterpDelay window). Linear
            // decay: 1/N, 1/(N-1), …, 1/1 — closes the full remaining gap
            // by the final tick, then pure snapshot-driven from there. See
            // OnEffectiveTierChanged for the rationale (avoids backwards-
            // in-time teleport when cubes have been pushed during Resim).
            if (_interpBlendinTicksRemaining > 0)
            {
                float t = 1f / _interpBlendinTicksRemaining;
                pos = body.GlobalPosition.Lerp(pos, t);
                rot = body.Quaternion.Slerp(rot, t);
                _interpBlendinTicksRemaining--;
            }

            // Kinematic-mode teleport detection. The smoother's auto-capture
            // (CaptureUnexplainedJump) is disabled for kinematic bodies because
            // every per-tick interp write would otherwise be flagged as an
            // unexplained jump. But genuine snap-stream teleports (server
            // teleport-entity, or a rollback that mis-routes through this
            // path) still produce a visible single-tick body jump that should
            // be smoothed. Detect them here by comparing the about-to-write
            // pose against the body's current pose; if the delta exceeds the
            // threshold, call AbsorbBodyTeleport explicitly so the visual
            // mesh holds at the pre-jump pose and decays toward the new pose
            // over DecayTime instead of jumping with the body.
            Vector3 currentPos = body.GlobalPosition;
            Quaternion currentRot = body.Quaternion;
            float jumpSq = (pos - currentPos).LengthSquared();
            if (jumpSq > KinematicTeleportThresholdSq)
            {
                var smoother = _predictionRb?.Smoothing;
                if (smoother?.Visual != null)
                    smoother.AbsorbBodyTeleport(currentPos, currentRot, pos, rot);
            }

            body.GlobalTransform = new Transform3D(new Basis(rot.Normalized()), pos);
            // ForceUpdateTransform pushes the new pose into the physics
            // server BEFORE the next SpaceStep so awake Resim bodies that
            // collide with this kinematic prop see the up-to-date pose
            // rather than the previous tick's. Without it, a player/vehicle
            // contact against an Interpolate-tier prop would race against
            // the integration order.
            body.ForceUpdateTransform();
            return;
        }
        // Resim tier: nothing to do here for the blend-in counter (it only
        // applies after a Resim → Interpolate demote). The counter is reset
        // on each new demote in OnEffectiveTierChanged.
        _interpBlendinTicksRemaining = 0;

        // Resim tier: standard local sim is running. Cascade the upgrade to
        // any prop this body is currently contacting AND any prop in its
        // near-future swept path. Without the velocity-aware proactive
        // sweep, the cube-to-cube authority handoff is purely reactive:
        // the owned cube has to physically bump into the unowned cube
        // (which acts as a kinematic wall) before the cascade fires
        // RequestResimUpgrade → RequestAuthority. By the time the server
        // grants authority (~1 RTT later) and the body unfreezes, the
        // player has already felt the wall for ~10 ticks. The proactive
        // sweep mirrors the player's pre-step contact-upgrade pattern
        // (LocalRigidPlayerPrediction line 290 — see QueryNearbyBodies
        // rationale). For AT-policy entities, RequestResimUpgrade triggers
        // RequestAuthority on the edge transition (ClientPredictedEntity
        // line 204), so the auth request goes out 3-5 ticks earlier and
        // authority is in place by the time contact actually happens.
        //
        // Margin tuned to one tick of velocity: at 5 m/s × 1/60 s = 0.083 m,
        // so 0.15 m covers ~2 ticks of lookahead. Larger margins inflate
        // bandwidth (extra spurious requests) without commensurate benefit;
        // smaller misses fast-cube ↔ slow-cube approach events.
        var nearbyBodies = RigidPlayerPhysics.QueryNearbyBodies(body, marginAlongVelocity: 0.15f);
        if (nearbyBodies.Count == 0) return;
        foreach (var cb in nearbyBodies)
        {
            var cpe = LocalRigidPlayerPrediction.FindOwningPredictedEntity(cb);
            if (cpe != null && cpe != this) cpe.RequestResimUpgrade();
        }
    }

    // T2: hold Resim while the body still has meaningful velocity. The
    // hysteresis counter naturally expires 15 ticks after the last
    // RequestResimUpgrade — but for a cube that was launched by the player
    // and is now in mid-flight WITHOUT contact, that's still mid-flight
    // motion the user expects to continue. Demoting now would freeze the
    // body and hand control to a snapshot stream InterpDelay ticks behind
    // current — visually stopping the cube and letting it re-acquire
    // motion only via the delayed snapshots. Holding Resim while
    // |v| > 0.5 m/s keeps the local sim driving the body until it settles
    // for real, at which point ShouldAutoDemote takes over the early-exit.
    protected override bool ShouldHoldResimWhileMoving()
    {
        var body = _predictionRb?.Body;
        if (body == null) return false;
        if (body.Freeze) return false;  // already kinematic
        // 0.25 m²/s² = (0.5 m/s)² — generous so a cube briefly slowed by
        // contact friction doesn't release Resim and re-enter; but well
        // above the at-rest threshold (0.0625) so genuinely settled bodies
        // do release.
        return body.LinearVelocity.LengthSquared() > 0.25f
               || body.AngularVelocity.LengthSquared() > 0.25f;
    }

    // T2: auto-demotion DISABLED. Earlier design auto-demoted when both client
    // and server agreed the body was at rest, to skip the remaining hysteresis
    // ticks on a settled body. That conflicts with the pre-step proximity
    // contact-upgrade path on player / vehicle: an idle cube the player is
    // about to push gets upgraded via RequestResimUpgrade(15), then on the
    // SAME tick TickTierHysteresis calls ShouldAutoDemote (server sleeping,
    // client near rest) and forces the counter back to zero — net result the
    // cube ping-pongs Interp→Resim→Interp inside one tick, NoteTierIfChanged
    // sees no net change (no TIER-SWITCH log), and the body stays kinematic
    // (a wall) for the very SpaceStep that's about to run. The vehicle then
    // bounces off a "still-kinematic" cube despite the upgrade firing.
    //
    // Cost of NOT auto-demoting: a freshly-settled body stays in Resim for
    // 15 ticks of unnecessary local sim before falling back to kinematic. For
    // a sleeping Jolt body this is essentially free (Jolt skips integration
    // on sleeping bodies). Worth the trade for reliable contact-upgrade
    // semantics.
    //
    // Left as virtual override returning false so the infrastructure remains
    // in place if a less-aggressive variant is needed later (e.g. demote only
    // after N ticks of consistent at-rest agreement).
    protected override bool ShouldAutoDemote() => false;

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

    /// <summary>
    /// Legacy per-mispredict pulse correction. **Deprecated** — see
    /// <see cref="ApplyContinuousBlendedVelocity"/> for the per-tick
    /// continuous correction that replaces this. ClientPredictionManager's
    /// HasMisspredicted-gated path is retained only as a fallback for the
    /// hard-snap case (extreme drift past hard-snap threshold).
    ///
    /// This method now no-ops on the per-tick path — the per-tick continuous
    /// correction has already been running. Kept overridden so the base-class
    /// virtual still resolves and the existing manager call site doesn't
    /// crash if BlendedVelocity is selected without the per-tick path firing
    /// (e.g. mid-tier-switch transitions where the entity briefly enters
    /// Resim mid-snapshot-processing).
    /// </summary>
    public override void ApplyBlendedVelocity(IEntityStateData receivedState, float blendAlpha)
    {
        // Per-tick path handles convergence; do nothing here so we don't
        // double-correct on the snapshot-arrival tick.
    }

    // PredictiveInterpolation tuning constants. Derived from the UE5
    // NetworkPhysicsSettingsPredictiveInterpolation parameter set; canonical
    // defaults aren't publicly documented but the docs explicitly tie the
    // time-constants to RTT + send-rate. These values are tuned for a 60 Hz
    // physics + ~20 Hz snapshot setup on a low-latency LAN; future work could
    // make them RTT-scaled like UE5 does (pos_correction_time_base +
    // pos_correction_time_multiplier × RTT).
    //
    // PosCorrectionTime: timescale over which positional error is closed.
    //   Larger value → smaller per-tick correction velocity → smoother but
    //   slower convergence. UE5's per-doc rationale says this should be
    //   "around the round-trip-time" for the smooth path. 0.15 s ≈ 9 ticks
    //   at 60 Hz, well above one snapshot interval (3 ticks at 20 Hz) so the
    //   correction doesn't fight the next snapshot's update.
    // Delta-5 — position-error early-out (UE5 skip_velocity_rep_on_pos_early_out).
    // When |posError| (after half-RTT forward-prediction) is below this threshold,
    // skip the velocity write entirely so natural physics can finish unstable
    // transitions like a cube toppling off an edge. UE5's exact default isn't
    // public; 5 cm is the same magnitude as the body's contact-margin and the
    // server's quantization granularity, so anything smaller is noise.
    private const float PosEarlyOutThreshold = 0.05f;
    private const float PosEarlyOutThresholdSq = PosEarlyOutThreshold * PosEarlyOutThreshold;

    // Deltas-2,3 — UE5 RTT-scaled correction times, independent position/rotation
    // parameters. posCorrTime = max(MinTime + dt, BaseTime + RTT * Mult); same
    // structure for rotation but independent values so rotation correction can
    // be slower (let natural rotation breathe). UE5 docs don't publish defaults;
    // values below are tuned for a low-latency LAN test setup.
    private const float PosCorrectionTimeBase = 0.05f;
    private const float PosCorrectionTimeMin  = 0.05f;
    private const float PosCorrectionTimeMult = 1.5f;
    // Rotation correction (re-enabled with UE5 layers + Godot contact-aware
    // bias). Rotation correction targets server's predicted orientation. The
    // bias gates the correction down (or off) when Jolt reports active
    // contacts — UE5 itself has no contact awareness and recommends switching
    // to Resimulation for genuinely unstable-equilibrium bodies; we take the
    // middle path of "soften correction during contact" which lets a cube
    // tipping on an edge keep tipping (gravity > suppressed correction).
    private const float RotCorrectionTimeBase = 0.30f;
    private const float RotCorrectionTimeMin  = 0.20f;
    private const float RotCorrectionTimeMult = 1.0f;
    // Contact-aware bias for the angular correction. When the body has any
    // active contact, multiply correctionAngVel by this factor (0..1). At
    // 0.15 the correction is ~7x weaker — still bleeds out long-term rotation
    // drift but doesn't overpower gravity-driven torque on a tipping body.
    // UE5 has no equivalent (per the np2.PredictiveInterpolation cvar list);
    // this is a Godot/Jolt-side improvement enabled by Jolt's cheap contact
    // query via RigidPlayerPhysics.QueryContactBodies.
    private const float ContactAngularBias = 0.15f;

    // UE5 PostResimWaitForUpdate equivalent for BV tier transitions. Set in
    // OnEffectiveTierChanged (Interpolate→Resim branch) to the latest received
    // snapshot tick; ApplyContinuousBlendedVelocity skips correction while
    // the manager's LastReceivedTick is <= this value. Cleared automatically
    // once a fresh snapshot arrives. Prevents BV from immediately fighting
    // the seeded velocity using a stale snapshot at the transition moment.
    private int _bvSuppressUntilSnapshotTick = -1;

    // Delta-4 — interpolation-time uses SEND INTERVAL (not RTT), per UE5 docs.
    // ServerManager broadcasts at ~20 Hz, so send interval ≈ 0.05 s. Multiplier
    // controls how many send intervals' worth of smoothing the velocity blend
    // takes to converge to the server's velocity.
    private const float ServerSendIntervalSec = 0.05f; // ServerManager broadcast cadence
    private const float PosInterpolationTimeMult = 1.5f;
    // Rotation interp time independent of position — slower so per-tick
    // angular blend integrates gently over multiple snapshot intervals.
    private const float RotInterpolationTimeMult = 3.0f;

    // Legacy single-constant — kept for the hard-snap fallback math. Unused
    // by the smooth-path now (replaced by RTT-scaled formula above).
    private const float PosCorrectionTime = 0.15f;
    // PosInterpolationTime: timescale of the convex velocity-Lerp toward
    //   server velocity. The per-tick blend alpha is dt/PosInterpolationTime,
    //   so smaller value → faster velocity-blend convergence. 0.10 s ≈ 6 ticks
    //   at 60 Hz, slightly faster than position convergence so the body's
    //   *direction of motion* converges before its *position* does.
    private const float PosInterpolationTime = 0.10f;
    // HardSnapThresholdSq: positional error magnitude squared above which
    //   the continuous correction abandons the smooth path and teleports.
    //   For a 2 m error the smooth-path correction velocity would be 13 m/s,
    //   which the contact solver can't absorb without large counter-impulses.
    //   At that magnitude the visual jump from a hard snap is the lesser
    //   evil. UE5 documents a similar fallback though doesn't publish the
    //   threshold default.
    private const float HardSnapThresholdSq = 4.0f; // (2 m)²
    // Velocity-correction magnitude clamp. Even within the smooth-path
    //   window, very large errors generate correction velocities the contact
    //   solver fights against. Clamping at 5 m/s lets the body converge over
    //   multiple ticks while staying inside the solver's comfortable range.
    private const float MaxCorrectionVelocity = 5.0f;

    /// <summary>
    /// UE5 PredictiveInterpolation-style continuous per-tick correction. Runs
    /// every physics tick while EffectiveTier == Resim AND Policy ==
    /// BlendedVelocity (gated by the caller in OnPostPhysicsTick). Reads the
    /// latest snapshot for this entity from the prediction manager's
    /// history buffer and applies two contributions:
    ///
    ///   (a) Velocity blend: <c>currentVel.Lerp(serverVel, alpha)</c> where
    ///       <c>alpha = dt/PosInterpolationTime</c>. Tilts velocity toward
    ///       server velocity by a small fraction each tick — exponential
    ///       decay with timescale PosInterpolationTime. Replaces the
    ///       previous hard-coded alpha=0.5 which caused the body to
    ///       overshoot.
    ///
    ///   (b) Positional correction velocity:
    ///       <c>(serverPos - currentPos) / PosCorrectionTime</c>. Added on
    ///       top of the blended velocity. Closes the positional gap over
    ///       PosCorrectionTime seconds (exponential decay regime when
    ///       reapplied each tick). Clamped to MaxCorrectionVelocity so the
    ///       contact solver doesn't fight the correction.
    ///
    /// Hard-snap fallback when positional error exceeds HardSnapThresholdSq:
    /// teleports the body. Smoother absorbs the jump as a decaying visual
    /// offset, exactly as it does for HandleReconciliation.
    ///
    /// Crucially, no transform writes occur on the smooth path — only
    /// LinearVelocity/AngularVelocity assignments. Per Jolt's
    /// JoltBody3D::set_linear_velocity (engine source) this does NOT
    /// invalidate persistent contact manifolds. The body stays in its
    /// existing contact pairs and the constraint solver integrates the
    /// velocity adjustment as part of the next SpaceStep without flushing
    /// or rebuilding the manifold pool.
    /// </summary>
    private void ApplyContinuousBlendedVelocity(int tick)
    {
        var manager = MonkeNet.Client.ClientPredictionManager.Instance;
        if (manager == null) return;

        // UE5 PostResimWaitForUpdate-style gate: after a tier transition INTO
        // Resim, wait for a fresh snapshot before resuming correction writes.
        // The snapshot at the transition tick is stale (N ticks behind local
        // tick) and dragging the body toward it would undo the seeded velocity.
        if (manager.LastReceivedTick <= _bvSuppressUntilSnapshotTick) return;
        // Once a fresh snapshot has arrived, clear the gate so subsequent
        // checks are cheap (no per-tick comparison).
        if (_bvSuppressUntilSnapshotTick > 0)
            _bvSuppressUntilSnapshotTick = -1;

        var latest = manager.TryGetLatestSnapshot(EntityId);
        if (latest is not EntityStateMessage state) return;

        var body = _predictionRb?.Body;
        if (body == null || body.Freeze) return; // kinematic body — no velocity correction

        Vector3 currentPos = body.GlobalPosition;
        Vector3 currentLin = body.LinearVelocity;
        Vector3 currentAng = body.AngularVelocity;

        // Delta-1 — forward-predict the snapshot by half the RTT before
        // computing error. The snapshot represents the server's view from
        // ~halfRtt seconds ago; comparing the body's CURRENT pose against
        // raw stale data would drag the body toward where the server WAS,
        // not where it IS. UE5's PredictiveInterpolation does the same:
        //   predictedPos = snap.Position + snap.LinearVelocity * halfRtt
        // RTT comes from ClientNetworkClock.AverageLatencyInTicks; we
        // double it because that field is one-way latency in ticks.
        float halfRttSec = 0f;
        {
            var clock = MonkeNet.Client.ClientManager.Instance?
                .GetNodeOrNull<MonkeNet.Client.ClientNetworkClock>("ClientNetworkClock");
            if (clock != null)
            {
                int oneWayTicks = clock.AverageLatencyInTicks;
                halfRttSec = oneWayTicks * MonkeNet.Shared.PhysicsUtils.DeltaTime;
            }
        }
        Vector3 forwardPredictedPos = state.Position + state.Velocity * halfRttSec;
        Vector3 posError = forwardPredictedPos - currentPos;
        float posErrorSq = posError.LengthSquared();

        // Hard-snap fallback for extreme drift. Above ~2 m, the smooth path
        // would either produce dangerous correction velocities or take
        // multiple seconds to converge — teleport is the practical answer.
        if (posErrorSq > HardSnapThresholdSq)
        {
            MonkeLogger.Debug(
                $"[ENTITY-BLEND-VEL-SNAP] eid={EntityId} tick={tick} |posError|={Mathf.Sqrt(posErrorSq):F3}m " +
                $"> hardSnapThreshold={Mathf.Sqrt(HardSnapThresholdSq):F3}m — teleporting");
            _predictionRb.Reconcile(new MonkeNet.Shared.RigidbodyState
            {
                Position = state.Position,
                Rotation = state.Rotation,
                LinearVelocity = state.Velocity,
                AngularVelocity = state.AngularVelocity,
            });
            return;
        }

        // Delta-5 — early-out when the body is already close enough to the
        // forward-predicted server pose. Skipping the velocity write entirely
        // lets natural physics finish unstable transitions (a cube toppling
        // off its edge, a settling stack rearranging). Without this gate the
        // per-tick blend continuously drags angular velocity toward the
        // (often near-zero) server angular velocity, suppressing natural
        // rotation and producing the "stuck on edge" symptom. Still emit the
        // override log line with zeros so the metric panel shows that
        // we DIDN'T fight physics on this tick.
        if (posErrorSq < PosEarlyOutThresholdSq)
        {
            MonkeLogger.Debug(
                $"[BV-OVERRIDE] eid={EntityId} tick={tick} " +
                $"|posError|={Mathf.Sqrt(posErrorSq):F3}m " +
                $"linOverride=0.000 angOverride=0.000 " +
                $"preAng={currentAng.Length():F3} postAng={currentAng.Length():F3} " +
                $"snapAng={state.AngularVelocity.Length():F3} EARLY-OUT");
            return;
        }

        // Deltas-2 — RTT-scaled position correction time. posCorrTime =
        // max(MinTime + dt, BaseTime + RTT * Mult). RTT scaling means tight
        // corrections at low RTT, gentle ones at high RTT (where fighting
        // stale data would be harmful).
        float dt = MonkeNet.Shared.PhysicsUtils.DeltaTime;
        float rttSec = halfRttSec * 2f;
        float posCorrTime = Mathf.Max(PosCorrectionTimeMin + dt,
                                       PosCorrectionTimeBase + rttSec * PosCorrectionTimeMult);

        // Linear correction velocity from positional error.
        Vector3 correctionVel = posError / posCorrTime;
        float correctionMag = correctionVel.Length();
        if (correctionMag > MaxCorrectionVelocity)
            correctionVel = correctionVel * (MaxCorrectionVelocity / correctionMag);

        // Rotation correction (UE5 axis-angle approach, with contact-aware bias).
        // Forward-predict server rotation by halfRtt (same time-alignment as
        // position), compute shortest-arc quaternion from current to predicted,
        // convert to axis*angle vector, divide by rotCorrTime → target angular
        // velocity that closes the rotation gap over rotCorrTime seconds.
        float rotCorrTime = Mathf.Max(RotCorrectionTimeMin + dt,
                                       RotCorrectionTimeBase + rttSec * RotCorrectionTimeMult);
        Quaternion currentRot = body.Quaternion;
        Quaternion serverRotPredicted = IntegrateRotation(state.Rotation, state.AngularVelocity, halfRttSec);
        Quaternion rotErrorQ = (serverRotPredicted * currentRot.Inverse()).Normalized();
        if (rotErrorQ.W < 0f) // shortest-arc
            rotErrorQ = new Quaternion(-rotErrorQ.X, -rotErrorQ.Y, -rotErrorQ.Z, -rotErrorQ.W);
        float angle = 2f * Mathf.Acos(Mathf.Clamp(rotErrorQ.W, -1f, 1f));
        Vector3 axis = new Vector3(rotErrorQ.X, rotErrorQ.Y, rotErrorQ.Z);
        float axisLen = axis.Length();
        Vector3 correctionAngVel = (axisLen > 1e-6f) ? (axis / axisLen) * (angle / rotCorrTime) : Vector3.Zero;

        // Contact-aware bias: scale down angular correction when the body has
        // any active contact. Lets a tipping cube keep tipping (gravity-driven
        // torque > suppressed correction); long-term rotation drift on
        // free-flying bodies still converges normally. Without this, the
        // correction can hold a cube in unstable equilibrium on its edge for
        // many ticks (observed on eid=4 ~ticks 789-838 of pre-fix BV test).
        // UE5's PredictiveInterpolation has no equivalent; their answer is
        // "switch to Resimulation for such bodies" — we take the Godot
        // middle path of "soften correction during contact".
        var contactBodies = RigidPlayerPhysics.QueryContactBodies(body);
        if (contactBodies.Count > 0)
            correctionAngVel *= ContactAngularBias;

        // Delta-4 — interpolation times scaled by SEND INTERVAL, not RTT.
        // Velocity-blend smoothing tracks packet arrival cadence so we don't
        // over-correct between snapshots. Rotation slower than position so
        // angular blend integrates gently.
        float posInterpTime = ServerSendIntervalSec * PosInterpolationTimeMult;
        float rotInterpTime = ServerSendIntervalSec * RotInterpolationTimeMult;
        float alphaLin = Mathf.Clamp(dt / posInterpTime, 0f, 1f);
        float alphaAng = Mathf.Clamp(dt / rotInterpTime, 0f, 1f);

        // Target velocity = server velocity + corrective velocity. Lerp toward
        // it with separate alphas for lin vs ang.
        Vector3 targetLinV = state.Velocity + correctionVel;
        Vector3 targetAngV = state.AngularVelocity + correctionAngVel;
        Vector3 blendedLin = currentLin.Lerp(targetLinV, alphaLin);
        Vector3 blendedAng = currentAng.Lerp(targetAngV, alphaAng);
        float alpha = alphaLin; // legacy log field

        body.LinearVelocity = blendedLin;
        body.AngularVelocity = blendedAng;

        // BV-interference metric. linOverride = |blendedLin - currentLin| =
        // magnitude of velocity the blend is FORCING onto the body that natural
        // physics did not produce. angOverride = |blendedAng - currentAng| =
        // same for rotation. When the cube is mid-topple but the server's
        // (stale) snapshot says angVel≈0, the per-tick blend drags it toward
        // zero — angOverride captures exactly that suppression. Logged
        // unconditionally so the plotter can render the time-series and we can
        // see the suppression pattern directly.
        float linOverride = (blendedLin - currentLin).Length();
        float angOverride = (blendedAng - currentAng).Length();
        MonkeLogger.Debug(
            $"[BV-OVERRIDE] eid={EntityId} tick={tick} " +
            $"|posError|={Mathf.Sqrt(posErrorSq):F3}m " +
            $"linOverride={linOverride:F3} angOverride={angOverride:F3} " +
            $"preAng={currentAng.Length():F3} postAng={blendedAng.Length():F3} " +
            $"snapAng={state.AngularVelocity.Length():F3}");

        // Spammy at 60 Hz × many entities; gate by error magnitude so the
        // log surfaces only the active-correction moments.
        if (posErrorSq > 0.001f) // > 3.2 cm
        {
            MonkeLogger.Debug(
                $"[ENTITY-BLEND-VEL-CONT] eid={EntityId} tick={tick} alpha={alpha:F3} " +
                $"|posError|={Mathf.Sqrt(posErrorSq):F3}m " +
                $"correctionVel=({correctionVel.X:F3},{correctionVel.Y:F3},{correctionVel.Z:F3}) " +
                $"-> blended=({blendedLin.X:F3},{blendedLin.Y:F3},{blendedLin.Z:F3})");
        }
    }

    // Quaternion that integrates rot forward by angVel*dt radians around angVel's
    // axis. Used by ApplyContinuousBlendedVelocity to forward-predict the
    // server's rotation by halfRtt before computing the rotation error.
    private static Quaternion IntegrateRotation(Quaternion rot, Vector3 angVel, float dt)
    {
        float speed = angVel.Length();
        if (speed < 1e-6f) return rot;
        Vector3 axis = angVel / speed;
        var delta = new Quaternion(axis, speed * dt);
        return (delta * rot).Normalized();
    }

    public override void ApplyAuthoritativeNonPoseState(IEntityStateData receivedState)
    {
        // Sleep-state coherence for Resim-tier props. Interpolate-tier props
        // are kinematic — they have no sleep state in the Jolt sense — so
        // SyncSleepState's SnapToRest path noops cleanly when the body is
        // frozen. Keeping the call here means a Resim → Interpolate transition
        // doesn't change the sleep-sync contract for an entity that flipped
        // tier mid-frame.
        SyncSleepState((EntityStateMessage)receivedState);
    }

    // Client body must be at near-rest BEFORE we force it to sleep — this prevents
    // the locally-predicted player from being frozen the instant it hits a cube.
    private const float ClientNearRestVelocitySquared = 0.0625f;

    private void SyncSleepState(EntityStateMessage state)
    {
        var body = _predictionRb?.Body;
        if (body == null) return;
        // Kinematic body has no sleep state to sync — its pose is driven from
        // the snapshot every tick. Skip the SnapToRest path entirely; the
        // upgrade path will re-seed velocity when the body becomes simulated
        // again.
        if (body.Freeze) return;

        bool clientNearRest = body.LinearVelocity.LengthSquared() < ClientNearRestVelocitySquared
                              && body.AngularVelocity.LengthSquared() < ClientNearRestVelocitySquared;

        if (!state.ServerSleeping || !clientNearRest)
        {
            return;
        }

        _predictionRb.SnapToRest(state.Position, state.Rotation.Normalized());
    }
}
