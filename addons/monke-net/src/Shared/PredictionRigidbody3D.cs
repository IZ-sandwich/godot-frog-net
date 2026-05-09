using Godot;
using System.Collections.Generic;

namespace MonkeNet.Shared;

/// <summary>
/// Prediction-friendly wrapper around a <see cref="RigidBody3D"/>. Mirrors Fish-Net's
/// PredictionRigidbody: forces, impulses, torques, and velocity sets are queued via
/// the wrapper instead of applied to the body directly. <see cref="Simulate"/> flushes
/// the queue to the body once per tick. This makes resimulation deterministic — the
/// same call sequence in <c>OnProcessTick</c> and <c>ResimulateTick</c> produces the
/// same body state when followed by <c>SpaceStep</c>.
///
/// Pair with <see cref="ClientPredictedEntity"/>: the entity's tick handler enqueues
/// inputs through this wrapper, the framework calls <c>Simulate</c>, then
/// <c>SpaceStep</c> integrates. <see cref="Reconcile"/> restores the body to an
/// authoritative snapshot before the resimulation loop replays subsequent ticks.
/// </summary>
[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class PredictionRigidbody3D : Node
{
    [Export] private RigidBody3D _body;

    /// <summary>
    /// Optional. When set, <see cref="Reconcile"/> hands the visual root's pre-jump pose
    /// to the smoother so the visible mesh lags behind the body for a few ticks instead
    /// of snapping. Leave null on entities that don't need it (no jarring jumps under
    /// normal play, or sphere meshes where rotation snaps aren't visible).
    /// </summary>
    [Export] private PredictionVisualSmoothing3D _smoothing;

    public RigidBody3D Body => _body;

    /// <summary>
    /// Optional visual smoother wired in the inspector. Exposed so prediction entities
    /// can route soft-correction offsets through the visual root instead of mutating
    /// the body's transform — see <see cref="PredictionVisualSmoothing3D.AddDriftCorrection"/>.
    /// Returns null when no smoother is wired (sphere meshes, props that don't need it).
    /// </summary>
    public PredictionVisualSmoothing3D Smoothing => _smoothing;

    private enum PendingKind
    {
        Force,
        ForceAtPosition,
        Impulse,
        ImpulseAtPosition,
        Torque,
        TorqueImpulse,
        SetLinearVelocity,
        SetAngularVelocity,
    }

    private struct PendingOp
    {
        public PendingKind Kind;
        public Vector3 Vector;
        public Vector3 Position;
    }

    private readonly List<PendingOp> _pending = new();

    /// <summary>
    /// Wires the wrapper to a RigidBody3D programmatically (used by tests; in scenes
    /// the [Export] field is set in the editor). Safe to call multiple times.
    /// </summary>
    public void Initialize(RigidBody3D body, PredictionVisualSmoothing3D smoothing = null)
    {
        _body = body;
        _smoothing = smoothing;
        _pending.Clear();
    }

    public void AddForce(Vector3 force) =>
        _pending.Add(new PendingOp { Kind = PendingKind.Force, Vector = force });

    public void AddForceAtPosition(Vector3 force, Vector3 position) =>
        _pending.Add(new PendingOp { Kind = PendingKind.ForceAtPosition, Vector = force, Position = position });

    public void AddImpulse(Vector3 impulse) =>
        _pending.Add(new PendingOp { Kind = PendingKind.Impulse, Vector = impulse });

    public void AddImpulseAtPosition(Vector3 impulse, Vector3 position) =>
        _pending.Add(new PendingOp { Kind = PendingKind.ImpulseAtPosition, Vector = impulse, Position = position });

    public void AddTorque(Vector3 torque) =>
        _pending.Add(new PendingOp { Kind = PendingKind.Torque, Vector = torque });

    public void AddTorqueImpulse(Vector3 torque) =>
        _pending.Add(new PendingOp { Kind = PendingKind.TorqueImpulse, Vector = torque });

    /// <summary>
    /// Queues an absolute set of linear velocity. Use sparingly — overrides whatever
    /// the simulation produced. Useful for clamping max speed or directional jumps.
    /// </summary>
    public void SetLinearVelocity(Vector3 velocity) =>
        _pending.Add(new PendingOp { Kind = PendingKind.SetLinearVelocity, Vector = velocity });

    public void SetAngularVelocity(Vector3 velocity) =>
        _pending.Add(new PendingOp { Kind = PendingKind.SetAngularVelocity, Vector = velocity });

    /// <summary>
    /// Flushes all queued operations to the underlying RigidBody3D. Call once per
    /// tick after the entity's tick handler and before <c>PhysicsServer3D.SpaceStep</c>.
    /// </summary>
    public void Simulate()
    {
        if (_body == null) return;
        if (_pending.Count > 0)
        {
            MonkeLogger.Debug($"[PHYS-RB-SIMULATE] body={_body.Name} pending={_pending.Count} prePos=({_body.GlobalPosition.X:F3},{_body.GlobalPosition.Y:F3},{_body.GlobalPosition.Z:F3}) preVel=({_body.LinearVelocity.X:F3},{_body.LinearVelocity.Y:F3},{_body.LinearVelocity.Z:F3})");
        }
        for (int i = 0; i < _pending.Count; i++)
        {
            var p = _pending[i];
            string atSuffix = (p.Kind == PendingKind.ForceAtPosition || p.Kind == PendingKind.ImpulseAtPosition)
                ? $" at=({p.Position.X:F3},{p.Position.Y:F3},{p.Position.Z:F3})" : "";
            MonkeLogger.Debug($"[PHYS-RB-SIMULATE]   {p.Kind} v=({p.Vector.X:F3},{p.Vector.Y:F3},{p.Vector.Z:F3}){atSuffix}");
            switch (p.Kind)
            {
                case PendingKind.Force: _body.ApplyCentralForce(p.Vector); break;
                case PendingKind.ForceAtPosition: _body.ApplyForce(p.Vector, p.Position); break;
                case PendingKind.Impulse: _body.ApplyCentralImpulse(p.Vector); break;
                case PendingKind.ImpulseAtPosition: _body.ApplyImpulse(p.Vector, p.Position); break;
                case PendingKind.Torque: _body.ApplyTorque(p.Vector); break;
                case PendingKind.TorqueImpulse: _body.ApplyTorqueImpulse(p.Vector); break;
                case PendingKind.SetLinearVelocity: _body.LinearVelocity = p.Vector; break;
                case PendingKind.SetAngularVelocity: _body.AngularVelocity = p.Vector; break;
            }
        }
        _pending.Clear();
    }

    /// <summary>Number of pending operations not yet flushed by <see cref="Simulate"/>.</summary>
    public int PendingCount => _pending.Count;

    public RigidbodyState SnapshotState()
    {
        if (_body == null) return default;
        return new RigidbodyState
        {
            Position = _body.GlobalPosition,
            Rotation = _body.Quaternion,
            LinearVelocity = _body.LinearVelocity,
            AngularVelocity = _body.AngularVelocity,
        };
    }

    /// <summary>
    /// Restores the body to an authoritative snapshot, clears any pending operations,
    /// and flushes the new transform to the physics server. Call from
    /// <see cref="Client.ClientPredictedEntity.HandleReconciliation"/>.
    /// </summary>
    public void Reconcile(RigidbodyState authoritative)
    {
        if (_body == null) return;

        // Capture the visual's current world pose BEFORE we teleport the body so the
        // smoother can keep the visible mesh exactly there and lerp it back over a few
        // ticks. Done here (not inside the smoother) so we read the actual rendered
        // pose, not whatever the smoother last wrote to it.
        Vector3 preVisualPos = Vector3.Zero;
        Quaternion preVisualRot = Quaternion.Identity;
        bool smoothingEnabled = _smoothing != null && _smoothing.Visual != null;
        if (smoothingEnabled)
        {
            preVisualPos = _smoothing.Visual.GlobalPosition;
            preVisualRot = _smoothing.Visual.Quaternion;
        }

        Vector3 prePos = _body.GlobalPosition;
        Vector3 preVel = _body.LinearVelocity;
        Vector3 posDelta = authoritative.Position - prePos;
        Vector3 velDelta = authoritative.LinearVelocity - preVel;
        MonkeLogger.Debug($"[PHYS-RB-RECONCILE] body={_body.Name} prePos=({prePos.X:F3},{prePos.Y:F3},{prePos.Z:F3}) -> authPos=({authoritative.Position.X:F3},{authoritative.Position.Y:F3},{authoritative.Position.Z:F3}) |posDelta|={posDelta.Length():F4} preVel=({preVel.X:F3},{preVel.Y:F3},{preVel.Z:F3}) -> authVel=({authoritative.LinearVelocity.X:F3},{authoritative.LinearVelocity.Y:F3},{authoritative.LinearVelocity.Z:F3}) |velDelta|={velDelta.Length():F4} smoothing={smoothingEnabled} pendingDropped={_pending.Count}");

        // Atomic Transform set propagates Position + Rotation to the physics
        // server in a single update; setting them separately can leave the body
        // briefly inconsistent and (more importantly) leaves Basis with whatever
        // scale/shear drift accumulated from prior frames. Reconstructing Basis
        // from the authoritative quaternion guarantees a clean orthonormal basis.
        _body.GlobalTransform = new Transform3D(new Basis(authoritative.Rotation), authoritative.Position);
        _body.LinearVelocity = authoritative.LinearVelocity;
        _body.AngularVelocity = authoritative.AngularVelocity;
        // Clear residual continuous forces / torques. RigidBody3D keeps
        // ConstantForce/ConstantTorque applied every tick until cleared; if game
        // code ever called AddConstantForce (not AddImpulse) those would otherwise
        // outlive the rollback and corrupt the resimulated trajectory.
        _body.ConstantForce = Vector3.Zero;
        _body.ConstantTorque = Vector3.Zero;
        _pending.Clear();
        _body.ForceUpdateTransform();

        // With physics_interpolation enabled the visual mesh lerps between the body's
        // _previous and _current transforms each render frame. After a teleport-style
        // reset the _previous transform is still the pre-rollback predicted pose — if
        // the renderer hits before the next SpaceStep overwrites it, the mesh shows a
        // bogus interpolated frame between two unrelated rotations. ResetPhysicsInterpolation
        // collapses both buffers to the new transform, so the body teleports cleanly.
        _body.ResetPhysicsInterpolation();

        if (smoothingEnabled)
            _smoothing.OnReconciled(preVisualPos, preVisualRot);
    }
}
