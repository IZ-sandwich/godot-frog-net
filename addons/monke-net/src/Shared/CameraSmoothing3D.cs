using Godot;

namespace MonkeNet.Shared;

/// <summary>
/// First-person camera smoother for predicted rigid-body players. Mirrors
/// Source's <c>m_vecPredictionError</c> / Quake III's <c>cg.predictedError</c>
/// design: the body hard-snaps on each reconcile, but the rendered camera
/// position keeps the captured prediction error and decays it to zero over
/// <see cref="DecayTime"/>. The visual mesh smoother
/// (<see cref="PredictionVisualSmoothing3D"/>) optimises for what a third-
/// party observer sees; this one optimises for what the owning player's eye
/// feels, which is a fundamentally different requirement. M19 numbers on
/// S3-CubeStackPushT2KI without this smoother showed camera-jolt p99 of
/// 11-16 m/s on C3-C5 while M14 visual-jolt p95 was under 1 m/s — the
/// existing smoother was protecting observers and leaving the eye unsmoothed.
///
/// <para>
/// Mechanism: <see cref="CaptureBodyTeleport"/> is called from
/// <see cref="PredictionRigidbody3D.Reconcile"/> whenever the body teleports
/// to an authoritative pose. The captured error
/// (<c>postPos − prePos</c>) seeds <see cref="_errorOffset"/>. Each
/// <c>_Process</c> linearly attenuates this by
/// <c>max(0, 1 − elapsed / DecayTime)</c> — matching Source's linear-decay
/// behaviour — and writes the camera at <c>natural_world_pose − attenuated_error</c>
/// so the rendered pose continues to look as if the prediction had been
/// correct, then drifts to the actual body pose over the decay window.
/// </para>
///
/// <para>
/// Chained reconciles during the decay window accumulate onto the residual
/// visible error rather than restarting from the new body pose — this is
/// what stops the camera from snapping when C4/C5 fires reconciles faster
/// than a single decay window completes (the typical sustained-contact
/// case). A new error larger than <see cref="MaxSmoothableDistance"/> is
/// treated as a real teleport (respawn, scripted move) and snaps the
/// camera; the smoother is for prediction error, not arbitrary game-driven
/// jumps.
/// </para>
///
/// <para>
/// Wiring: the camera is NOT made <c>TopLevel</c>, because the parent
/// <c>RotationHelperY</c> applies the player's yaw input via local rotation
/// and we want that to keep flowing through. Instead, we apply the
/// error as a local-frame offset on the camera node — equivalent to
/// shifting the camera within its parent's local space such that the
/// final world position lands at <c>natural − error</c>. The baseline
/// local position captured at <c>_Ready</c> is restored when the error
/// fully decays so we don't leave the camera permanently offset.
/// </para>
/// </summary>
[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class CameraSmoothing3D : Node3D
{
    /// <summary>Camera node to smooth. Its local position is written each
    /// render frame; its rotation is left alone (the helper / camera
    /// controller continues to drive that).</summary>
    [Export] public Node3D Camera { get; set; }

    /// <summary>Exponential decay time-constant (seconds). Each
    /// <c>_Process</c>, the residual error is multiplied by
    /// <c>exp(-delta / DecayTime)</c>. The visible offset halves every
    /// ~0.69 × DecayTime and reaches ~5 % after 3 × DecayTime. Continuous
    /// decay (not "restart timer on capture") bounds the steady-state
    /// error under sustained reconcile streams to ~capture_rate × delta
    /// × DecayTime. Source's cl_smoothtime ships with the same 0.1 s
    /// window for a similar reason.</summary>
    [Export] public float DecayTime { get; set; } = 0.1f;

    /// <summary>Hard cap on the visible camera-vs-body offset (meters).
    /// Captures that would push the residual past this are clamped along
    /// the residual direction. Bounds the per-frame Δv at the next render
    /// frame after a capture to <c>cap / dt</c>: for the 0.5 m default,
    /// that's ~5 m/s on a single capture event at 60 Hz, comfortably
    /// inside vestibular tolerance. Without this clamp the per-frame Δv
    /// scales linearly with capture magnitude — observed at >10 m/s on
    /// C1/C2 chained contact reconciles where the residual grew to 1+ m.</summary>
    [Export] public float MaxOffsetDistance { get; set; } = 0.5f;

    /// <summary>Above this magnitude (meters), captured errors snap
    /// instead of smoothing. Real teleports (respawn, level transition)
    /// shouldn't be confused with prediction errors. 5 m at 5 m/s
    /// convergence = 1 s of smoothing; anything beyond is a hard cut.</summary>
    [Export] public float MaxSmoothableDistance { get; set; } = 5.0f;

    /// <summary>Captures below this magnitude (meters) are ignored. Sub-cm
    /// reconciles are not meaningful for camera comfort and refreshing
    /// the decay timer with a tiny error would leave the existing
    /// (larger) residual decaying forever. Set 0 to disable filtering.</summary>
    [Export] public float MinCaptureDistance { get; set; } = 0.02f;

    private Vector3 _errorOffset;
    private Vector3 _baselineLocalPos;
    private Node3D _cameraParent;
    private bool _initialized;

    public override void _Ready()
    {
        SetProcess(true);
        if (Camera != null)
        {
            _baselineLocalPos = Camera.Position;
            _cameraParent = Camera.GetParent<Node3D>();
            _initialized = _cameraParent != null;
        }
    }

    /// <summary>Capture the body's just-completed teleport.
    /// <paramref name="prePos"/> = world-space body pose before the
    /// reconcile, <paramref name="postPos"/> = world-space body pose after.
    /// The camera will keep rendering at the pre-teleport pose and decay
    /// to the post-teleport pose over <see cref="DecayTime"/>. Chained
    /// captures during an in-flight decay accumulate onto the visible
    /// residual error rather than snapping.</summary>
    public void CaptureBodyTeleport(Vector3 prePos, Vector3 postPos)
    {
        if (!_initialized) return;

        Vector3 delta = postPos - prePos;
        if (delta.Length() < MinCaptureDistance) return;

        // Accumulate onto the current visible offset rather than overwriting.
        // Overwriting causes a one-frame position jump of magnitude
        // |residual + delta| when residual and delta disagree, observed as
        // p99 spikes >30 m/s on C2 under chained contact-reconciles where
        // a partially-decayed forward residual got overwritten by a smaller
        // new error. Accumulation keeps the visible offset continuous across
        // the capture; the per-frame Δv is determined by the decay rate
        // (error / DecayTime) rather than the residual replacement.
        // Accumulate onto the residual rather than overwriting: overwriting
        // causes a one-frame jump of |residual − new_delta| when chained
        // captures disagree in direction (observed as p99 > 30 m/s on C2).
        // Accumulation keeps the visible offset continuous across each
        // capture; combined with the clamp below, the per-frame Δv at the
        // next render is bounded by MaxOffsetDistance / dt.
        Vector3 newError = _errorOffset + delta;
        float mag = newError.Length();
        if (mag > MaxSmoothableDistance)
        {
            // Real teleport — drop the residual and let the camera snap
            // to the body next frame.
            _errorOffset = Vector3.Zero;
            return;
        }
        if (mag > MaxOffsetDistance)
            newError *= MaxOffsetDistance / mag;
        _errorOffset = newError;
    }

    public override void _Process(double delta)
    {
        if (!_initialized) return;

        if (!_errorOffset.IsZeroApprox() && DecayTime > 0f)
        {
            float alpha = Mathf.Exp(-(float)delta / DecayTime);
            _errorOffset *= alpha;
            if (_errorOffset.LengthSquared() < 1e-8f) _errorOffset = Vector3.Zero;
        }
        Vector3 worldError = _errorOffset;

        if (worldError.IsZeroApprox())
        {
            // Done decaying. Restore baseline so the camera tracks the body
            // 1:1 again via the parent chain. Reset FTI so the renderer
            // doesn't lerp from the just-applied offset back to baseline
            // across one render frame (would look like a slow drift to
            // the natural pose, defeating the convergence we just earned).
            if (!Camera.Position.IsEqualApprox(_baselineLocalPos))
            {
                Camera.Position = _baselineLocalPos;
                Camera.ResetPhysicsInterpolation();
            }
            return;
        }

        // Express the world-space error in the camera's parent local frame.
        // Parent is RotationHelperY (yaw-only rotation from input); inverting
        // its basis on the world error gives the local translation that —
        // when applied to Camera.Position — produces the desired world-space
        // offset on the rendered pose.
        Basis parentBasis = _cameraParent.GlobalTransform.Basis;
        Vector3 localError = parentBasis.Inverse() * worldError;
        Camera.Position = _baselineLocalPos - localError;
        // Same reason as the FirstPersonCameraController's reset after mouse
        // motion: we're writing the local transform from _Process (outside
        // the physics tick), so Godot's FTI would otherwise lerp from the
        // previous prev/curr pair toward this new value across one render
        // frame — visible as a one-frame drift to the smoothed position
        // rather than landing on it immediately.
        Camera.ResetPhysicsInterpolation();
    }

    /// <summary>Reset the camera smoother to its baseline. Called by
    /// the harness's <c>visual-smoothness-reset</c> command after scenario
    /// setup so spawn-time captures (player falling to ground produces a
    /// 1.5 m error in Y that decays into the M19 sample window) don't
    /// pollute the measurement.</summary>
    public void Reset()
    {
        _errorOffset = Vector3.Zero;
        if (_initialized && !Camera.Position.IsEqualApprox(_baselineLocalPos))
        {
            Camera.Position = _baselineLocalPos;
            Camera.ResetPhysicsInterpolation();
        }
    }
}
