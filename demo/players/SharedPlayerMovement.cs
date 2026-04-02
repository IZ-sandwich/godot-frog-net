using Godot;
using ImGuiNET;
using MonkeNet.Shared;

namespace GameDemo;

public enum InputFlags
{
    Forward = 0b_0000_0001,
    Backward = 0b_0000_0010,
    Left = 0b_0000_0100,
    Right = 0b_0000_1000,
    Space = 0b_0001_0000,
    Shift = 0b_0010_0000,
}

/// <summary>
/// Shared player movement code, used to move both client and server players.
/// </summary>
public partial class SharedPlayerMovement : Node
{
    [Export] private CharacterBody3D _characterBody;
    public static readonly float MaxRunSpeed = 5;
    public static readonly float MaxWalkSpeed = 2;
    public static readonly float Gravity = 9.8f;
    public static readonly float JumpVelocity = 6.0f;

    public void AdvancePhysics(CharacterInputMessage input)
    {
        _characterBody.Velocity = CalculateVelocity(_characterBody, input);
        PhysicsUtils.MoveAndSlide(_characterBody);
    }

    public static Vector3 CalculateVelocity(CharacterBody3D body, CharacterInputMessage input)
    {
        Vector2 direction2D = InputToDirection(input.Keys);

        bool isWalking = ReadInput(input.Keys, InputFlags.Shift);
        bool isJumping = ReadInput(input.Keys, InputFlags.Space);
        Vector3 velocity = body.Velocity;

        bool isOnFloor = body.IsOnFloor();
        Vector3 direction = new Vector3(direction2D.X, 0, direction2D.Y).Normalized();
        direction = direction.Rotated(Vector3.Up, input.CameraYaw);

        if (!direction.IsZeroApprox())
        {
            velocity.X = direction.X * (isWalking ? MaxWalkSpeed : MaxRunSpeed);
            velocity.Z = direction.Z * (isWalking ? MaxWalkSpeed : MaxRunSpeed);
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

    public static Vector2 InputToDirection(byte input)
    {
        Vector2 direction = Vector2.Zero;

        if ((input & (byte)InputFlags.Right) > 0) direction.X += 1;
        if ((input & (byte)InputFlags.Left) > 0) direction.X -= 1;
        if ((input & (byte)InputFlags.Backward) > 0) direction.Y += 1;
        if ((input & (byte)InputFlags.Forward) > 0) direction.Y -= 1;

        return direction.Normalized();
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
