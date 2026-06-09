using Godot;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.Client;

/// <summary>
/// Two-tier classification for predicted entities, mirroring UE5's
/// PredictiveInterpolation split. <see cref="Resim"/> entities use the
/// classic full-scene rollback flow on any misprediction (current behaviour).
/// <see cref="Interpolate"/> entities are NOT rollbacked when they
/// mispredict — instead the snapshot pose/vel becomes a blend target that
/// <see cref="ClientPredictedEntity.HandleInterpolateReconciliation"/>
/// applies smoothly over a few ticks, absorbing cross-process Jolt drift
/// on idle props without paying the rollback cost. The locally-owned
/// player upgrades touched entities to effective Resim for a hysteresis
/// window so contact interaction stays crisp.
/// </summary>
public enum PredictionTier
{
    Resim,
    Interpolate,
}

/// <summary>
/// Decides how an Interpolate-tier entity transitions to Resim (and back) over
/// a player interaction. The current 15-tick "tick-counted hysteresis" is one
/// design point; the industry splits across this, snapshot-gated release
/// (UE5 PredictiveInterpolation), authority transfer (Fusion 2 / coherence),
/// and "always predict everything" (Netick Rocket Cars). Isolating the choice
/// as a per-entity policy lets each prop opt into whichever model suits its
/// gameplay role without a global engine change.
/// </summary>
public enum InterpolationPolicy
{
    /// <summary>
    /// Default. Contact upgrades to Resim for a fixed N-tick window (default
    /// 15 = 250 ms at 60 Hz); reverts to <see cref="ClientPredictedEntity.BaseTier"/>
    /// when the counter hits zero. Cheap, no per-body server-ack tracking
    /// needed; the cost is the arbitrary window length (a prop that's been
    /// kicked but is still flying may revert to Interpolate while visibly in
    /// motion).
    /// </summary>
    Hysteresis,
    /// <summary>
    /// No tier flip — <see cref="ClientPredictedEntity.EffectiveTier"/> is
    /// always <see cref="PredictionTier.Resim"/>, regardless of
    /// <see cref="ClientPredictedEntity.BaseTier"/> or contact history. Every
    /// snapshot drift triggers a full rollback. Matches the
    /// "always-predict-everything" pattern (Netick Rocket Cars, lightyear
    /// avian_physics) for bodies that must feel maximally responsive at the
    /// cost of more rollback work.
    /// </summary>
    AlwaysPredict,
    /// <summary>
    /// Photon Fusion 2 style. Default to Interpolate (server-driven kinematic),
    /// but on contact the client REQUESTS state authority via
    /// <see cref="MonkeNet.NetworkMessages.OwnershipChangeRequestMessage"/>.
    /// On server approval the entity's Authority flips to the requesting
    /// client; <see cref="ClientPredictedEntity.EffectiveTier"/> then reports
    /// Resim for the local owner (the local sim IS the truth, not a prediction
    /// against a remote server). On release (hysteresis expiry after contact
    /// loss) the client sends
    /// <see cref="MonkeNet.NetworkMessages.ReleaseAuthorityMessage"/> and the
    /// entity reverts to server authority + Interpolate tier.
    ///
    /// Requires the server-side <c>EntitySpawnConfiguration.OwnershipPolicy</c>
    /// to permit client claims for the entity type, and (optionally) an
    /// <c>OwnershipApprover</c> on the server. Best for props that need crisp
    /// local feel during interaction AND don't conflict with simultaneous
    /// claims from multiple peers — Fusion 2's design assumes only one peer
    /// can hold authority at a time.
    /// </summary>
    AuthorityTransfer,
    /// <summary>
    /// UE5 PredictiveInterpolation style. <see cref="ClientPredictedEntity.EffectiveTier"/>
    /// is always Resim — the client simulates the body locally — but
    /// misprediction is handled by a convex VELOCITY BLEND between the
    /// client's local velocity and the server's authoritative velocity
    /// rather than a teleport-and-rollback. Over a few ticks the body drifts
    /// toward the server pose without ever snapping; position never
    /// discontinuously moves. Trades a little visible "rubber-band" pull
    /// during heavy divergence for the elimination of snap-and-decay visual
    /// artifacts on every reconcile.
    ///
    /// No full-scene rollback runs in this mode — the misprediction count
    /// still increments (so the metric remains comparable across policies),
    /// but the engine cost is per-entity-velocity-blend only, not
    /// SpaceStep × resimTicks. Matches UE5's two-velocity blend described
    /// in PhysicsPredictionSettings and used by default for non-pawn
    /// rigid bodies in shipping UE5 projects.
    /// </summary>
    BlendedVelocity,
    // Future:
    // SnapshotGated   — hold Resim until next in-tolerance server snapshot
    //                   (UE5 post_resim_wait_for_update pattern). Replaces
    //                   the arbitrary tick window with evidence-of-convergence.
}

