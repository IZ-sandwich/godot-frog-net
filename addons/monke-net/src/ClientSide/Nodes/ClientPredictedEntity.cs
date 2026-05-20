using Godot;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.Client;

[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class ClientPredictedEntity : ClientNetworkBehaviour
{
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
