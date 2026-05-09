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
/// Horizontal motion is driven through Jolt's solver via capped impulses, not by
/// overwriting the body's post-contact velocity. Replacing horizontal velocity with
/// the input target every tick (the previous design) defeats the contact constraint:
/// when a player runs into an immovable surface — a wall, or a cube wedged against
/// a wall — the integrator advances the body by speed × dt before Jolt's constraint
/// solver reabsorbs the freshly-injected velocity, so the body penetrates the
/// surface by ~8 cm/tick during the transient. In-game this presented as the rigid
/// player creeping forward into the cube and snapping back to its original spot
/// every few frames once Jolt's positional correction caught up.
///
/// Capping the per-tick velocity change at MaxHorizontalAccel × dt lets the contact
/// constraint absorb each small impulse before the integrator can shove the body
/// past the surface. Free locomotion still reaches MaxRunSpeed within a few ticks
/// (~100 ms from standstill at 50 m/s²), and the determinism property is preserved:
/// both client and server compute the same target velocity from the same input and
/// add the same delta to whatever the previous tick's contact response left on the
/// body, so identical input still produces identical trajectories.
/// </summary>
public static class RigidPlayerPhysics
{
    public const float MaxRunSpeed = 5f;
    public const float MaxWalkSpeed = 1f;
    public const float JumpVelocity = 6f;

    // Cap on horizontal velocity change per physics tick (m/s²). 50 m/s² ≈ 5 g —
    // high enough that free-walking reaches MaxRunSpeed in ~6 ticks (100 ms at 60 Hz),
    // low enough that one tick of input cannot blow past a contact constraint. The
    // previous SetLinearVelocity path was effectively infinite acceleration and is
    // what produced the wall-penetration / snap-back bug.
    private const float MaxHorizontalAccel = 50f;
    private const float TickDt = 1f / 60f;

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

        float targetSpeed = !direction.IsZeroApprox()
            ? (isWalking ? MaxWalkSpeed : MaxRunSpeed) * inputMagnitude
            : 0f;
        Vector3 desiredHoriz = direction * targetSpeed;

        // Capped impulse toward the desired horizontal velocity. Y stays untouched
        // so gravity and contact lift flow through unchanged.
        Vector3 currentHoriz = new Vector3(body.LinearVelocity.X, 0, body.LinearVelocity.Z);
        Vector3 deltaVel = desiredHoriz - currentHoriz;
        float maxDelta = MaxHorizontalAccel * TickDt;
        if (deltaVel.LengthSquared() > maxDelta * maxDelta)
            deltaVel = deltaVel.Normalized() * maxDelta;

        bool onGround = IsOnGround(body);

        // Mirrors SharedPlayerMovement's [PHYS-PLAYER] block. RigidBody3D's SpaceStep is
        // run by the framework after this method returns, so postPos/postSlideVel can't
        // be logged here — those land in [PRED-REG] / the next tick's preVel. The shape
        // query below is the rigid-body analogue of MoveAndSlide's slide list: it reports
        // the bodies currently in contact going INTO the step.
        Vector3 prePos = body.GlobalPosition;
        Vector3 preVel = body.LinearVelocity;
        Quaternion preRot = body.Quaternion;
        Vector3 preAngVel = body.AngularVelocity;
        var contacts = QueryContacts(body);
        MonkeLogger.Debug($"[PHYS-RIGIDPLAYER] body={body.Name} input=({input}) desiredHoriz=({desiredHoriz.X:F3},{desiredHoriz.Z:F3}) deltaVel=({deltaVel.X:F3},{deltaVel.Z:F3}) prePos=({prePos.X:F3},{prePos.Y:F3},{prePos.Z:F3}) preVel=({preVel.X:F3},{preVel.Y:F3},{preVel.Z:F3}) preRot=({preRot.X:F3},{preRot.Y:F3},{preRot.Z:F3},{preRot.W:F3}) preAngVel=({preAngVel.X:F3},{preAngVel.Y:F3},{preAngVel.Z:F3}) contacts={contacts.Count} onGround={onGround} jumping={isJumping} walking={isWalking} sleeping={body.Sleeping}");
        for (int i = 0; i < contacts.Count; i++)
        {
            var c = contacts[i];
            MonkeLogger.Debug($"[PHYS-RIGIDPLAYER]   contact[{i}] collider={c.Name} at=({c.Position.X:F3},{c.Position.Y:F3},{c.Position.Z:F3})");
        }

        if (deltaVel.LengthSquared() > 0)
            predictionRb.AddImpulse(new Vector3(deltaVel.X, 0, deltaVel.Z) * body.Mass);

        if (isJumping && onGround)
        {
            // Jump: replace Y velocity outright so we don't compound an existing fall
            // velocity. Done after the horizontal impulse so the queued ops both flush
            // in one Simulate call.
            Vector3 jumpVel = body.LinearVelocity;
            jumpVel.Y = JumpVelocity;
            predictionRb.SetLinearVelocity(jumpVel);
        }

        predictionRb.Simulate();
    }

    private struct ContactInfo
    {
        public string Name;
        public Vector3 Position;
    }

    // Shape query against the body's own collision mask, excluding itself, to find what
    // it's currently overlapping. Equivalent in spirit to CharacterBody3D's slide
    // collisions, but read pre-step (slides are only resolved during SpaceStep on a
    // RigidBody3D, and contact_monitor isn't enabled on the demo player scene).
    private static System.Collections.Generic.List<ContactInfo> QueryContacts(RigidBody3D body)
    {
        var hits = new System.Collections.Generic.List<ContactInfo>();
        var space = body.GetWorld3D()?.DirectSpaceState;
        if (space == null) return hits;

        Node collisionShapeNode = null;
        foreach (Node child in body.GetChildren())
        {
            if (child is CollisionShape3D cs && cs.Shape != null)
            {
                collisionShapeNode = cs;
                break;
            }
        }
        if (collisionShapeNode is not CollisionShape3D shapeNode) return hits;

        var query = new PhysicsShapeQueryParameters3D
        {
            Shape = shapeNode.Shape,
            Transform = shapeNode.GlobalTransform,
            CollisionMask = body.CollisionMask,
            CollideWithBodies = true,
            CollideWithAreas = false,
            Exclude = new Godot.Collections.Array<Rid> { body.GetRid() },
        };

        var results = space.IntersectShape(query, maxResults: 8);
        foreach (var hit in results)
        {
            string name = (hit.TryGetValue("collider", out var cv) && cv.AsGodotObject() is Node n) ? n.Name : "<null>";
            Vector3 pos = body.GlobalPosition;
            hits.Add(new ContactInfo { Name = name, Position = pos });
        }
        return hits;
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
