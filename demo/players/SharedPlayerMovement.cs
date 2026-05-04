using Godot;
using ImGuiNET;
using MonkeNet.Shared;

namespace GameDemo;

public enum InputFlags
{
    Space = 0b_0000_0001,
    Shift = 0b_0000_0010,
}

/// <summary>
/// Shared player movement code, used to move both client and server players.
/// </summary>
public partial class SharedPlayerMovement : Node
{
    [Export] private CharacterBody3D _characterBody;
    [Export] private float _rigidBodyPushStrength = 1.0f;
    public static readonly float MaxRunSpeed = 5;
    public static readonly float MaxWalkSpeed = 2;
    public static readonly float Gravity = 9.8f;
    public static readonly float JumpVelocity = 6.0f;

    public void AdvancePhysics(CharacterInputMessage input)
    {
        Vector3 newVelocity = CalculateVelocity(_characterBody, input);
        _characterBody.Velocity = newVelocity;
        PhysicsUtils.MoveAndSlide(_characterBody);
        PushRigidBodies();
    }

    // CharacterBody3D.MoveAndSlide does not push RigidBody3Ds it slides against — it just
    // resolves the collision against the player. To push the ball, apply impulse along the
    // collision normal scaled by how fast the player is moving into the body.
    private void PushRigidBodies()
    {
        int count = _characterBody.GetSlideCollisionCount();
        for (int i = 0; i < count; i++)
        {
            var collision = _characterBody.GetSlideCollision(i);
            if (collision.GetCollider() is not RigidBody3D rb)
                continue;

            Vector3 pushDir = -collision.GetNormal();
            float speedIntoBody = _characterBody.Velocity.Dot(pushDir);
            if (speedIntoBody <= 0f)
                continue;

            Vector3 contactOffset = collision.GetPosition() - rb.GlobalPosition;
            rb.ApplyImpulse(pushDir * speedIntoBody * _rigidBodyPushStrength, contactOffset);
        }
    }

    public static Vector3 CalculateVelocity(CharacterBody3D body, CharacterInputMessage input)
    {
        // MoveX/MoveY are analog: -1..1 from either keyboard or controller stick.
        // Clamp the 2D magnitude to 1 so keyboard diagonals don't exceed max speed,
        // while partial stick tilts produce proportionally reduced speed.
        var move2D = new Vector2(input.MoveX, input.MoveY);
        float inputMagnitude = Mathf.Min(move2D.Length(), 1f);

        bool isWalking = ReadInput(input.Keys, InputFlags.Shift);
        bool isJumping = ReadInput(input.Keys, InputFlags.Space);
        Vector3 velocity = body.Velocity;

        bool isOnFloor = body.IsOnFloor();
        Vector3 direction = move2D.IsZeroApprox()
            ? Vector3.Zero
            : new Vector3(move2D.X, 0, move2D.Y).Normalized();
        direction = direction.Rotated(Vector3.Up, input.CameraYaw);

        if (!direction.IsZeroApprox())
        {
            float speed = (isWalking ? MaxWalkSpeed : MaxRunSpeed) * inputMagnitude;
            velocity.X = direction.X * speed;
            velocity.Z = direction.Z * speed;
        }
        else
        {
            velocity.X = 0;
            velocity.Z = 0;
        }

        if (!isOnFloor)
            velocity.Y -= Gravity * PhysicsUtils.DeltaTime;

        if (isJumping && isOnFloor)
            velocity.Y = JumpVelocity;

        return velocity;
    }

    public static bool ReadInput(byte input, InputFlags flag)
    {
        return (input & (byte)flag) > 0;
    }

    public void DisplayDebugInformation()
    {
        if (ImGui.CollapsingHeader("Movement"))
        {
            ImGui.Text($"Position ({_characterBody.GlobalPosition.X:0.00}, {_characterBody.GlobalPosition.Y:0.00}, {_characterBody.GlobalPosition.Z:0.00})");
            ImGui.Text($"Velocity ({_characterBody.Velocity.X:0.00}, {_characterBody.Velocity.Y:0.00}, {_characterBody.Velocity.Z:0.00})");
        }
    }
}
