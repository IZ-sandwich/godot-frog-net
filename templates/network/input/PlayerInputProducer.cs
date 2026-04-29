using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;

namespace YourGame;

// Reads keyboard/mouse/controller input each network tick and packages it into
// a PlayerInputMessage for transmission to the server.
//
// Scene setup: add as a child of your LocalPlayer scene. The base class _Ready
// call registers this producer with the ClientManager automatically.
//
// Action names below must match your project's Input Map. Axis actions work for
// both keyboard (returns -1/0/1) and controller sticks (returns analog float).
public partial class PlayerInputProducer : InputProducerComponent
{
    // Assign a Node3D that tracks camera/character yaw and pitch,
    // e.g. your FirstPersonCameraController or SpringArm3D parent.
    [Export] private Node3D _cameraYawPivot;
    [Export] private Node3D _cameraPitchPivot;

    // Mouse look sensitivity (radians per pixel).
    [Export] private float _mouseSensitivity = 0.002f;

    private float _accumulatedYaw;
    private float _accumulatedPitch;

    public override void _Ready()
    {
        base._Ready(); // Registers this producer with MonkeNetConfig — do not remove.
    }

    public override void _Input(InputEvent @event)
    {
        // Accumulate mouse delta between ticks. The accumulated value is sent once
        // per tick then reset so the server sees the same delta the client does.
        if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            _accumulatedYaw   -= motion.Relative.X * _mouseSensitivity;
            _accumulatedPitch -= motion.Relative.Y * _mouseSensitivity;
        }
    }

    public override IPackableElement GenerateCurrentInput()
    {
        // Use _cameraYawPivot.Rotation.Y if you want the absolute yaw sent each tick
        // (simpler, but slightly more bandwidth). The demo uses absolute yaw.
        float yaw = _cameraYawPivot != null
            ? _cameraYawPivot.Rotation.Y
            : _accumulatedYaw;

        float pitch = _cameraPitchPivot != null
            ? _cameraPitchPivot.Rotation.X
            : _accumulatedPitch;

        var input = new PlayerInputMessage
        {
            // Analog movement: works for both keyboard (-1/0/1) and controller stick (analog).
            MoveX        = Input.GetAxis("move_left",  "move_right"),
            MoveY        = Input.GetAxis("move_back",  "move_forward"),
            CameraYaw    = yaw,
            CameraPitch  = pitch,
            Actions      = GetCurrentActions(),
        };

        // Reset accumulated mouse delta after packaging.
        _accumulatedYaw   = 0f;
        _accumulatedPitch = 0f;

        return input;
    }

    private static byte GetCurrentActions()
    {
        byte actions = 0;
        if (Input.IsActionPressed("jump"))     actions = InputHelper.SetPressed(actions, PlayerAction.Jump);
        if (Input.IsActionPressed("crouch"))   actions = InputHelper.SetPressed(actions, PlayerAction.Crouch);
        if (Input.IsActionPressed("interact")) actions = InputHelper.SetPressed(actions, PlayerAction.Interact);
        if (Input.IsActionPressed("sprint"))   actions = InputHelper.SetPressed(actions, PlayerAction.Sprint);
        return actions;
    }
}
