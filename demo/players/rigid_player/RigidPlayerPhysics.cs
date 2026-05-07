using Godot;
using MonkeNet.Shared;

namespace GameDemo;

/// <summary>
/// Shared, deterministic rigid-body player simulation. Both
/// <see cref="ServerRigidPlayerStateSyncronizer"/> and
/// <see cref="LocalRigidPlayerPrediction"/> route through this so the server's
/// authoritative tick and the client's predicted tick produce identical body
/// trajectories given identical input — the property the rollback resimulation loop
/// relies on.
///
/// The driver is intentionally simple: each tick we set the horizontal velocity to
/// the input target via <see cref="PredictionRigidbody3D.SetLinearVelocity"/>, leave
/// the Y component untouched so Jolt's gravity and contact response handle vertical
/// motion, and inject one-shot Y velocity for jumps. Setting the same target X/Z on
/// both client and server keeps input-driven motion identical across processes; any
/// per-impact divergence comes from Jolt's contact-resolution nondeterminism, which
/// the threshold + soft-correction in <see cref="LocalRigidPlayerPrediction"/>
/// absorbs.
///
/// Why no manual force/impulse: reaching into the body with custom impulses to
/// "respect collision response" preserves the post-contact velocity (good for
/// in-process feel) but feeds cross-process nondeterminism into the *next* tick's
/// velocity update — so position drift accumulates faster, not slower. Letting Jolt
/// own the per-tick collision response and resetting horizontal velocity to a
/// deterministic, input-derived value is the cross-process-deterministic choice.
/// </summary>
public static class RigidPlayerPhysics
{
    public const float MaxRunSpeed = 5f;
    public const float MaxWalkSpeed = 2f;
    public const float JumpVelocity = 6f;

    // Ground probe ray: starts a few cm ABOVE body center (so the origin is never
    // on a surface — IntersectRay returns no hit when the origin is on/inside a
    // body) and extends only a small distance below it. The capsule's bottom in
    // LocalRigidPlayer.tscn sits at body.Y (CollisionShape3D offset Y=+1 with
    // capsule half-height 1.0), so body.Y is the ground-contact reference and we
    // only want IsOnGround to fire when something is within ~5 cm below it.
    //
    // The previous 1.5 m reach made IsOnGround return true even when the player
    // was 1.4 m airborne — holding Space during a jump re-fired the jump impulse
    // every tick, the player ascended like a rocket, and across two Jolt
    // instances the cumulative Y trajectory diverged enough to trip the
    // misprediction threshold. A tight ~5 cm tolerance restricts ground detection
    // to genuine "feet on the floor" cases: after the first jump's first
    // SpaceStep the body has already risen ~8 cm, so the next tick's space input
    // is correctly rejected and the player follows a single parabolic arc.
    private const float GroundProbeOriginUp = 0.1f;
    private const float GroundProbeReachDown = 0.05f;

    public static void AdvancePhysics(PredictionRigidbody3D predictionRb, CharacterInputMessage input)
    {
        var body = predictionRb?.Body;
        if (body == null) return;

        var move2D = new Vector2(input.MoveX, input.MoveY);
        float inputMagnitude = Mathf.Min(move2D.Length(), 1f);
        bool isWalking = SharedPlayerMovement.ReadInput(input.Keys, InputFlags.Shift);
        bool isJumping = SharedPlayerMovement.ReadInput(input.Keys, InputFlags.Space);

        Vector3 direction = move2D.IsZeroApprox()
            ? Vector3.Zero
            : new Vector3(move2D.X, 0, move2D.Y).Normalized();
        direction = direction.Rotated(Vector3.Up, input.CameraYaw);

        // Start from the body's current velocity so Y (gravity / contact lift) flows
        // through; only horizontal components are overwritten from input.
        Vector3 newVel = body.LinearVelocity;

        if (!direction.IsZeroApprox())
        {
            float speed = (isWalking ? MaxWalkSpeed : MaxRunSpeed) * inputMagnitude;
            newVel.X = direction.X * speed;
            newVel.Z = direction.Z * speed;
        }
        else
        {
            newVel.X = 0;
            newVel.Z = 0;
        }

        if (isJumping && IsOnGround(body))
            newVel.Y = JumpVelocity;

        predictionRb.SetLinearVelocity(newVel);
        predictionRb.Simulate();
    }

    private static bool IsOnGround(RigidBody3D body)
    {
        var space = body.GetWorld3D()?.DirectSpaceState;
        if (space == null) return false;

        Vector3 origin = body.GlobalPosition;
        Vector3 from = origin + Vector3.Up * GroundProbeOriginUp;
        Vector3 to = origin + Vector3.Down * GroundProbeReachDown;

        var query = PhysicsRayQueryParameters3D.Create(from, to);
        // Reuse the body's own collision mask so the player only "stands" on the same
        // layers it physically collides with — environment in single-player, plus
        // ServerPlayers/ClientPlayers split in listen-server mode.
        query.CollisionMask = body.CollisionMask;
        query.Exclude = new Godot.Collections.Array<Rid> { body.GetRid() };

        var hit = space.IntersectRay(query);
        return hit.Count > 0;
    }
}
