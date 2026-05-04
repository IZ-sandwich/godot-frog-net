using Godot;
using MonkeNet.Client;

namespace GameDemo;

public partial class FirstPersonCameraController : Node3D
{
    [Export] private float _mouseSensitivity = 0.05f;
    [Export] private float _maxVerticalAngle = 90;

    private Node3D _rotationHelperY;
    private ClientManager _subscribedManager;
    private Callable _networkReadyCallable;
    private bool _networkReadySubscribed;

    public override void _Ready()
    {
        _rotationHelperY = GetParent<Node3D>();
        var cm = ClientManager.Instance;
        if (cm?.IsNetworkReady == true)
        {
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
        else if (cm != null)
        {
            _networkReadyCallable = Callable.From(OnNetworkReady);
            _networkReadySubscribed = true;
            _subscribedManager = cm;
            cm.Connect(ClientManager.SignalName.NetworkReady, _networkReadyCallable);
        }
    }

    public override void _ExitTree()
    {
        Input.MouseMode = Input.MouseModeEnum.Visible;
        if (_networkReadySubscribed && _subscribedManager != null && IsInstanceValid(_subscribedManager))
        {
            _subscribedManager.Disconnect(ClientManager.SignalName.NetworkReady, _networkReadyCallable);
            _networkReadySubscribed = false;
        }
        _subscribedManager = null;
    }

    private void OnNetworkReady()
    {
        Input.MouseMode = Input.MouseModeEnum.Captured;
        if (_networkReadySubscribed && _subscribedManager != null)
        {
            _subscribedManager.Disconnect(ClientManager.SignalName.NetworkReady, _networkReadyCallable);
            _networkReadySubscribed = false;
            _subscribedManager = null;
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (Input.MouseMode == Input.MouseModeEnum.Captured && @event is InputEventMouseMotion mouseMotionEvent)
        {
            RotateX(-Mathf.DegToRad(mouseMotionEvent.Relative.Y * _mouseSensitivity));
            _rotationHelperY.RotateY(Mathf.DegToRad(-mouseMotionEvent.Relative.X * _mouseSensitivity));

            Vector3 cameraRot = RotationDegrees;
            cameraRot.X = Mathf.Clamp(cameraRot.X, -_maxVerticalAngle, _maxVerticalAngle);
            RotationDegrees = cameraRot;

            // Camera is transformed outside _PhysicsProcess; reset so Godot's built-in
            // physics interpolation doesn't lerp back to the previous physics-frame pose.
            ResetPhysicsInterpolation();
            _rotationHelperY.ResetPhysicsInterpolation();
        }

        if (@event is InputEventKey keyEvent)
        {
            if (keyEvent.Keycode == Key.C && keyEvent.Pressed && ClientManager.Instance?.IsNetworkReady == true)
            {
                Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Visible ?
                    Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
            }
        }

    }

    public float GetLateralRotationAngle()
    {
        return _rotationHelperY.Rotation.Y;
    }

    public void RotateCameraYaw(float amount)
    {
        _rotationHelperY.RotateY(amount);
    }
}