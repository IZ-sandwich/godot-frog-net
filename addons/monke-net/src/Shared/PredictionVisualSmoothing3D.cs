using Godot;

namespace MonkeNet.Shared;

/// <summary>
/// Library-managed visual interpolation for predicted physics bodies.
///
/// The smoother owns the visual's render-frame position end-to-end:
///
///   - <c>_PhysicsProcess</c> snapshots <c>vis_target = body.GlobalPosition + posOffset</c>
///     once per physics tick into a prev/curr buffer. Same tick the offset decays
///     exponentially toward zero, and unexplained body jumps (reconcile,
///     SyncSleepState, manual transform writes) get absorbed into the offset.
///
///   - <c>_Process</c> writes <c>Visual.GlobalPosition = Lerp(prev, curr, pif)</c>
///     every render frame using <c>Engine.GetPhysicsInterpolationFraction()</c>.
///     This is the actual rendered position. No reliance on Godot's <c>SceneTreeFTI</c>
///     cache, which can go stale when a rollback writes mid-frame (the cache is
///     computed once per <c>frame_update</c> before <c>_Process</c> runs and is
///     not invalidated by post-cache writes to <c>local_transform</c>, producing
///     a backward visual jolt the next frame as the cache catches up to the new
///     prev/curr). See <see cref="FixupOffsetAfterResim"/> for the rollback path.
///
/// The Visual is set <c>top_level = true</c> so the body's transform never drags
/// the visual along — the smoother is the only writer to the visual's world pose.
/// The Visual stays a child of the body in the scene tree for organisation /
/// lifecycle (auto-freed when body freed). For <see cref="SmoothRotation"/>=false
/// (caller-owned rotation, e.g. a knight rig whose yaw is driven from input every
/// tick), the caller continues to write <c>Visual.Rotation</c>; since the node is
/// top_level those writes are world-space rather than body-relative, which is the
/// natural interpretation for player-facing rotation.
///
/// Why not rely on Godot's per-render-frame interpolation:
///   1. SceneTreeFTI caches <c>data.global_transform_interpolated</c> once per
///      render frame at <c>frame_update</c> (start of frame). The cache is read
///      by both the renderer AND <c>get_global_transform_interpolated()</c>.
///   2. A rollback writes <c>Visual.GlobalPosition</c> mid-frame from a network
///      callback. The write updates <c>local_transform</c> but does NOT invalidate
///      the cache. The current render frame still draws the stale cached value.
///   3. The next render frame's <c>frame_update</c> recomputes the cache from
///      the new <c>local_transform_prev/curr</c>. The cache jumps from
///      pre-rollback-interp to post-rollback-interp. Visible as a one-frame
///      backward jolt of the visual (the bug this smoother was originally built
///      to mask but couldn't fully — the cache jump is downstream of where the
///      smoother could intervene).
/// Owning the per-render write removes the cache entirely from the path.
/// </summary>
[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class PredictionVisualSmoothing3D : Node3D
{
    [Export] public Node3D Body { get; set; }
    [Export] public Node3D Visual { get; set; }
    /// <summary>Optional first-person camera node. When set, the smoother
    /// samples its rendered (FTI-interpolated) world position each
    /// <c>_Process</c> and accumulates per-render-frame |Δv|² into
    /// <see cref="CameraJoltDvSquaredSamples"/>. Reports M19 — the camera-
    /// motion analogue of M14, measuring what the player's eye actually
    /// experiences each render frame. Null on third-person-only smoothers
    /// (passive props, remote players) so they don't pay the sample cost.</summary>
    [Export] public Node3D Camera { get; set; }

    /// <summary>Decay strategy for the position offset. The rotation path is
    /// unaffected — rotation captures are always reset to identity in
    /// <see cref="AbsorbBodyTeleport"/> regardless of mode.</summary>
    public enum PosDecayMode
    {
        /// <summary>Original behaviour. Each tick: <c>posOffset *= exp(-dt /
        /// DecayTime)</c>. Smooth-looking for one-shot teleports but pathological
        /// under per-snapshot reconcile streams: the body advances by
        /// <c>body_velocity * dt</c> each tick while the new correction adds
        /// <c>-delta</c>; under steady-state push these cancel and the visual
        /// freezes at <c>body + offset</c>. Between snapshots the offset decays
        /// exponentially and the visual sprints to catch up. This is the
        /// freeze-then-sprint M15/M16 artefact observed on S3-SingleCubePush.
        /// Kept as the default so existing scenes don't change behaviour.</summary>
        Exponential,
        /// <summary>Q3 / UE-Linear-mode style. Each tick: <c>posOffset =
        /// originalOffset * max(0, 1 - elapsed / SmoothingTime)</c>. Produces a
        /// constant correction velocity (<c>originalOffset / SmoothingTime</c>)
        /// that ADDS to body velocity instead of fighting it — visual moves at
        /// <c>body_velocity + correction_velocity</c> through the entire decay
        /// window. Chained per-snapshot corrections just keep refreshing the
        /// constant, so the visual continues at a steady catch-up rate rather
        /// than freezing then sprinting. Recommended for any scenario with
        /// sustained reconcile streams (continuous push, multi-body chaos).</summary>
        Linear,
        /// <summary>
        /// Magnitude-adaptive linear decay with a per-frame minimum-correction
        /// floor. Modelled on Photon Fusion 2's
        /// <c>InterpolatedErrorCorrectionSettings</c> (see Fusion 2 API
        /// reference — they replaced Fusion 1's pure-exponential with this
        /// after users reported the same freeze-then-sprint pathology).
        ///
        /// <para>Each <c>_PhysicsProcess</c>:
        /// <code>
        /// mag  = |posOffset|
        /// t    = saturate((mag - BlendStart) / (BlendEnd - BlendStart))
        /// rate = lerp(MinRate, MaxRate, t)                  // Hz
        /// step = max(mag * rate * dt, MinCorrection)        // floor
        /// step = min(step, mag)                             // no overshoot
        /// posOffset -= (posOffset / mag) * step             // toward zero
        /// </code></para>
        ///
        /// <para>Two key behaviours that distinguish this from
        /// <see cref="Exponential"/>:</para>
        /// <para>1. <b>Rate adapts to magnitude</b>: tiny errors decay slowly
        /// (MinRate ≈ 3.3 Hz → ~210 ms half-life), large errors decay fast
        /// (MaxRate = 10 Hz → ~70 ms half-life). Bursty multi-decimetre
        /// corrections converge quickly without the small-error visual
        /// hunting that pure-fast-decay causes.</para>
        /// <para>2. <b>Minimum-correction floor</b>: even tiny offsets get
        /// closed by at least <see cref="MagnitudeAdaptiveMinCorrection"/>
        /// per frame (clamped to actual offset so we never overshoot). This
        /// is the explicit anti-freeze-then-sprint mechanism — sub-floor
        /// offsets resolve in 1-3 frames instead of asymptoting forever
        /// under chained reconciles.</para>
        ///
        /// <para>No overshoot, no extrapolation — the correction is always
        /// strictly toward the body's current pose with a per-frame cap.
        /// Recommended for production over both Exponential and Linear.</para>
        /// </summary>
        MagnitudeAdaptive,
    }

    /// <summary>Decay strategy for <see cref="_posOffset"/>. See
    /// <see cref="PosDecayMode"/> for the trade-offs. Default
    /// <see cref="PosDecayMode.Exponential"/> preserves the pre-existing
    /// behaviour of every scene that already has this smoother wired up.</summary>
    [Export] public PosDecayMode PositionDecayMode { get; set; } = PosDecayMode.Exponential;

    /// <summary>Time constant (seconds) for exponential offset decay. After
    /// DecayTime seconds the offset has decayed to ~37% of its captured value;
    /// after 3×DecayTime it is effectively zero. Used only when
    /// <see cref="PositionDecayMode"/> = <see cref="PosDecayMode.Exponential"/>.</summary>
    [Export] public float DecayTime { get; set; } = 0.1f;

    /// <summary>Linear-decay window (seconds). The captured offset reaches
    /// zero exactly at this elapsed wall-clock time, producing a constant
    /// correction velocity equal to <c>originalOffset / SmoothingTime</c>.
    /// Used only when <see cref="PositionDecayMode"/> =
    /// <see cref="PosDecayMode.Linear"/>. Defaults to the same 0.1 s window
    /// used by <see cref="DecayTime"/> so the two modes converge to zero in
    /// the same wall-clock budget; tune per scene if the visible catch-up
    /// velocity feels too fast (raise it) or too laggy (lower it). Quake 3
    /// uses ~100 ms by default; Unreal's Linear mode default is ~100 ms.</summary>
    [Export] public float SmoothingTime { get; set; } = 0.1f;

    // ------------------ MagnitudeAdaptive mode params ------------------
    // Matches Photon Fusion 2's InterpolatedErrorCorrectionSettings defaults
    // exactly — Fusion engineers tuned these against shipping projects with
    // mixed-rigidbody contact workloads, so they're a good starting point.
    // Override per-smoother if a particular prop's contact regime is
    // unusual (very fast spinners want a higher MinRate, very slow drifters
    // a lower one).

    /// <summary>MagnitudeAdaptive — minimum decay rate (Hz). Applied to
    /// offsets ≤ <see cref="MagnitudeAdaptiveBlendStart"/>. At 3.3 Hz the
    /// half-life is ~210 ms — slow enough that tiny per-snapshot Jolt
    /// drift doesn't visibly hunt, fast enough that small offsets resolve
    /// before the eye registers them as persistent visual lag.</summary>
    [Export] public float MagnitudeAdaptiveMinRate { get; set; } = 3.3f;

    /// <summary>MagnitudeAdaptive — maximum decay rate (Hz). Applied to
    /// offsets ≥ <see cref="MagnitudeAdaptiveBlendEnd"/>. At 10 Hz the
    /// half-life is ~70 ms — fast enough that multi-decimetre rollback
    /// corrections converge before the next render frame so the visual
    /// snaps cleanly without sustained rubber-banding.</summary>
    [Export] public float MagnitudeAdaptiveMaxRate { get; set; } = 10.0f;

    /// <summary>MagnitudeAdaptive — offset magnitude (meters) at or below
    /// which <see cref="MagnitudeAdaptiveMinRate"/> applies. Between this
    /// and <see cref="MagnitudeAdaptiveBlendEnd"/> the rate is linearly
    /// interpolated. Fusion 2's default 0.25 m is a reasonable contact-
    /// scale boundary — sub-25 cm errors are micro-corrections that should
    /// resolve gently; above this they're contact / rollback events that
    /// want a more aggressive catch-up.</summary>
    [Export] public float MagnitudeAdaptiveBlendStart { get; set; } = 0.25f;

    /// <summary>MagnitudeAdaptive — offset magnitude (meters) at or above
    /// which <see cref="MagnitudeAdaptiveMaxRate"/> applies. Errors past
    /// this are large rollback / teleport corrections; they get the
    /// fastest decay so the visual converges before
    /// <see cref="TeleportDistance"/> kicks in. Fusion 2 default 1.0 m.</summary>
    [Export] public float MagnitudeAdaptiveBlendEnd { get; set; } = 1.0f;

    /// <summary>MagnitudeAdaptive — per-frame minimum correction magnitude
    /// (meters). The CORE anti-freeze-then-sprint mechanism: even when
    /// <c>mag * rate * dt</c> would round to a sub-mm value, we still close
    /// the offset by at least this much per render frame (clamped to actual
    /// offset so we never overshoot). Without this floor the algorithm
    /// degenerates to exponential decay and produces the same freeze
    /// pattern. Fusion 2 default 0.025 m — small enough to be sub-perceptual
    /// per frame, large enough to converge a 0.1 m offset in 4 frames
    /// instead of asymptoting.</summary>
    [Export] public float MagnitudeAdaptiveMinCorrection { get; set; } = 0.025f;

    /// <summary>Smallest position jump (meters) over a single frame, after
    /// subtracting <c>LinearVelocity * dt</c>, that counts as a teleport. Below
    /// this everything is treated as physics integration noise and ignored.</summary>
    [Export] public float PositionJumpEpsilon { get; set; } = 0.002f;

    /// <summary>Smallest rotation jump (radians) over a single frame, after
    /// subtracting the rotation implied by <c>AngularVelocity * dt</c>, that
    /// counts as a teleport.</summary>
    [Export] public float RotationJumpEpsilonRad { get; set; } = 0.005f;

    /// <summary>If the accumulated offset ever exceeds this, snap it to zero
    /// instead of smoothing. Lerping a multi-meter correction looks worse than
    /// a teleport because the visual trails far behind collision. 0 disables
    /// the threshold.</summary>
    [Export] public float TeleportDistance { get; set; } = 5f;

    /// <summary>When false, the smoother only manages POSITION offsets and never
    /// writes the visual's rotation — caller-owned rotation (e.g. a knight rig
    /// whose Y yaw is driven from camera input every tick) is preserved. Default
    /// true preserves the original prop/vehicle behaviour where the visual's
    /// rotation is fully derived from the body.</summary>
    [Export] public bool SmoothRotation { get; set; } = true;

    /// <summary>Aggression multiplier for the PVB rotation slerp. Murphy's
    /// canonical PVB uses the same linear blendFactor for both position and
    /// rotation; here we let rotation catch up faster by scaling the rotation
    /// blendFactor before clamping to [0,1]. At the default 3.0 the rotation
    /// slerp reaches body.Quaternion when the position blend is one-third of
    /// the way through the window, i.e. ~83 ms of rotation convergence for a
    /// 250 ms PVB position window. Set to 1.0 to recover canonical paired
    /// behaviour; set higher for snappier rotation tracking on bodies whose
    /// rotation evolves on a much shorter timescale than their translation
    /// (cubes mid-spin while being kicked across the floor — position takes
    /// hundreds of ms to converge but rotation needs to track tens-of-ms
    /// reconcile cycles or visual lag becomes visible).</summary>
    [Export] public float RotationBlendAggression { get; set; } = 3.0f;

    // World-space position offset between visual and body. Decays toward zero.
    private Vector3 _posOffset;
    // Linear-decay state (used only when PositionDecayMode = Linear).
    // _originalPosOffset captures _posOffset immediately after a fresh
    // AbsorbBodyTeleport / FixupOffsetAfterResim / ClearOffset writes it,
    // and serves as the baseline for the linear ramp: posOffset(t) =
    // originalPosOffset * (1 - elapsed/SmoothingTime). Chained captures
    // restart the ramp by re-snapping the baseline and resetting elapsed —
    // matching Q3's cg.predictedError model.
    private Vector3 _originalPosOffset;
    private float _smoothingElapsed;

    // Rebase the linear-decay state to the current _posOffset value. Called
    // after every site that writes _posOffset so the next _PhysicsProcess
    // tick starts the linear ramp from "right now" — chained corrections
    // restart the ramp instead of stacking. Cheap no-op when PositionDecayMode
    // is Exponential, but we always update so a mid-scene mode flip doesn't
    // need to re-initialise the baseline.
    private void RebaseLinearDecay()
    {
        _originalPosOffset = _posOffset;
        _smoothingElapsed = 0f;
    }
    // World-space rotation offset: Visual.Quaternion = _rotOffset * Body.Quaternion.
    // Decays toward identity. World-frame (left-multiplied) keeps the teleport
    // capture math symmetric with the position path — body rotates by R in
    // world space, offset absorbs R⁻¹.
    private Quaternion _rotOffset = Quaternion.Identity;

    // Per-physics-tick body pose, used by CaptureUnexplainedJump to diff
    // actual body motion against vel*dt.
    private Vector3 _prevBodyPos;
    private Quaternion _prevBodyRot = Quaternion.Identity;
    // Velocity captured at the START of the PREVIOUS _PhysicsProcess — the
    // velocity that was current BEFORE the engine's last integration step.
    // CaptureUnexplainedJump uses this (not the post-step velocity reachable
    // via Body.LinearVelocity at the current call) to compute the expected
    // delta the engine just integrated. This is the integration-scheme-agnostic
    // way to detect "unexplained" body motion: we compare the actual delta
    // (bodyPos − _prevBodyPos) against what semi-implicit Euler / Jolt would
    // have produced from the known pre-step state. Using the post-step velocity
    // (which already includes damping / contact response from the step we're
    // measuring) re-introduced a few mm of "fictitious jump" per tick, which
    // CaptureUnexplainedJump folded into _posOffset every tick faster than
    // the exponential decay could drain it — producing a persistent ~0.4 m
    // visual lag during constant-velocity motion (S2 plots, 2026-06-08).
    private Vector3 _prevLinVel;
    private Vector3 _prevAngVel;
    private bool _hasPrev;

    // Per-physics-tick target visual pose: prev = end of LAST physics tick,
    // curr = end of CURRENT physics tick. _Process lerps between them each
    // render frame using Engine.GetPhysicsInterpolationFraction(). This is the
    // library-managed interpolation buffer that replaces the engine's
    // SceneTreeFTI cache for this node.
    private Vector3 _visTargetPrev;
    private Vector3 _visTargetCurr;
    private Quaternion _visTargetRotPrev = Quaternion.Identity;
    private Quaternion _visTargetRotCurr = Quaternion.Identity;
    private bool _hasTarget;

    // Last position the smoother actually wrote to Visual.GlobalPosition from
    // _Process. Logged so post-hoc analysis sees the same value the user saw
    // on screen rather than whatever Visual.GetGlobalTransformInterpolated()
    // returns (which can be stale by one frame — see class docstring).
    private Vector3 _lastRenderedPos;
    private Quaternion _lastRenderedRot = Quaternion.Identity;

    // Projective Velocity Blending (Lengyel 2011, "Believable Dead Reckoning
    // for Networked Games", Game Engine Gems 2 ch. 22). When the snap-overflow
    // path teleports the body by multiple metres in one tick — too far for
    // exponential offset decay to absorb without producing a multi-m/s Δv on
    // the very next frame — we instead run a parallel kinematic projection
    // from the pre-snap visual state ("old") and the post-snap auth state
    // ("new"), and linearly blend between them over <see cref="PvbDurationSec"/>.
    // Both projections curve forward at constant velocity so the blended
    // trajectory's per-frame Δv stays bounded ≈ |newVel − oldVel|, independent
    // of how big the position correction was.
    //
    // While <c>_pvbActive == true</c>, <see cref="_Process"/> ignores the
    // past-interpolation lerp buffer entirely and writes the blended PVB
    // position to Visual.GlobalPosition. _PhysicsProcess still runs but the
    // lerp buffer it maintains doesn't affect the rendered position; the
    // buffer is re-pinned to the blended position on PVB completion so the
    // normal smoother resumes seamlessly.
    private bool _pvbActive;
    private float _pvbElapsed;
    private float _pvbDuration;
    private Vector3 _pvbOldPos, _pvbOldVel;
    private Vector3 _pvbNewPos, _pvbNewVel;
    // Rotation slerp anchor for the active PVB window. PVB is a position-only
    // technique in Murphy's "Believable Dead Reckoning for Networked Games"
    // (Game Engine Gems 2 ch. 22); rotation is handled separately. Standard
    // practice (Delta3D reference codebase, Glenn Fiedler's snapshot interp,
    // Unreal Chaos resim) is to slerp the rendered orientation toward the
    // body's current authoritative rotation using the same blendFactor that
    // drives the position trajectory blend. Capturing the start orientation
    // here lets us do the canonical single-target slerp(_pvbStartRot,
    // body.Quaternion, blendFactor) instead of the previous behaviour of
    // freezing visRot at _lastRenderedRot for the entire blend window — which
    // produced the 97° single-tick rotation jerk visible on S7-C3 eid=4
    // tick=754 when PVB chained across ~540 ms while the body actually
    // rotated through ~97° of yaw.
    private Quaternion _pvbStartRot = Quaternion.Identity;

    // Visual smoothness tracking. Each render frame in _Process we compute
    // the frame-to-frame visual velocity v_t = (visPos_t − visPos_{t-1}) / dt
    // and accumulate the squared change in velocity (|v_t − v_{t-1}|²). The
    // RMS of these per-frame Δv values quantifies how smooth the rendered
    // motion is from the user's perspective:
    //   - smooth motion (constant or smoothly-accelerating velocity)
    //     produces Δv ≈ 0 every frame → low RMS
    //   - judder, snap-overflows, stutter all produce Δv spikes →
    //     proportionally higher RMS
    // Read by the harness as the M14 quantitative-suite metric. Accumulator
    // is reset by <see cref="ResetSmoothnessAccumulator"/> when the runner
    // wants to exclude warm-up / scenario.Setup from the measurement window.
    private Vector3 _lastVisVel;
    private bool _hasLastVisVel;
    // Separate "have we ever rendered a frame" flag from "have we got a
    // previous velocity sample". Without this, the first _Process reads the
    // default Vector3.Zero in _lastRenderedPos and computes an enormous
    // currVisVel = (visPos - 0) / delta — usually 60+ m/s for a body that
    // spawned 5 m away from origin. _hasLastVisVel keeps that bogus value
    // out of the Δv calculation on frame 2, but only because we explicitly
    // discard the first frame's currVisVel below.
    private bool _hasLastRenderedPos;
    // Bucket EVERY per-render-frame |Δv|² value rather than just summing,
    // so the test runner can compute the full distribution (p50, p95, RMS)
    // and produce a CDF plot. A typical scenario produces ~500 samples per
    // smoother per cell (≈8 s × 60 fps) so the in-memory footprint stays
    // small. Cleared by <see cref="ResetSmoothnessAccumulator"/>.
    private readonly System.Collections.Generic.List<float> _smoothnessDvSquared = new();

    // M15 — freeze-frame ratio. Per-render-frame counter pair: the body
    // moved at least <see cref="FreezeBodyMotionThresholdM"/> since the
    // previous render frame (the denominator condition: "the body is in
    // motion this frame") but the rendered visual moved less than
    // <see cref="FreezeVisMotionThresholdM"/> (the freeze signature: "the
    // visual stayed put while the body kept going"). Reported as
    // _freezeFrames / _motionFrames at summary time. Captures the
    // AbsorbBodyTeleport-pinned-prev/curr saw-tooth which M14 only partially
    // shows — M14 sees the catch-up jerk after the freeze; this counter
    // sees the freeze itself.
    private long _freezeFrames;
    private long _motionFrames;
    private Vector3 _lastBodyPosForFreezeCheck;
    private bool _hasLastBodyPosForFreezeCheck;
    // Thresholds tuned from the user-reported S7-C0 push trace where freezes
    // were unambiguous at body-delta > 5 cm / vis-delta < 1 mm. 1 cm body
    // delta is loose enough to capture slow-push frames; 1 mm vis delta is
    // tight enough that float-precision residue from the lerp doesn't
    // count as motion.
    private const float FreezeBodyMotionThresholdM = 0.01f;
    private const float FreezeVisMotionThresholdM  = 0.001f;

    // M16 — visual-vs-body phase lag during sustained motion. Each render
    // frame where |body velocity| > <see cref="PhaseLagMinSpeedMps"/> the
    // smoother appends <c>|visRendered − bodyRaw|</c> to this list. Distinct
    // from M5 (server vs client *body* drift); this is purely client-side
    // "the visual is trailing the rigidbody". Summary reports mean + p95
    // over the combined cross-smoother pool — the C0 push trace showed
    // mean 21 cm / peak 38 cm, which is perceptible lag that M14 doesn't
    // measure at all (M14 catches jerk, not steady-state offset).
    private readonly System.Collections.Generic.List<float> _phaseLagSamples = new();
    private const float PhaseLagMinSpeedMps = 1.0f;
    // Squared form for the comparison in the hot path — avoids a sqrt per
    // _Process call when the body is stationary.
    private const float PhaseLagMinSpeedMpsSquared = PhaseLagMinSpeedMps * PhaseLagMinSpeedMps;

    // M17 — render pacing gap ratio. Each render frame the smoother diffs
    // ClientNetworkClock.GetCurrentTick() against the value from the previous
    // render frame. Gap ≥3 physics ticks means the engine ran 3+ physics
    // catch-up steps between this _Process call and the last one (i.e. one
    // or more physics ticks fired without an intervening render). Captures
    // the global "all visuals freeze together" pattern caused by heavy
    // network-callback bursts (rollback/resim) inside _PhysicsProcess.
    // Reported as _renderGapCount / _renderFrameCount at summary time.
    private long _renderGapCount;
    private long _renderFrameCount;
    private int _lastClientTickForGapCheck = -1;
    private const int RenderGapPhysicsTicksThreshold = 3;

    // M18 — visual-vs-body direction mismatch ratio. Render frames where
    // both the visual is moving (|visVel| > min) AND the body is moving
    // (|bodyVel| > min) but their velocity vectors point in opposite
    // half-spaces (dot < 0). Captures the "visual is catching up backwards"
    // artefact when offset decay overshoots — visible to the user as
    // a momentary backwards-pull on an already-moving body. Reported as
    // _dirMismatchFrames / _motionPairFrames at summary time.
    private long _dirMismatchFrames;
    private long _motionPairFrames;
    private const float DirMismatchMinSpeedMps = 0.1f;
    private const float DirMismatchMinSpeedMpsSquared = DirMismatchMinSpeedMps * DirMismatchMinSpeedMps;

    // M19 — first-person camera jolt. Per-render-frame |Δv|² of the camera's
    // FTI-interpolated world position. Mirrors M14's accumulator shape so
    // RMS / p50 / p95 / p99 can be reported the same way. Sampled only when
    // Camera is wired; non-null only on the local player's smoother.
    // Reading Camera.GetGlobalTransformInterpolated() rather than
    // Camera.GlobalPosition matters: the camera is FTI'd through its parent
    // body's prev/curr cache, and the interpolated value is what the
    // renderer actually shows the player's eye that frame. A raw read
    // would miss the rollback-reset jolt that ResetBodyFtiAfterResim
    // collapses the prev→curr lerp across.
    private readonly System.Collections.Generic.List<float> _cameraJoltDvSquared = new();
    private Vector3 _lastCameraPos;
    private Vector3 _lastCameraVel;
    private bool _hasLastCameraPos;
    private bool _hasLastCameraVel;

    public override void _EnterTree()
    {
        // Explicit enable — override of _PhysicsProcess does not always auto-
        // enable physics processing for Node3D nodes added programmatically.
        SetPhysicsProcess(true);
        SetProcess(true);

        // Detach the Visual's transform from the body's. The smoother is the
        // sole writer of Visual.GlobalPosition (and GlobalTransform when
        // SmoothRotation=true); keeping Visual as a non-top-level child would
        // mean Godot multiplies the body's transform into the visual's world
        // pose every frame, dragging the visual with any body warp that
        // happens between our writes (most notably the rollback's reconcile
        // → resim chain, which can move the body 0.5+ m mid-frame from a
        // network callback). top_level=true keeps Visual a child in the scene
        // tree (for organisation + lifecycle) but treats its local transform
        // as its world transform for rendering purposes.
        if (Visual != null)
            Visual.TopLevel = true;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (Body == null || Visual == null) return;

        // Snapshot the body's state at the START of this _PhysicsProcess
        // BEFORE any other component (other autoloads, network callbacks,
        // input handlers) runs. Godot calls _PhysicsProcess before stepping
        // the engine, so these are the body's "post-last-step" values — the
        // outcome of the integration we're about to attribute to either
        // natural motion or an unexplained jump.
        Vector3 bodyPos = Body.GlobalPosition;
        Quaternion bodyRot = Body.Quaternion;
        Vector3 linVel = Body is RigidBody3D rb1 ? rb1.LinearVelocity : Vector3.Zero;
        Vector3 angVel = Body is RigidBody3D rb2 ? rb2.AngularVelocity : Vector3.Zero;
        float dt = (float)delta;

        if (_hasPrev && dt > 0f)
        {
            // CaptureUnexplainedJump compares (bodyPos − _prevBodyPos) — the
            // delta the engine integrated during the LAST step — against the
            // motion implied by the velocity that was current at the START of
            // that step (= _prevLinVel, snapshotted at the previous _PhysicsProcess).
            // Using the post-step velocity instead would re-introduce a few
            // mm-per-tick of fictitious jump (damping, contact response, etc.)
            // that the smoother would then chase forever — see _prevLinVel docstring.
            CaptureUnexplainedJump(bodyPos, bodyRot, dt);
        }

        // Position offset decay — strategy controlled by PositionDecayMode.
        // Exponential is the legacy default; Linear matches Q3 / UE's Linear
        // mode and is the recommended choice when reconciles chain (see the
        // PosDecayMode docstrings). Rotation still uses the exponential slerp
        // because rotation captures are always reset to identity in
        // AbsorbBodyTeleport — only the offset->Identity decay rate of any
        // residual rotation between absorbs is being controlled here.
        if (PositionDecayMode == PosDecayMode.Linear)
        {
            _smoothingElapsed += dt;
            if (SmoothingTime > 0f)
            {
                float t = Mathf.Clamp(_smoothingElapsed / SmoothingTime, 0f, 1f);
                _posOffset = _originalPosOffset * (1f - t);
            }
            else
            {
                _posOffset = Vector3.Zero;
            }
        }
        else if (PositionDecayMode == PosDecayMode.MagnitudeAdaptive)
        {
            // Fusion 2-style adaptive rate. See PosDecayMode.MagnitudeAdaptive
            // docstring for the reference algorithm. Operates on _posOffset
            // directly (no baseline-and-elapsed state needed) — chained
            // reconciles just see whatever _posOffset they happen to leave
            // behind and the magnitude-blended rate sorts itself out.
            float mag = _posOffset.Length();
            if (mag > 1e-6f)
            {
                float blendRange = MagnitudeAdaptiveBlendEnd - MagnitudeAdaptiveBlendStart;
                float t = blendRange > 1e-6f
                    ? Mathf.Clamp((mag - MagnitudeAdaptiveBlendStart) / blendRange, 0f, 1f)
                    : (mag >= MagnitudeAdaptiveBlendEnd ? 1f : 0f);
                float rate = Mathf.Lerp(MagnitudeAdaptiveMinRate, MagnitudeAdaptiveMaxRate, t);
                // Per-frame correction magnitude with the anti-freeze floor.
                // The floor is the entire point of this mode — without it
                // we degenerate to exponential and reproduce the freeze.
                float step = Mathf.Max(mag * rate * dt, MagnitudeAdaptiveMinCorrection);
                step = Mathf.Min(step, mag);  // never overshoot zero
                _posOffset -= _posOffset / mag * step;
            }
        }
        else
        {
            float alpha = DecayTime > 0f ? Mathf.Exp(-dt / DecayTime) : 0f;
            _posOffset *= alpha;
        }
        {
            float rotAlpha = DecayTime > 0f ? Mathf.Exp(-dt / DecayTime) : 0f;
            _rotOffset = Quaternion.Identity.Slerp(_rotOffset.Normalized(), rotAlpha);
        }

        if (TeleportDistance > 0f && _posOffset.Length() > TeleportDistance)
        {
            _posOffset = Vector3.Zero;
            _originalPosOffset = Vector3.Zero;
            _smoothingElapsed = 0f;
            _rotOffset = Quaternion.Identity;
        }

        // Past-interpolation lerp buffer — matches Godot's built-in physics
        // interpolation behaviour for noisy motion. prev = body pose at end
        // of step N-2; curr = body pose at end of step N-1 (the pose we
        // just observed at the start of this _PhysicsProcess call). The
        // renderer lerps between two ALREADY-OBSERVED positions over the
        // wall-clock interval covering physics step N — so the rendered
        // visual is always ~1 physics tick behind the body's "true" current
        // state but is GUARANTEED smooth: it never extrapolates and so never
        // overshoots when velocity changes abruptly (slope contact noise,
        // reconciles, jumps).
        //
        // Tried extrapolation (prev = body NOW, curr = body + linVel*dt) to
        // remove the 1-tick lag, but it amplified per-tick velocity noise
        // visible as micro-shudder during slope walking — the renderer was
        // chasing a different "predicted" position every tick as the contact
        // normal shifted the integrated velocity by mm/tick. The smooth
        // rendering of past-interpolation is preferred over the 1-tick
        // responsiveness gain of extrapolation. Visual sitting BEHIND the
        // body is acceptable; visual AHEAD or shuddering is not.
        Vector3 newTarget = bodyPos + _posOffset;
        Quaternion newTargetRot = (_rotOffset * bodyRot).Normalized();
        if (_hasTarget)
        {
            _visTargetPrev = _visTargetCurr;
            _visTargetRotPrev = _visTargetRotCurr;
        }
        else
        {
            _visTargetPrev = newTarget;
            _visTargetRotPrev = newTargetRot;
        }
        _visTargetCurr = newTarget;
        _visTargetRotCurr = newTargetRot;
        _hasTarget = true;

        // Save this tick's start-of-step pose AND velocity for the next
        // _PhysicsProcess's CaptureUnexplainedJump. The velocity is the
        // critical bit: next tick we'll compare (bodyPos − _prevBodyPos)
        // against _prevLinVel*dt — the motion that THIS tick's velocity
        // was about to integrate.
        _prevBodyPos = bodyPos;
        _prevBodyRot = bodyRot;
        _prevLinVel = linVel;
        _prevAngVel = angVel;
        _hasPrev = true;
    }

    public override void _Process(double delta)
    {
        if (Body == null || Visual == null || !_hasTarget) return;

        float pif = (float)Engine.GetPhysicsInterpolationFraction();
        Vector3 visPos;
        Quaternion visRot;
        if (_pvbActive)
        {
            // Projective Velocity Blending overrides the past-interp lerp
            // for the duration of the blend window. P_old continues the
            // pre-snap visual trajectory at oldVel; P_new continues the
            // post-snap auth trajectory at newVel; blendFactor walks 0→1
            // linearly so the rendered Δv per frame is roughly constant at
            // |newVel − oldVel|/duration + |oldVel|, independent of the
            // (possibly multi-metre) position correction.
            _pvbElapsed += (float)delta;
            float t = _pvbElapsed;
            float blendFactor = Mathf.Clamp(t / _pvbDuration, 0f, 1f);
            Vector3 pOld = _pvbOldPos + _pvbOldVel * t;
            Vector3 pNew = _pvbNewPos + _pvbNewVel * t;
            visPos = pOld.Lerp(pNew, blendFactor);
            // Canonical PVB-rotation pairing: single-target slerp from the
            // visual rotation captured at PVB start toward the body's current
            // authoritative rotation, weighted by an aggression-scaled
            // blendFactor. Murphy's chapter prescribes "slerp the rendered
            // orientation toward the projected target using the same
            // blendFactor" — the body's quaternion IS that projected target
            // because reconciles teleport the body to the auth rotation and
            // Jolt integrates angular velocity between snapshots, so
            // body.Quaternion at this instant is the best-known orientation
            // estimate. RotationBlendAggression scales blendFactor so the
            // rotation slerp catches up before the (slower) position blend
            // finishes — e.g. aggression=3 reaches body.Quaternion at 1/3 of
            // the position window. Chained PVB restarts re-anchor
            // _pvbStartRot to the in-flight visual rotation so the slerp
            // continues smoothly from the current visual rather than warping
            // back to the original anchor.
            float rotBlend = Mathf.Clamp(blendFactor * RotationBlendAggression, 0f, 1f);
            visRot = _pvbStartRot.Slerp(Body is Node3D bN ? bN.Quaternion : Quaternion.Identity, rotBlend);
            if (blendFactor >= 1f)
            {
                _pvbActive = false;
                // Re-pin the lerp buffer + offset to the blended endpoint so
                // the next physics tick resumes normal smoother behaviour
                // from where the PVB left off rather than from whatever the
                // parallel _PhysicsProcess updates left in the buffer while
                // we were overriding it.
                _visTargetPrev = visPos;
                _visTargetCurr = visPos;
                _visTargetRotPrev = visRot;
                _visTargetRotCurr = visRot;
                _posOffset = visPos - Body.GlobalPosition;
                _rotOffset = Quaternion.Identity;
                MonkeLogger.Debug($"[SMOOTH-PVB-END] body={Body.Name} endVis=({visPos.X:F3},{visPos.Y:F3},{visPos.Z:F3}) bodyPos=({Body.GlobalPosition.X:F3},{Body.GlobalPosition.Y:F3},{Body.GlobalPosition.Z:F3}) residualOffset=({_posOffset.X:F3},{_posOffset.Y:F3},{_posOffset.Z:F3})");
            }
        }
        else
        {
            visPos = _visTargetPrev.Lerp(_visTargetCurr, pif);
            visRot = _visTargetRotPrev.Slerp(_visTargetRotCurr, pif);
        }

        // Visual smoothness sample. Computed BEFORE we overwrite
        // _lastRenderedPos so we have last-frame's rendered pos available.
        // currVel = how fast the visual is moving between the last frame and
        // this one. Δv = how much the per-frame velocity changed from the
        // previous render frame — large Δv means the visual jerked (snap-
        // overflow, frame-rate spike, smoother offset reset). The harness
        // reads RMS(|Δv|) as the M14 metric.
        Vector3 bodyPosNowM = Body.GlobalPosition;
        if (delta > 0 && _hasLastRenderedPos)
        {
            Vector3 currVisVel = (visPos - _lastRenderedPos) / (float)delta;
            if (_hasLastVisVel)
            {
                Vector3 dv = currVisVel - _lastVisVel;
                _smoothnessDvSquared.Add(dv.LengthSquared());
            }
            _lastVisVel = currVisVel;
            _hasLastVisVel = true;

            // M18 — visual-vs-body direction mismatch. Only count frames
            // where BOTH the visual and the body are meaningfully moving
            // (above DirMismatchMinSpeed); otherwise tiny float-noise
            // velocity vectors would flip signs randomly and inflate the
            // ratio. Both must agree on a direction for the metric to
            // describe a real disagreement.
            Vector3 bodyVelDir = Body is RigidBody3D rbDir ? rbDir.LinearVelocity : Vector3.Zero;
            if (currVisVel.LengthSquared() > DirMismatchMinSpeedMpsSquared
                && bodyVelDir.LengthSquared() > DirMismatchMinSpeedMpsSquared)
            {
                _motionPairFrames++;
                if (currVisVel.Dot(bodyVelDir) < 0f) _dirMismatchFrames++;
            }
        }

        // M15 — freeze-frame ratio. Body delta vs visual delta this frame.
        // Tracked using _lastBodyPosForFreezeCheck (sampled at the START of
        // each _Process so it lines up with _lastRenderedPos which was
        // sampled at the END of the previous _Process — i.e. the same
        // wall-clock interval covers both deltas).
        if (_hasLastBodyPosForFreezeCheck && _hasLastRenderedPos)
        {
            float bodyDeltaMag = (bodyPosNowM - _lastBodyPosForFreezeCheck).Length();
            float visDeltaMag  = (visPos      - _lastRenderedPos).Length();
            if (bodyDeltaMag > FreezeBodyMotionThresholdM)
            {
                _motionFrames++;
                if (visDeltaMag < FreezeVisMotionThresholdM) _freezeFrames++;
            }
        }
        _lastBodyPosForFreezeCheck = bodyPosNowM;
        _hasLastBodyPosForFreezeCheck = true;

        // M16 — phase lag sample. Only collected while the body is in
        // sustained motion — at-rest frames have zero lag by definition and
        // would bias the mean downward. PhaseLagMinSpeed = 1 m/s is well
        // above Jolt micro-twitch but well below a player walk (~3 m/s) or
        // a kicked cube's mid-flight velocity (~3-5 m/s).
        Vector3 bodyVelLag = Body is RigidBody3D rbLag ? rbLag.LinearVelocity : Vector3.Zero;
        if (bodyVelLag.LengthSquared() > PhaseLagMinSpeedMpsSquared)
        {
            _phaseLagSamples.Add((visPos - bodyPosNowM).Length());
        }

        // M17 — render pacing gap. ClientNetworkClock tick advances 1 per
        // physics tick; if two consecutive _Process calls observe a gap of
        // ≥3 ticks, the engine ran 3+ catch-up physics steps between them
        // (and Godot's one-slot FTI history dropped all intermediate poses).
        int currentClientTick = MonkeNet.Client.ClientManager.Instance?
            .GetNodeOrNull<MonkeNet.Client.ClientNetworkClock>("ClientNetworkClock")?
            .GetCurrentTick() ?? -1;
        if (currentClientTick >= 0)
        {
            if (_lastClientTickForGapCheck >= 0)
            {
                int gap = currentClientTick - _lastClientTickForGapCheck;
                _renderFrameCount++;
                if (gap >= RenderGapPhysicsTicksThreshold) _renderGapCount++;
            }
            _lastClientTickForGapCheck = currentClientTick;
        }

        if (SmoothRotation)
        {
            Visual.GlobalTransform = new Transform3D(new Basis(visRot), visPos);
            _lastRenderedRot = visRot;
        }
        else
        {
            Visual.GlobalPosition = visPos;
            // Caller still owns Visual.Rotation in this branch — log whatever
            // they last set so SMOOTH-FRAME reports the actual rendered pose
            // rather than an out-of-date smoother-side rotation.
            _lastRenderedRot = Visual.Quaternion;
        }
        _lastRenderedPos = visPos;
        _hasLastRenderedPos = true;

        // M19 — sample the camera's rendered position, compute its per-frame
        // velocity and Δv. Mirrors the M14 calc on _lastRenderedPos but
        // applied to a different node — the one the player's eye attaches
        // to. GlobalTransformInterpolated returns the FTI-cached pose the
        // renderer is using for this frame (= what the player sees), which
        // differs from Camera.GlobalPosition during the rollback frame
        // where ResetBodyFtiAfterResim collapses prev→curr.
        if (Camera != null && delta > 0)
        {
            Vector3 camPos = Camera.GetGlobalTransformInterpolated().Origin;
            if (_hasLastCameraPos)
            {
                Vector3 currCamVel = (camPos - _lastCameraPos) / (float)delta;
                if (_hasLastCameraVel)
                {
                    Vector3 dvCam = currCamVel - _lastCameraVel;
                    _cameraJoltDvSquared.Add(dvCam.LengthSquared());
                }
                _lastCameraVel = currCamVel;
                _hasLastCameraVel = true;
            }
            _lastCameraPos = camPos;
            _hasLastCameraPos = true;
        }

        LogSmoothFrame(pif, delta);
    }

    /// <summary>Clear the visual-smoothness accumulator. Called by the
    /// harness via the "visual-smoothness-reset" command at the start of
    /// the scenario's observation window (typically after scenario.Setup) so
    /// the warm-up phase doesn't bias the M14 measurement.</summary>
    public void ResetSmoothnessAccumulator()
    {
        _lastVisVel = Vector3.Zero;
        _hasLastVisVel = false;
        // Don't clear _hasLastRenderedPos — at the point this is called,
        // we've already been rendering for ~5 s of clock-sync + spawn-burst,
        // so _lastRenderedPos is a valid recent value. Next frame's
        // currVisVel = (visPos - _lastRenderedPos) / delta is the velocity
        // ACROSS the reset boundary; we just discard its Δv (because
        // _hasLastVisVel is false) and start fresh from frame 2.
        _smoothnessDvSquared.Clear();
        // M15 / M16 / M17 / M18 — same reset semantics. Each metric needs
        // its "first sample post-reset" boundary discarded (the body-delta
        // / clientTick / velocity diff across the reset is meaningless).
        _freezeFrames = 0;
        _motionFrames = 0;
        _hasLastBodyPosForFreezeCheck = false;
        _phaseLagSamples.Clear();
        _renderGapCount = 0;
        _renderFrameCount = 0;
        _lastClientTickForGapCheck = -1;
        _dirMismatchFrames = 0;
        _motionPairFrames = 0;
        _cameraJoltDvSquared.Clear();
        _hasLastCameraPos = false;
        _hasLastCameraVel = false;
    }

    /// <summary>M15 — number of render frames where the body moved
    /// &gt;<see cref="FreezeBodyMotionThresholdM"/> but the visual moved
    /// &lt;<see cref="FreezeVisMotionThresholdM"/>. Use with
    /// <see cref="MotionFrames"/> as the denominator to compute the
    /// freeze-frame ratio.</summary>
    public long FreezeFrames => _freezeFrames;

    /// <summary>M15 denominator — number of render frames where the body
    /// moved &gt;<see cref="FreezeBodyMotionThresholdM"/> (i.e. the body
    /// is in motion this frame, so we can meaningfully ask whether the
    /// visual was frozen).</summary>
    public long MotionFrames => _motionFrames;

    /// <summary>M16 — per-render-frame |visRendered − bodyRaw| samples (m),
    /// collected only while the body's speed exceeds
    /// <see cref="PhaseLagMinSpeedMps"/>. Summary computes mean + p95 over
    /// the combined pool across all smoothers.</summary>
    public System.Collections.Generic.IReadOnlyList<float> PhaseLagSamples => _phaseLagSamples;

    /// <summary>M17 — number of render frames whose ClientNetworkClock tick
    /// advanced ≥<see cref="RenderGapPhysicsTicksThreshold"/> since the
    /// previous render frame (i.e. the engine ran multiple physics catch-up
    /// steps between renders, dropping intermediate FTI poses). Use with
    /// <see cref="RenderFrameCount"/> as the denominator.</summary>
    public long RenderPacingGapCount => _renderGapCount;

    /// <summary>M17 denominator — number of render frames the smoother has
    /// observed since the last <see cref="ResetSmoothnessAccumulator"/>.</summary>
    public long RenderFrameCount => _renderFrameCount;

    /// <summary>M18 — number of render frames where visual velocity and
    /// body velocity disagreed on direction (dot &lt; 0), both above the
    /// noise floor <see cref="DirMismatchMinSpeedMps"/>. Use with
    /// <see cref="MotionPairFrames"/> as the denominator.</summary>
    public long DirectionMismatchFrames => _dirMismatchFrames;

    /// <summary>M18 denominator — render frames where BOTH the visual and
    /// the body were meaningfully moving (above the noise floor on each
    /// side), so the dot-product comparison is well-defined.</summary>
    public long MotionPairFrames => _motionPairFrames;

    /// <summary>M19 — raw |Δv|² samples of the first-person camera's
    /// rendered (FTI-interpolated) world position. Empty when
    /// <see cref="Camera"/> is null. Aggregated by the harness alongside
    /// M14; the runner reports RMS / p50 / p95 / p99 in m/s.</summary>
    public System.Collections.Generic.IReadOnlyList<float> CameraJoltDvSquaredSamples
        => _cameraJoltDvSquared;

    /// <summary>Raw |Δv|² samples (units (m/s)²) accumulated since the last
    /// reset, one per render frame. Exposed for the harness's
    /// "visual-smoothness" RPC command — the test runner ships the full
    /// distribution to <see cref="MonkeNet.Tests.Infrastructure.Metrics.SyncMetrics"/>
    /// which computes RMS, p50, and p95 from the combined pool across
    /// every smoother on the client.</summary>
    public System.Collections.Generic.IReadOnlyList<float> SmoothnessDvSquaredSamples => _smoothnessDvSquared;

    // Per-render-frame smoothness trace. Logs the actual rendered position
    // (the value we just wrote to Visual.GlobalPosition this _Process call),
    // not Visual.GetGlobalTransformInterpolated() — that returns the
    // SceneTreeFTI cached value from the previous frame_update, which can
    // be stale by exactly the window this smoother is now closing.
    private bool _loggedFtiState;

    private void LogSmoothFrame(float pif, double delta)
    {
        if (!_loggedFtiState)
        {
            _loggedFtiState = true;
            var tree = GetTree();
            bool treeInterp = tree != null && tree.IsPhysicsInterpolationEnabled();
            string stateMsg = $"[SMOOTH-FTI-STATE] body={Body.Name} treeInterp={treeInterp} " +
                $"bodyInterpMode={Body.PhysicsInterpolationMode} " +
                $"visInterpMode={Visual.PhysicsInterpolationMode} " +
                $"physTicksPerSec={Engine.PhysicsTicksPerSecond} maxFps={Engine.MaxFps} " +
                $"visIsTopLevel={Visual.TopLevel} libraryManaged=True";
            MonkeLogger.Debug(stateMsg);
            GD.Print(stateMsg);
        }

        Vector3 bodyRaw = Body.GlobalPosition;
        Quaternion bodyRotRaw = Body is Node3D bN ? bN.Quaternion : Quaternion.Identity;
        Vector3 vel = Body is RigidBody3D rb ? rb.LinearVelocity : Vector3.Zero;
        ulong physFrame = Engine.GetPhysicsFrames();
        int clientTick = MonkeNet.Client.ClientManager.Instance?
            .GetNodeOrNull<MonkeNet.Client.ClientNetworkClock>("ClientNetworkClock")?
            .GetCurrentTick() ?? -1;

        // visRendered IS what's on screen this frame — the smoother just
        // wrote Visual.GlobalTransform / GlobalPosition to (visPos, visRot).
        // bodyRendered for a body with FTI off (the recommended config for
        // the body when paired with this smoother) is the same as raw.
        //
        // ForceDebug bypasses MonkeLogger.DebugEnabled so this single render-
        // frame marker survives a "disable debug logging" run — it's how the
        // log-analysis tools (gap detection, push trace) measure render
        // pacing. Disabling DebugEnabled globally kills the other ~thousand
        // log lines per second this smoother used to produce; keeping just
        // SMOOTH-FRAME (one per render frame per body) gives us enough
        // signal to detect engine-tick catch-up bursts without re-paying
        // the per-call log cost on the hot reconcile path.
        MonkeLogger.ForceDebug(
            $"[SMOOTH-FRAME] body={Body.Name} pf={physFrame} clientTick={clientTick} " +
            $"dt={delta:F5} pif={pif:F3} " +
            $"bodyRaw=({bodyRaw.X:F5},{bodyRaw.Y:F5},{bodyRaw.Z:F5}) " +
            $"bodyRot=({bodyRotRaw.X:F4},{bodyRotRaw.Y:F4},{bodyRotRaw.Z:F4},{bodyRotRaw.W:F4}) " +
            $"visRendered=({_lastRenderedPos.X:F5},{_lastRenderedPos.Y:F5},{_lastRenderedPos.Z:F5}) " +
            $"visRot=({_lastRenderedRot.X:F4},{_lastRenderedRot.Y:F4},{_lastRenderedRot.Z:F4},{_lastRenderedRot.W:F4}) " +
            $"targetPrev=({_visTargetPrev.X:F5},{_visTargetPrev.Y:F5},{_visTargetPrev.Z:F5}) " +
            $"targetCurr=({_visTargetCurr.X:F5},{_visTargetCurr.Y:F5},{_visTargetCurr.Z:F5}) " +
            $"posOffset=({_posOffset.X:F5},{_posOffset.Y:F5},{_posOffset.Z:F5}) " +
            $"vel=({vel.X:F4},{vel.Y:F4},{vel.Z:F4})");
    }

    // Diff actual body motion against the motion implied by the velocity at
    // the START of the just-completed step (= _prevLinVel / _prevAngVel,
    // snapshotted at the previous _PhysicsProcess). Excess is folded into the
    // offsets so the visual stays at its rendered pose across whatever
    // produced the jump (Reconcile, SyncSleepState, etc.).
    //
    // Why pre-step velocity, not post-step Body.LinearVelocity: Jolt's
    // semi-implicit Euler integrates as
    //   vel_after = vel_before + accel*dt - damping*vel_before*dt + contacts
    //   pos_after = pos_before + vel_after * dt
    // The motion we just measured (bodyPos − _prevBodyPos) is pos_after −
    // pos_before = vel_after * dt. Reading Body.LinearVelocity at THIS
    // _PhysicsProcess gives vel_after, so naively using vel_after*dt for
    // the expected delta cancels out and reports zero jump — except when
    // contact-driven velocity changes happen the math becomes brittle and
    // the post-step velocity is the wrong reference for the change. Using
    // the pre-step velocity (= _prevLinVel, what the previous
    // _PhysicsProcess saw) gives a clean "how much of this motion would I
    // have predicted from the inputs the engine had?" baseline. Residual
    // accumulates only when the engine applied a real impulse / teleport.
    private void CaptureUnexplainedJump(Vector3 bodyPos, Quaternion bodyRot, float dt)
    {
        // Kinematic-body bail-out (T2 KinematicInterpolation policy support).
        // When Body.Freeze=true, LocalRigidPropPrediction.OnPostPhysicsTick
        // writes the body's transform every tick to track the snapshot-interp
        // pose. Pre-step velocity is 0 (kinematic bodies don't accumulate
        // velocity), so each per-tick transform write would count here as
        // an "unexplained jump" and accumulate into _posOffset (observed
        // pre-fix on this branch: 1-5 m/s induced visual jerk per tick on
        // KI-mode cubes). For a kinematic body the explicit
        // AbsorbBodyTeleport calls from LocalRigidPropPrediction's teleport-
        // detection path are the only legitimate offset captures — the
        // per-tick smooth motion already follows the body naturally
        // because the smoother's lerp targets observe it without needing
        // an offset to track.
        if (Body is RigidBody3D rbFreeze && rbFreeze.Freeze) return;

        Vector3 expectedPosDelta = _prevLinVel * dt;
        Vector3 jumpPos = (bodyPos - _prevBodyPos) - expectedPosDelta;
        if (jumpPos.LengthSquared() > PositionJumpEpsilon * PositionJumpEpsilon)
        {
            _posOffset -= jumpPos;
            RebaseLinearDecay();
        }

        Quaternion expectedRot = AngularDelta(_prevAngVel, dt);
        Quaternion actualRot = (bodyRot * _prevBodyRot.Inverse()).Normalized();
        Quaternion jumpRot = (expectedRot.Inverse() * actualRot).Normalized();
        float jumpAngle = 2f * Mathf.Acos(Mathf.Clamp(Mathf.Abs(jumpRot.W), -1f, 1f));
        if (jumpAngle > RotationJumpEpsilonRad)
        {
            _rotOffset = (_rotOffset * jumpRot.Inverse()).Normalized();
        }
    }

    // Quaternion that rotates by (angVel * dt) around the angVel axis. Mirrors
    // the semi-implicit Euler step Godot/Jolt uses for free-flying bodies, so
    // for natural motion expectedRot ≈ actualRot to within float precision.
    private static Quaternion AngularDelta(Vector3 angVel, float dt)
    {
        float speed = angVel.Length();
        if (speed < 1e-6f) return Quaternion.Identity;
        Vector3 axis = angVel / speed;
        return new Quaternion(axis, speed * dt);
    }

    /// <summary>
    /// Absorb a body teleport that's about to happen / just happened in the
    /// same physics frame. Called from <see cref="PredictionRigidbody3D.Reconcile"/>
    /// after the body's transform has been written to the authoritative pose.
    ///
    /// Updates <c>_posOffset</c> so that <c>body.NEW + offset.NEW == body.OLD + offset.OLD</c>
    /// — the visual target stays at the pre-teleport pose. Does NOT touch
    /// <c>Visual.GlobalTransform</c>: that write happens in <c>_Process</c> next
    /// render frame, using the updated <c>_visTargetCurr</c>. Same-frame
    /// readers between this call and the next render frame will see the visual
    /// at its last-rendered position (preserved by the smoother's
    /// <c>_lastRenderedPos</c>) — the body has warped but the visual hasn't
    /// caught up yet, which is the correct ordering.
    ///
    /// Rotation is intentionally NOT captured into <c>_rotOffset</c>. The
    /// Interpolate-tier per-snapshot blend (HandleInterpolateReconciliation's
    /// 3-tick BLEND-START path) calls this every ~3 ticks per cube under
    /// chained snapshots, and the captured rotation offset is the inverse of
    /// the body's per-blend-tick rotation step. Each new absorb refreshes
    /// that counter-rotation before the per-frame slerp decay
    /// (DecayTime=0.1s ≈ 6 ticks) drains the previous one — the offset
    /// stabilises at the value that exactly cancels the body's rotation in
    /// <c>newTargetRot = _rotOffset * bodyRot</c>, and the visual freezes
    /// at a stale orientation for the full duration of the snapshot stream.
    /// Observed on S7-C3 eid=37 around tick 362: body teleported through
    /// ~180° of yaw via blend writes while the visual held at +0° for
    /// 9 ticks (~150 ms) and then snap-caught-up over 1 frame. Position
    /// offset has the same accumulation shape but the equilibrium is masked
    /// by snap-to-auth's <c>ClearOffset</c> calls (~40% of frames under C4)
    /// — rotation has no such clear hook. Resetting <c>_rotOffset</c> to
    /// identity here matches the established "no rotation smoothing across
    /// reconciles" design (<see cref="FixupOffsetAfterResim"/> docstring):
    /// visual rotation tracks the body's actual orientation 1:1, accepting
    /// a one-frame snap on the rare genuine teleport in exchange for
    /// faithful rotation rendering during the common contact / blend case.
    /// </summary>
    public void AbsorbBodyTeleport(Vector3 prePos, Quaternion preRot, Vector3 postPos, Quaternion postRot)
    {
        if (Body == null || Visual == null) return;

        Vector3 jump = postPos - prePos;
        _posOffset -= jump;
        _rotOffset = Quaternion.Identity;

        if (TeleportDistance > 0f && _posOffset.Length() > TeleportDistance)
        {
            _posOffset = Vector3.Zero;
        }
        // Rebase the linear-decay baseline AFTER the teleport-distance snap
        // so the residual+new sum (Q3 style) AND the post-snap zero case
        // both restart the ramp from the correct value.
        RebaseLinearDecay();

        _prevBodyPos = postPos;
        _prevBodyRot = postRot;
        // Refresh the saved pre-step velocity to the post-teleport velocity.
        // The next CaptureUnexplainedJump will use this as the baseline for
        // motion the next step is about to integrate — using the pre-teleport
        // velocity would falsely flag the entire next-step delta as an
        // unexplained jump and double-absorb the offset we just set.
        _prevLinVel = Body is RigidBody3D rbL ? rbL.LinearVelocity : Vector3.Zero;
        _prevAngVel = Body is RigidBody3D rbA ? rbA.AngularVelocity : Vector3.Zero;
        _hasPrev = true;

        // Pin only the CURR end of the past-interpolation lerp to the
        // post-teleport visual position (= body.NEW + offset.NEW = body.OLD
        // + offset.OLD by construction — same as the visual was being rendered
        // at pre-teleport). Leaving _visTargetPrev at its previous value (the
        // body pose from the last _PhysicsProcess) lets the subsequent render
        // frames lerp from "where the visual just was" toward pinnedVisual
        // smoothly across pif=[0..1], so the visual continues to slide
        // forward at the body's effective velocity rather than freezing for
        // one render frame.
        //
        // Pinning BOTH ends (the previous behaviour) wrote the same value
        // into prev and curr, which collapsed the lerp to a constant for the
        // remainder of the current tick's render window — visual frozen until
        // next _PhysicsProcess rotates prev=curr and recomputes curr from
        // the new body pose. Under continuous-push conditions (S7-C0 eid=14
        // tick 600-690, body travelling at ~3.4 m/s, snapshot rate 20 Hz)
        // this produced a freeze-then-jump pattern every ~3 ticks: visDz=0
        // for one frame, visDz=2× the body's per-frame motion the next frame.
        // M14 |Δv|² metric reads that saw-tooth as ~3.4 m/s of induced visual
        // jerk on top of the real push, making C0 (no snap-to-auth, high
        // movement) the WORST-smoothness condition in the matrix despite
        // being on a clean network.
        //
        // The new behaviour preserves the "absorb the teleport" semantics —
        // pinnedVisual still equals the pre-teleport rendered position by
        // construction, so the visual doesn't lurch toward the body's new
        // pose — but lets it continue moving at the visual velocity it had
        // before the absorb, decaying into pinnedVisual smoothly.
        Vector3 pinnedVisual = postPos + _posOffset;
        Quaternion pinnedVisualRot = (_rotOffset * postRot).Normalized();
        _visTargetCurr = pinnedVisual;
        _visTargetRotCurr = pinnedVisualRot;

        MonkeLogger.Debug($"[SMOOTH-ABSORB] body={Body.Name} prePos=({prePos.X:F3},{prePos.Y:F3},{prePos.Z:F3}) postPos=({postPos.X:F3},{postPos.Y:F3},{postPos.Z:F3}) jump=({jump.X:F3},{jump.Y:F3},{jump.Z:F3}) newOffset=({_posOffset.X:F3},{_posOffset.Y:F3},{_posOffset.Z:F3})");
    }

    /// <summary>
    /// Re-anchor the smoother after a rollback+resim has moved the body from
    /// auth_pose through N replayed ticks to post_resim_pose. Called by
    /// <see cref="Client.ClientPredictionManager"/> at the end of
    /// <c>RollbackAndResimulate</c>.
    ///
    /// Sets <c>_posOffset</c> so <c>body.POST + offset == preReconcileVisualPos</c>
    /// (preserves where the visual was being rendered) and clamps the target
    /// buffer to that visual position so the next render frame draws the visual
    /// exactly where the user saw it last frame. The decay over
    /// <see cref="DecayTime"/> then carries it toward the body's actual pose
    /// over subsequent ticks.
    ///
    /// Rotation is intentionally NOT re-anchored. For bodies with small
    /// reconcile-driven rotation deltas (resting cubes, balls just settling)
    /// the decay back from a captured rotation is a visible 6-tick wobble
    /// repeated on every reconcile — more noticeable than the underlying
    /// rotation jump, which <see cref="AbsorbBodyTeleport"/> already absorbs
    /// adequately.
    /// </summary>
    public void FixupOffsetAfterResim(Vector3 preReconcileVisualPos)
    {
        if (Body == null || Visual == null) return;

        Vector3 postResimBodyPos = Body.GlobalPosition;
        Quaternion postResimBodyRot = Body.Quaternion;

        _posOffset = preReconcileVisualPos - postResimBodyPos;

        if (TeleportDistance > 0f && _posOffset.Length() > TeleportDistance)
        {
            _posOffset = Vector3.Zero;
            _rotOffset = Quaternion.Identity;
        }
        RebaseLinearDecay();

        _prevBodyPos = postResimBodyPos;
        _prevBodyRot = postResimBodyRot;
        // Same reason as AbsorbBodyTeleport: refresh the saved pre-step
        // velocity to the post-resim velocity so the next
        // CaptureUnexplainedJump compares against the correct baseline.
        _prevLinVel = Body is RigidBody3D rbL ? rbL.LinearVelocity : Vector3.Zero;
        _prevAngVel = Body is RigidBody3D rbA ? rbA.AngularVelocity : Vector3.Zero;
        _hasPrev = true;

        // Freeze both ends of the lerp at preReconcileVisualPos. The very
        // next render frame writes Visual.GlobalPosition = preVis (no
        // movement from where it was). The next _PhysicsProcess advances:
        // prev = preVis (= old curr), curr = body + decayed_offset, and the
        // visual smoothly lerps from preVis toward the body over the next
        // physics tick. The exponential offset decay carries that
        // convergence over DecayTime regardless of physics tick rate.
        _visTargetPrev = preReconcileVisualPos;
        _visTargetCurr = preReconcileVisualPos;
        _hasTarget = true;

        MonkeLogger.Debug($"[SMOOTH-FIXUP] body={Body.Name} preVis=({preReconcileVisualPos.X:F3},{preReconcileVisualPos.Y:F3},{preReconcileVisualPos.Z:F3}) postBody=({postResimBodyPos.X:F3},{postResimBodyPos.Y:F3},{postResimBodyPos.Z:F3}) newOffset=({_posOffset.X:F3},{_posOffset.Y:F3},{_posOffset.Z:F3})");
    }

    /// <summary>
    /// Reset the smoother offset to zero and pin the lerp buffer to the
    /// body's current pose. After this call, the next render frame will draw
    /// the visual AT the body's position rather than at any previously-
    /// absorbed offset; subsequent _PhysicsProcess calls then resume normal
    /// past-interpolation lerp updates.
    ///
    /// Used by the snap-to-auth-overflow path under high latency (C3/C4)
    /// where the body is teleported backward every tick to the stale auth
    /// pose. The default AbsorbBodyTeleport behaviour (capture the backward
    /// jump as a forward _posOffset, decay over DecayTime) accumulates a
    /// forward equilibrium offset of ~vel*dt/decay_per_tick (~0.55 m at
    /// 5 m/s walking) because each tick replenishes the offset faster than
    /// the exponential decay drains it — visible as the visual sitting
    /// ahead of the body. Clearing the offset after each snap-overflow
    /// forces the visual to converge to the body via the per-tick lerp
    /// instead of via the stalled exponential decay, putting the visual
    /// briefly behind the body (1 physics tick) which is the desired
    /// trade-off: less precise but never ahead.
    /// </summary>
    public void ClearOffset()
    {
        if (Body == null || Visual == null) return;
        _posOffset = Vector3.Zero;
        _rotOffset = Quaternion.Identity;
        RebaseLinearDecay();
        Vector3 bodyPos = Body.GlobalPosition;
        Quaternion bodyRot = Body.Quaternion;
        _visTargetPrev = bodyPos;
        _visTargetRotPrev = bodyRot;
        _visTargetCurr = bodyPos;
        _visTargetRotCurr = bodyRot;
        _hasTarget = true;
        _prevBodyPos = bodyPos;
        _prevBodyRot = bodyRot;
        _prevLinVel = Body is RigidBody3D rbLv ? rbLv.LinearVelocity : Vector3.Zero;
        _prevAngVel = Body is RigidBody3D rbAv ? rbAv.AngularVelocity : Vector3.Zero;
        _hasPrev = true;
    }

    /// <summary>Kick off (or restart) a Projective Velocity Blend toward the
    /// new authoritative pose. Used by the snap-overflow path in
    /// <c>ClientPredictionManager</c> where the body has just teleported by
    /// metres and exponential offset decay would produce a multi-m/s Δv on
    /// the next render frame. While the blend is active, <see cref="_Process"/>
    /// ignores the past-interp lerp buffer and writes the parallel-projection
    /// blend position to Visual.GlobalPosition instead. Subsequent snap-
    /// overflows during the blend window just restart it with the new endpoints.
    ///
    /// <para><paramref name="oldPos"/>/<paramref name="oldVel"/> seed the
    /// pre-snap trajectory (typically: the smoother's
    /// <see cref="LastRenderedPosition"/> and the body's velocity right
    /// before <c>HandleReconciliation</c> teleported it). <paramref name="newPos"/>/
    /// <paramref name="newVel"/> seed the post-snap trajectory (typically:
    /// the body's authoritative pose and velocity from the snapshot).
    /// <paramref name="durationSec"/> caps the blend window — at 250 ms a
    /// 3 m position gap blends at ~12 m/s peak which beats the unbounded
    /// exponential-decay peak by ~3× under C4 conditions while still
    /// converging fast enough to avoid visible rubber-banding.</para></summary>
    public void StartProjectiveVelocityBlend(
        Vector3 oldPos, Vector3 oldVel,
        Vector3 newPos, Vector3 newVel,
        float durationSec)
    {
        if (Body == null || Visual == null) return;
        _pvbActive = true;
        _pvbElapsed = 0f;
        _pvbDuration = Mathf.Max(0.001f, durationSec);
        _pvbOldPos = oldPos;
        _pvbOldVel = oldVel;
        _pvbNewPos = newPos;
        _pvbNewVel = newVel;
        // Anchor the rotation slerp to the visual's current rendered rotation.
        // _lastRenderedRot is whatever _Process wrote to Visual.Quaternion on
        // the previous render frame (or Identity on a cold start before any
        // frame has rendered). For a chained restart mid-blend this equals
        // the in-flight slerp output from the previous frame, so the new
        // slerp(_pvbStartRot, body.Quaternion, blendFactor=0) starts at that
        // exact rotation — no warping back to the original anchor.
        _pvbStartRot = _lastRenderedRot;
        // Zero the legacy offset state — the past-interp buffer is suspended
        // for the blend window and we'll re-derive _posOffset on completion.
        _posOffset = Vector3.Zero;
        _rotOffset = Quaternion.Identity;
        RebaseLinearDecay();
        Quaternion bodyRotSnap = Body is Node3D bN0 ? bN0.Quaternion : Quaternion.Identity;
        float rotJumpDeg = Mathf.RadToDeg(_pvbStartRot.AngleTo(bodyRotSnap));
        MonkeLogger.Debug($"[SMOOTH-PVB-START] body={Body.Name} oldPos=({oldPos.X:F3},{oldPos.Y:F3},{oldPos.Z:F3}) oldVel=({oldVel.X:F3},{oldVel.Y:F3},{oldVel.Z:F3}) newPos=({newPos.X:F3},{newPos.Y:F3},{newPos.Z:F3}) newVel=({newVel.X:F3},{newVel.Y:F3},{newVel.Z:F3}) dur={_pvbDuration:F3} startRot=({_pvbStartRot.X:F4},{_pvbStartRot.Y:F4},{_pvbStartRot.Z:F4},{_pvbStartRot.W:F4}) bodyRot=({bodyRotSnap.X:F4},{bodyRotSnap.Y:F4},{bodyRotSnap.Z:F4},{bodyRotSnap.W:F4}) rotJump={rotJumpDeg:F2}deg");
    }

    /// <summary>True while the visual is meaningfully offset from the body.</summary>
    public bool IsSmoothing =>
        _posOffset.LengthSquared() > 1e-8f
        || Mathf.Abs(_rotOffset.W) < 0.99999f;

    /// <summary>Current world-space position offset between visual and body.</summary>
    public Vector3 CurrentOffset => _posOffset;

    /// <summary>The position written to Visual.GlobalPosition by the most
    /// recent <c>_Process</c> call — the actual on-screen position. Used by
    /// <c>ClientPredictionManager.RollbackAndResimulate</c> to capture
    /// <c>preReconcileVisualPos</c> against what the user just saw on screen,
    /// rather than against an FTI-cached value that may be a frame stale.
    ///
    /// <para>Before the smoother has rendered its first frame (e.g. when a
    /// snap-overflow fires at spawn time, before the spawn-tick render),
    /// falls back to <c>Body.GlobalPosition</c> — the body IS where the
    /// visual will appear once it first renders. Without the fallback,
    /// callers see the default <c>Vector3.Zero</c> and feed it into
    /// <see cref="FixupOffsetAfterResim"/>, which sets
    /// <c>_posOffset = (0,0,0) − bodyPos</c>. For entities spawned more
    /// than <see cref="TeleportDistance"/> from the world origin (≈ every
    /// non-trivial scene) the clamp then forces <c>_posOffset</c> to zero
    /// while leaving the lerp targets pinned to <c>(0,0,0)</c> — observed
    /// as the visual mesh sticking near world origin while the body walks
    /// away, until the snap-overflow rate finally drops enough for
    /// _PhysicsProcess to update targets faster than snaps re-pin them.</para></summary>
    public Vector3 LastRenderedPosition =>
        _hasLastRenderedPos ? _lastRenderedPos
        : (Body != null ? Body.GlobalPosition : Vector3.Zero);
}
