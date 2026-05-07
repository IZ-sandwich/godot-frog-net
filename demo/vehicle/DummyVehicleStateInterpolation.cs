using Godot;
using MonkeNet.Client;
using MonkeNet.Shared;

namespace GameDemo;

/// <summary>
/// Drives a non-owned vehicle on remote clients. The body is a RigidBody3D so the local
/// player's push impulses are felt immediately; the server snapshot is then applied each
/// frame as a soft correction (lerp toward target, snap on large divergence) so the
/// vehicle doesn't drift away from authoritative state.
/// Mirrors DummyBallStateInterpolation — see that class for detailed rationale.
/// </summary>
public partial class DummyVehicleStateInterpolation : ClientInterpolatedEntity
{
    [Export] private RigidBody3D _body;
    // Squared distance above which we hard-snap to the server state instead of soft-correcting.
    [Export] private float _snapDistanceSquared = 1.0f;
    // Per-call blend toward the server-interpolated target.
    [Export] private float _correctionBlend = 0.15f;

    private Vector3 _lastFuturePosition;
    private bool _haveLastFuture;

    public override void HandleStateInterpolation(IEntityStateData past, IEntityStateData future, float interpolationFactor)
    {
        var pastState = (EntityStateMessage)past;
        var futureState = (EntityStateMessage)future;

        Vector3 targetPos = pastState.Position.Lerp(futureState.Position, interpolationFactor);
        var pastQ = Quaternion.FromEuler(pastState.Rotation);
        var futureQ = Quaternion.FromEuler(futureState.Rotation);
        Quaternion targetRot = pastQ.Slerp(futureQ, interpolationFactor).Normalized();

        Vector3 delta = targetPos - _body.GlobalPosition;

        if (delta.LengthSquared() > _snapDistanceSquared)
        {
            _body.GlobalPosition = targetPos;
            _body.Quaternion = targetRot;
            _body.LinearVelocity = futureState.Velocity;
            _body.AngularVelocity = futureState.AngularVelocity;
            _body.ResetPhysicsInterpolation();
            _lastFuturePosition = futureState.Position;
            _haveLastFuture = true;
            return;
        }

        // Sync velocity to the server's authoritative value when a brand-new snapshot
        // becomes the future target. Between snapshots the same future is passed in
        // repeatedly — those calls only soft-correct position so local impulses keep
        // accumulating instead of being overwritten every render frame.
        bool newSnapshot = !_haveLastFuture || futureState.Position != _lastFuturePosition;
        if (newSnapshot)
        {
            _body.LinearVelocity = futureState.Velocity;
            _body.AngularVelocity = futureState.AngularVelocity;
            _lastFuturePosition = futureState.Position;
            _haveLastFuture = true;
        }

        _body.GlobalPosition = _body.GlobalPosition.Lerp(targetPos, _correctionBlend);
        _body.Quaternion = _body.Quaternion.Normalized().Slerp(targetRot, _correctionBlend).Normalized();
    }
}
