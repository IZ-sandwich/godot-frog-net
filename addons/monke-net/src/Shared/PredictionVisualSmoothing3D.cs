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

    /// <summary>Time constant (seconds) for exponential offset decay. After
    /// DecayTime seconds the offset has decayed to ~37% of its captured value;
    /// after 3×DecayTime it is effectively zero.</summary>
    [Export] public float DecayTime { get; set; } = 0.1f;

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

    /// <summary>
    /// When true, the smoother stops capturing offsets and forces both
    /// position and rotation offsets to zero each tick. Used for entities
    /// whose body is the local truth and is never reconciled (e.g.
    /// AuthorityTransfer-owned cubes the local client is currently driving):
    /// the smoother's only contribution would be FALSE-positive
    /// CaptureUnexplainedJump events from Jolt's contact-time position
    /// corrections, which accumulate as a growing visual lag during
    /// sustained contact. Toggle via <see cref="SetMuted"/> from game-side
    /// code that knows when a body is locally authoritative.
    /// </summary>
    public bool Muted { get; private set; } = false;

    /// <summary>Toggle mute state and clear any residual offset on entry.</summary>
    public void SetMuted(bool muted)
    {
        if (muted != Muted)
        {
            MonkeLogger.Debug($"[SMOOTH-MUTE] body={Body?.Name} muted={muted} (was={Muted})");
            if (muted)
            {
                _posOffset = Vector3.Zero;
                _rotOffset = Quaternion.Identity;
            }
        }
        Muted = muted;
    }

    // World-space position offset between visual and body. Decays toward zero.
    private Vector3 _posOffset;
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

        // Muted: clamp offsets to zero, skip the jump-capture path. Pin both
        // lerp targets to the body's current pose so the past-interpolation
        // renderer draws Visual exactly at the body. Used for AT-owned cubes
        // where the local sim is the truth (see SetMuted docstring).
        if (Muted)
        {
            _posOffset = Vector3.Zero;
            _rotOffset = Quaternion.Identity;
            _visTargetPrev = bodyPos;
            _visTargetRotPrev = bodyRot;
            _visTargetCurr = bodyPos;
            _visTargetRotCurr = bodyRot;
            _prevBodyPos = bodyPos;
            _prevBodyRot = bodyRot;
            _prevLinVel = linVel;
            _prevAngVel = angVel;
            _hasPrev = true;
            return;
        }

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

        float alpha = DecayTime > 0f ? Mathf.Exp(-dt / DecayTime) : 0f;
        _posOffset *= alpha;
        _rotOffset = Quaternion.Identity.Slerp(_rotOffset.Normalized(), alpha);

        if (TeleportDistance > 0f && _posOffset.Length() > TeleportDistance)
        {
            _posOffset = Vector3.Zero;
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
            visRot = _lastRenderedRot;
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
    }

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
        Vector3 vel = Body is RigidBody3D rb ? rb.LinearVelocity : Vector3.Zero;
        ulong physFrame = Engine.GetPhysicsFrames();
        int clientTick = MonkeNet.Client.ClientManager.Instance?
            .GetNodeOrNull<MonkeNet.Client.ClientNetworkClock>("ClientNetworkClock")?
            .GetCurrentTick() ?? -1;

        // visRendered IS what's on screen this frame — the smoother just
        // wrote Visual.GlobalTransform / GlobalPosition to (visPos, visRot).
        // bodyRendered for a body with FTI off (the recommended config for
        // the body when paired with this smoother) is the same as raw.
        MonkeLogger.Debug(
            $"[SMOOTH-FRAME] body={Body.Name} pf={physFrame} clientTick={clientTick} " +
            $"dt={delta:F5} pif={pif:F3} " +
            $"bodyRaw=({bodyRaw.X:F5},{bodyRaw.Y:F5},{bodyRaw.Z:F5}) " +
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
    //
    // T2: skip kinematic bodies entirely. When Body.Freeze=true,
    // LocalRigidPropPrediction.OnPostPhysicsTick writes the body's transform
    // every tick to track the snapshot-interp pose. Pre-step velocity is 0
    // (kinematic bodies don't accumulate velocity), so each per-tick
    // transform write would count as an "unexplained jump" and accumulate
    // into _posOffset (observed pre-fix: multi-metre visual offsets after
    // a few dozen kinematic ticks). For a kinematic body the visual mesh
    // is a child of the body anyway, so it follows the body's transform
    // write naturally — no smoother offset needed. Explicit
    // AbsorbBodyTeleport calls from Reconcile still register because they
    // baseline _prevBodyPos to the post-teleport pose, so the next
    // _PhysicsProcess sees zero residual delta.
    private void CaptureUnexplainedJump(Vector3 bodyPos, Quaternion bodyRot, float dt)
    {
        if (Body is RigidBody3D rbFreeze && rbFreeze.Freeze) return;

        Vector3 expectedPosDelta = _prevLinVel * dt;
        Vector3 jumpPos = (bodyPos - _prevBodyPos) - expectedPosDelta;
        if (jumpPos.LengthSquared() > PositionJumpEpsilon * PositionJumpEpsilon)
        {
            _posOffset -= jumpPos;
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
    /// </summary>
    public void AbsorbBodyTeleport(Vector3 prePos, Quaternion preRot, Vector3 postPos, Quaternion postRot)
    {
        if (Body == null || Visual == null) return;

        Vector3 jump = postPos - prePos;
        _posOffset -= jump;
        Quaternion rotJump = (postRot * preRot.Inverse()).Normalized();
        _rotOffset = (_rotOffset * rotJump.Inverse()).Normalized();

        if (TeleportDistance > 0f && _posOffset.Length() > TeleportDistance)
        {
            _posOffset = Vector3.Zero;
            _rotOffset = Quaternion.Identity;
        }

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

        // Pin both lerp endpoints to the post-teleport visual position
        // (= body.NEW + offset.NEW = body.OLD + offset.OLD by construction
        // — same as the visual was being rendered at pre-teleport). With
        // past-interpolation, prev and curr are both observed body positions;
        // right after a teleport we don't have a "previous frame" body
        // position that's safe to lerp against (the body just jumped), so
        // we freeze both ends to the absorbed visual position. The next
        // _PhysicsProcess will set prev = this frozen value and curr =
        // body's new observed pose — the lerp resumes normally from there.
        Vector3 pinnedVisual = postPos + _posOffset;
        Quaternion pinnedVisualRot = (_rotOffset * postRot).Normalized();
        _visTargetPrev = pinnedVisual;
        _visTargetRotPrev = pinnedVisualRot;
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
        // Zero the legacy offset state — the past-interp buffer is suspended
        // for the blend window and we'll re-derive _posOffset on completion.
        _posOffset = Vector3.Zero;
        _rotOffset = Quaternion.Identity;
        MonkeLogger.Debug($"[SMOOTH-PVB-START] body={Body.Name} oldPos=({oldPos.X:F3},{oldPos.Y:F3},{oldPos.Z:F3}) oldVel=({oldVel.X:F3},{oldVel.Y:F3},{oldVel.Z:F3}) newPos=({newPos.X:F3},{newPos.Y:F3},{newPos.Z:F3}) newVel=({newVel.X:F3},{newVel.Y:F3},{newVel.Z:F3}) dur={_pvbDuration:F3}");
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