[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class ClientPredictedEntity : ClientNetworkBehaviour
{
    /// <summary>
    /// Static classification for this entity. Default <see cref="PredictionTier.Resim"/>
    /// preserves the pre-T1 behaviour for every existing entity — only entities that
    /// explicitly opt in (props, ambient bodies) flip to <see cref="PredictionTier.Interpolate"/>.
    /// Subclasses can set this in <c>_Ready</c> to override the default before the
    /// prediction manager reads <see cref="EffectiveTier"/>.
    ///
    /// Ignored when <see cref="Policy"/> is <see cref="InterpolationPolicy.AlwaysPredict"/>
    /// — that policy short-circuits to <see cref="PredictionTier.Resim"/> regardless.
    /// </summary>
    [Export] public PredictionTier BaseTier { get; set; } = PredictionTier.Resim;

    /// <summary>
    /// Selects how <see cref="EffectiveTier"/> transitions on player contact.
    /// See <see cref="InterpolationPolicy"/> for the trade-offs. Default
    /// <see cref="InterpolationPolicy.Hysteresis"/> matches the original T1
    /// behaviour.
    /// </summary>
    [Export] public InterpolationPolicy Policy { get; set; } = InterpolationPolicy.Hysteresis;

    // Hysteresis countdown for contact-driven upgrades to Resim. When > 0, the
    // entity reports Resim regardless of BaseTier; decremented once per physics
    // tick by ClientPredictionManager via TickTierHysteresis() AFTER contact
    // detection has had a chance to refresh it. Critically, the upgrade is
    // applied the SAME tick contact is detected (see RequestResimUpgrade) so the
    // misprediction check that runs after the snapshot arrives uses Resim
    // semantics — without same-tick effect, the contact tick would still see
    // Interpolate semantics and the body would visually lag the player's push.
    //
    // Only meaningful under InterpolationPolicy.Hysteresis. AlwaysPredict
    // ignores this field entirely; its EffectiveTier short-circuits to Resim.
    private int _resimUpgradeTicksRemaining;

    /// <summary>
    /// Tier in effect for the current tick. The active <see cref="Policy"/>
    /// decides how this is computed; read by <see cref="ClientPredictionManager"/>
    /// when routing mispredictions.
    /// </summary>
    public PredictionTier EffectiveTier => Policy switch
    {
        InterpolationPolicy.AlwaysPredict => PredictionTier.Resim,
        // BlendedVelocity uses the SAME contact-driven hysteresis as Hysteresis
        // — Resim while a local contact upgrade is fresh, Interpolate otherwise.
        // The only difference from Hysteresis is what happens DURING the Resim
        // window on misprediction: BlendedVelocity does a soft velocity-blend
        // correction (ApplyBlendedVelocity) while Hysteresis does a full-scene
        // rollback. Without this gating the observer's locally-simulated cube
        // races a 1-tick-lagged input stream during contact and accumulates
        // hundreds of corrections per cube; under Interpolate it tracks the
        // snapshot stream and only flips to Resim when the LOCAL client's
        // player actually touches the cube.
        InterpolationPolicy.BlendedVelocity => _resimUpgradeTicksRemaining > 0 ? PredictionTier.Resim : BaseTier,
        InterpolationPolicy.Hysteresis      => _resimUpgradeTicksRemaining > 0 ? PredictionTier.Resim : BaseTier,
        // AuthorityTransfer flips to Resim when the local client owns the
        // entity (Authority == local network id). Ownership transfer is
        // requested in RequestResimUpgrade and granted asynchronously by
        // the server's AuthorityChangedMessage broadcast — until then the
        // body stays on its BaseTier (typically Interpolate). The hysteresis
        // counter is reused to track "client wants ownership for the next
        // N ticks"; on counter expiry the client sends ReleaseAuthority.
        InterpolationPolicy.AuthorityTransfer =>
            IsLocallyOwned() ? PredictionTier.Resim : BaseTier,
        _ => BaseTier,
    };

    private bool IsLocallyOwned()
    {
        var clientMgr = ClientManager.Instance;
        if (clientMgr == null) return false;
        return Authority == clientMgr.GetNetworkId();
    }

    /// <summary>
    /// Called by the locally-owned player when its body comes into contact with this
    /// entity's body. Takes effect IMMEDIATELY — the very next read of
    /// <see cref="EffectiveTier"/> reports Resim, so the misprediction check that
    /// runs on the snapshot arriving for the contact tick uses Resim semantics
    /// (full rollback) rather than Interpolate (blend). Without same-tick effect
    /// the player would briefly push through a body that's still blending toward
    /// its snapshot pose.
    ///
    /// No-op under <see cref="InterpolationPolicy.AlwaysPredict"/> — that policy
    /// is already Resim and has no hysteresis counter to bump.
    /// </summary>
    /// <param name="ticks">Hysteresis window. Default 15 = 250 ms at 60 Hz, long
    /// enough to absorb a brief contact-loss bounce without flapping back to
    /// Interpolate.</param>
    public void RequestResimUpgrade(int ticks = 15)
    {
        // BlendedVelocity and AuthorityTransfer also use the hysteresis
        // counter, both as a "contact freshness" window. For BlendedVelocity
        // it gates Interpolate→Resim so the body doesn't simulate locally
        // until contact actually happens (see EffectiveTier comment). For
        // AuthorityTransfer it additionally drives the ownership request on
        // the edge transition and the ownership release on counter expiry.
        if (Policy != InterpolationPolicy.Hysteresis &&
            Policy != InterpolationPolicy.AuthorityTransfer &&
            Policy != InterpolationPolicy.BlendedVelocity) return;
        var prev = EffectiveTier;
        bool wasFresh = _resimUpgradeTicksRemaining > 0;
        if (ticks > _resimUpgradeTicksRemaining) _resimUpgradeTicksRemaining = ticks;
        if (Policy == InterpolationPolicy.AuthorityTransfer && !wasFresh && !IsLocallyOwned())
        {
            // Edge transition: contact just started AND we don't already own
            // the entity. Send the request; server's response will flip
            // Authority via AuthorityChangedMessage and the next read of
            // EffectiveTier will report Resim. Local state isn't touched
            // until the server confirms.
            ClientManager.Instance?.RequestAuthority(EntityId);
            MonkeLogger.Debug($"[AUTH-TRANSFER] eid={EntityId} requesting authority on contact");
        }
        var now = EffectiveTier;
        if (now != prev) OnEffectiveTierChanged(prev, now);
    }

    /// <summary>
    /// Decrement the contact-upgrade hysteresis. Called once per physics tick by
    /// <see cref="ClientPredictionManager.Predict"/> AFTER contact detection has
    /// had a chance to refresh the upgrade — so an entity in continuous contact
    /// never drops below 1, but one tick of contact loss decrements toward
    /// expiry.
    ///
    /// No-op under <see cref="InterpolationPolicy.AlwaysPredict"/> — there's
    /// nothing to decrement; the entity is permanently Resim.
    /// </summary>
    /// <summary>
    /// Called by <see cref="MonkeNet.Client.ClientEntityManager"/> AFTER
    /// the Authority field is written from an AuthorityChangedMessage. For
    /// policies whose <see cref="EffectiveTier"/> depends on Authority
    /// (AuthorityTransfer), this is the only signal that the tier may have
    /// flipped without any local-tick state change. Without it, the icon
    /// label (and any other tier-derived UI) would only update on the next
    /// TickTierHysteresis call AFTER the next contact upgrade — by which
    /// point Authority may have been released already.
    ///
    /// Synthesises an EffectiveTier diff using <paramref name="previousTier"/>
    /// (sampled by the caller BEFORE the Authority write) and the now-current
    /// EffectiveTier. Fires <see cref="OnEffectiveTierChanged"/> on a flip,
    /// which subclasses use to repaint the on-body tier glyph and run any
    /// other tier-transition logic (kinematic/dynamic flip, contact cascade, etc.).
    /// </summary>
    public void NotifyAuthorityChanged(PredictionTier previousTier)
    {
        var now = EffectiveTier;
        if (now != previousTier) OnEffectiveTierChanged(previousTier, now);
    }

    public void TickTierHysteresis()
    {
        if (Policy != InterpolationPolicy.Hysteresis &&
            Policy != InterpolationPolicy.AuthorityTransfer &&
            Policy != InterpolationPolicy.BlendedVelocity) return;
        if (_resimUpgradeTicksRemaining <= 0) return;
        var prev = EffectiveTier;
        // T2 hold-while-moving: if the body still has meaningful velocity,
        // skip the decrement. Demoting a mid-flight body hands its motion
        // over to the snapshot interpolation stream — which is InterpDelay
        // ticks BEHIND real time, so the body's current trajectory is ahead
        // of the snap target and the body visually stops / lurches backwards
        // to land on the delayed pose. Holding Resim until the body actually
        // settles keeps the visible motion continuous; only when the body is
        // both client-at-rest AND the snapshot stream shows it at rest does
        // the demote fire (via ShouldAutoDemote).
        if (ShouldHoldResimWhileMoving())
        {
            // Don't decrement and don't fire transitions — counter stays at
            // its current value. Next tick we re-evaluate.
            return;
        }
        // T2 auto-demote: fast-forward to Interpolate when the entity's body
        // has settled BEFORE the full hysteresis window has elapsed. A still
        // body sitting through 15 ticks of unnecessary local sim costs more
        // than just letting it go back to the cheap kinematic-interp path
        // early. Default ShouldAutoDemote is false — only entities that opt
        // in (typically rigid props) demote early.
        if (ShouldAutoDemote()) _resimUpgradeTicksRemaining = 0;
        else _resimUpgradeTicksRemaining--;
        var now = EffectiveTier;
        if (now != prev) OnEffectiveTierChanged(prev, now);
        // AuthorityTransfer: when the hysteresis window just expired AND we
        // currently own this entity, ask the server to take authority back.
        // The body returns to BaseTier (typically Interpolate) once the
        // server's AuthorityChangedMessage propagates. Without the release
        // the client would hold ownership indefinitely — defeating the
        // "interpolate by default" contract on the next idle-cube test.
        if (Policy == InterpolationPolicy.AuthorityTransfer
            && _resimUpgradeTicksRemaining == 0
            && IsLocallyOwned())
        {
            ClientManager.Instance?.ReleaseAuthority(EntityId);
            MonkeLogger.Debug($"[AUTH-TRANSFER] eid={EntityId} releasing authority (contact window expired)");
        }
    }

    /// <summary>
    /// Per-tick predicate evaluated by <see cref="TickTierHysteresis"/>. Return
    /// true while the body has enough velocity that demoting now would visibly
    /// stop / lurch the motion. Default false preserves the count-down for
    /// entities that don't opt in. Rigid props override this with a velocity
    /// threshold.
    /// </summary>
    protected virtual bool ShouldHoldResimWhileMoving() { return false; }

    /// <summary>
    /// Per-tick predicate evaluated by <see cref="TickTierHysteresis"/>. Return
    /// true when both client and server agree the entity has settled and the
    /// remaining hysteresis ticks should be skipped. Default false preserves
    /// the original tick-counted behaviour for entities that don't opt in.
    /// Override on rigid props to query the wrapped body's velocity.
    /// </summary>
    protected virtual bool ShouldAutoDemote() { return false; }

    /// <summary>
    /// Fires exactly once each time <see cref="EffectiveTier"/> flips. Default
    /// no-op; subclasses override to react — e.g. <c>LocalRigidPropPrediction</c>
    /// uses this to repaint its on-body tier indicator on tier change rather
    /// than polling every frame. Called from <see cref="RequestResimUpgrade"/>
    /// and <see cref="TickTierHysteresis"/>, so any UI side-effect runs once
    /// per real transition (Interpolate→Resim on contact, Resim→Interpolate on
    /// hysteresis expiry).
    /// </summary>
    protected virtual void OnEffectiveTierChanged(PredictionTier from, PredictionTier to) { }

    /// <summary>Cumulative count of EffectiveTier transitions for this entity.
    /// Surfaced via the multi-client harness "tier-state" cmd for tests that
    /// assert the contact-upgrade + hysteresis behaviour.</summary>
    public int TierSwitchCount { get; private set; }

    /// <summary>Tick number of the most recent EffectiveTier transition. Used by
    /// the contact-upgrade test to verify the upgrade happens within one tick
    /// of the player touching the entity.</summary>
    public int LastTierSwitchTick { get; private set; }

    // Last EffectiveTier reported to NoteTierIfChanged. Held by the manager
    // rather than the entity itself because the entity has no concept of "tick"
    // — the manager passes the current tick when it polls. Kept as a separate
    // ref-parameter dictionary entry on the manager side; on the entity we just
    // expose the bump method below.
    internal void NoteTierIfChanged(int tick, ref PredictionTier prevReported)
    {
        var now = EffectiveTier;
        if (now != prevReported)
        {
            TierSwitchCount++;
            LastTierSwitchTick = tick;
            MonkeLogger.Debug($"[TIER-SWITCH] tick={tick} eid={EntityId} {prevReported}->{now} (count={TierSwitchCount})");
            prevReported = now;
        }
    }

    public virtual void OnProcessTick(int tick, IPackableElement input) { }

    /// <summary>
    /// Per-snapshot hook for syncing AUXILIARY state (sleep flags, custom
    /// per-entity coherence data) from the authoritative snapshot when the
    /// body pose did NOT exceed the hard reconcile threshold. Does NOT
    /// touch the body's transform — that path is the
    /// <see cref="HandleReconciliation"/>/rollback flow. Fires every
    /// snapshot regardless of misprediction outcome (when below threshold),
    /// so cumulative-drift state (sleep coherence across stacked rigid
    /// bodies, for example) stays in lockstep without forcing a full
    /// pose snap. Default no-op.
    /// </summary>
    public virtual void ApplyAuthoritativeNonPoseState(IEntityStateData receivedState) { }

    /// <summary>
    /// Called after the per-tick SpaceStep completes, before
    /// <see cref="ClientPredictionManager"/> records the post-step
    /// snapshot. Use for state that must read this tick's post-step body pose of
    /// OTHER entities — e.g. anchoring a kinematic rider to the just-integrated
    /// vehicle pose. <see cref="OnProcessTick"/> runs BEFORE SpaceStep so any
    /// position it reads from a peer body is the previous tick's result; reading
    /// here gets the current tick's result.
    /// </summary>
    public virtual void OnPostPhysicsTick(int tick, IPackableElement input) { }

    public virtual bool HasMisspredicted(int tick, IEntityStateData receivedState, RigidbodyState savedState) { return false; }
    public virtual void HandleReconciliation(IEntityStateData receivedState) { }
    public virtual void ResimulateTick(IPackableElement input) { }

    /// <summary>
    /// UE5-style velocity blend used when <see cref="Policy"/> is
    /// <see cref="InterpolationPolicy.BlendedVelocity"/>. Instead of teleporting
    /// the body to the authoritative pose on misprediction, the prediction
    /// manager calls this method so the entity can write a convex blend of its
    /// CURRENT velocity with the SERVER'S velocity into the underlying body.
    /// Over the next several ticks the body's pose drifts toward server pose
    /// without ever discontinuously snapping — the cost of a single mispredict
    /// is one velocity write, not a SpaceStep × resimTicks rollback.
    ///
    /// <paramref name="blendAlpha"/> is the convex weight of the SERVER's
    /// velocity: 0 = keep local, 1 = full server velocity. Typical values
    /// 0.3–0.6 — high enough to converge in a few ticks, low enough to keep
    /// local input feel. Default no-op; override on each entity type that
    /// supports velocity blending (typically anything with a wrapped
    /// PredictionRigidbody3D).
    /// </summary>
    public virtual void ApplyBlendedVelocity(IEntityStateData receivedState, float blendAlpha) { }

    /// <summary>
    /// Deprecated in T2. Was called by the prediction manager when an
    /// Interpolate-tier entity mispredicted; now Interpolate-tier bodies are
    /// kinematic and driven directly from the snapshot-history buffer, so the
    /// manager no longer routes mispredict events through this method. Kept
    /// virtual so existing entity subclasses that override it still compile,
    /// but the default body simply delegates to <see cref="HandleReconciliation"/>
    /// for any caller that still invokes it directly.
    /// </summary>
    public virtual void HandleInterpolateReconciliation(IEntityStateData receivedState)
    {
        HandleReconciliation(receivedState);
    }

    /// <summary>
    /// Position of the entity at the moment of registration. Used by the prediction
    /// manager only as a quick accessor for diagnostic logging.
    /// </summary>
    public virtual Vector3 GetPosition() { return Vector3.Zero; }

    /// <summary>
    /// Snapshot of the entity's full simulation state at the current tick. Stored by
    /// the prediction manager for each registered tick and passed back to
    /// <see cref="HasMisspredicted"/> when a server snapshot for that tick arrives.
    /// Override on each predicted entity that wants velocity-aware misprediction
    /// detection — default is zeroed state.
    /// </summary>
    public virtual RigidbodyState GetSnapshotState() { return default; }

    /// <summary>
    /// Extracts the authoritative position from an <see cref="IEntityStateData"/>.
    /// The framework uses this only for diagnostic logging — concrete entity types
    /// know which message struct they use and can cast to it. Default returns
    /// <see cref="Vector3.Zero"/>; override on each predicted entity that wants
    /// useful misprediction logs.
    /// </summary>
    public virtual Vector3 ExtractAuthoritativePosition(IEntityStateData state) { return Vector3.Zero; }

    /// <summary>
    /// Extracts the authoritative linear velocity from an <see cref="IEntityStateData"/>.
    /// Used by the misprediction classifier to distinguish accumulated float drift
    /// (predicted velocity matches authoritative) from a genuine external force
    /// (velocity diverged because the server applied an impulse the client didn't
    /// replay). Default returns <see cref="Vector3.Zero"/>; override on each
    /// predicted entity that carries velocity in its state message.
    /// </summary>
    public virtual Vector3 ExtractAuthoritativeVelocity(IEntityStateData state) { return Vector3.Zero; }

    /// <summary>
    /// Extracts the authoritative rotation from an <see cref="IEntityStateData"/>.
    /// Used by the misprediction logger so a reconcile triggered by the rotation
    /// threshold (rather than position or velocity) shows which threshold actually
    /// fired — without this the info line would report a sub-threshold posDiff and
    /// sub-threshold velDiff and the reader has to guess that rotation tripped.
    /// Default returns <see cref="Quaternion.Identity"/>; override on each
    /// predicted entity that carries rotation in its state message.
    /// </summary>
    public virtual Quaternion ExtractAuthoritativeRotation(IEntityStateData state) { return Quaternion.Identity; }

    /// <summary>
    /// Names which of the documented reconcile thresholds tripped for this
    /// (authoritative, predicted) pair — "position", "velocity", "rotation",
    /// combinations thereof, or "below-thresholds" if none did. The
    /// misprediction logger calls this so its <c>trippedBy=…</c> tag uses the
    /// entity's own thresholds rather than a hardcoded set, which is the
    /// difference between "the player tripped at its tighter 0.5 m/s
    /// threshold but the prop's 1.0 m/s threshold says below" (silent
    /// misclassification) and "trippedBy=velocity" (correct).
    ///
    /// Default checks against common values (pos² &lt; 0.04, vel² &lt; 1.0,
    /// rot &lt; 5°). Entities with custom thresholds — particularly the
    /// rigid-body player whose velocity threshold is much tighter than the
    /// passive-prop threshold — override this to query their own fields.
    /// </summary>
    public virtual string DescribeMispredictTrigger(IEntityStateData authoritativeState, RigidbodyState savedState)
    {
        Vector3 authPos = ExtractAuthoritativePosition(authoritativeState);
        Vector3 authVel = ExtractAuthoritativeVelocity(authoritativeState);
        Quaternion authRot = ExtractAuthoritativeRotation(authoritativeState);
        bool posOver = (authPos - savedState.Position).LengthSquared() > 0.04f;          // 0.2 m
        bool velOver = (authVel - savedState.LinearVelocity).LengthSquared() > 1.0f;      // 1.0 m/s
        bool rotOver = authRot.AngleTo(savedState.Rotation) > Mathf.DegToRad(5f);         // 5°
        return MispredictTriggerString.Format(posOver, velOver, rotOver);
    }

    /// <summary>
    /// Restores the body's transform + velocities to a previously captured snapshot
    /// state, without any of the authority-reconcile side effects
    /// (<see cref="HandleReconciliation"/> may call SyncSleepState, zero residual
    /// forces, etc.). Called by the prediction manager's spawn-tick-alignment path
    /// to put non-newly-spawned entities back at their pre-resim pose after the
    /// catch-up resim's <c>SpaceStep</c> calls have over-stepped every body in the
    /// physics space. Default no-op — override on entities that wrap a
    /// <see cref="PredictionRigidbody3D"/>.
    /// </summary>
    public virtual void RestoreBodyState(RigidbodyState state) { }
}

/// <summary>
/// Small helper used by <see cref="ClientPredictedEntity.DescribeMispredictTrigger"/>
/// (and its overrides) to format a consistent string across all entities. Kept
/// here rather than in the prediction manager so entity-specific overrides can
/// share the same labels without depending on the manager.
/// </summary>
public static class MispredictTriggerString
{
    public static string Format(bool posOver, bool velOver, bool rotOver)
    {
        if (posOver && !velOver && !rotOver) return "position";
        if (!posOver && velOver && !rotOver) return "velocity";
        if (!posOver && !velOver && rotOver) return "rotation";
        if (posOver && velOver && !rotOver) return "position+velocity";
        if (posOver && !velOver && rotOver) return "position+rotation";
        if (!posOver && velOver && rotOver) return "velocity+rotation";
        if (posOver && velOver && rotOver)  return "position+velocity+rotation";
        return "below-thresholds";
    }
}
