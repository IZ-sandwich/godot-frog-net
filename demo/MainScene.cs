using Godot;
using MonkeNet.Client;
using MonkeNet.Shared;

namespace GameDemo;

public partial class MainScene : Node3D
{
    private static readonly string FLAG_DEDICATED_SERVER = "as_server";

    private Label _connectingLabel;
    private Button _spawnButton;
    private Button _spawnBallButton;
    private Button _disconnectButton;
    private Button _simulateTimeoutButton;
    private Button _cancelButton;
    private Button _stopServerButton;
    private Label _highPingLabel;
    private Label _packetLossLabel;
    private Label _noResponseLabel;

    public override void _ExitTree()
    {
        var cm = ClientManager.Instance;
        if (cm == null || !IsInstanceValid(cm)) return;
        cm.NetworkReady -= OnNetworkReady;
        cm.ConnectionLost -= OnConnectionLost;
        cm.ConnectionFailed -= OnConnectionFailed;
        cm.ServerSilent -= OnServerSilent;
        cm.ServerResponded -= OnServerResponded;
        cm.LatencyCalculated -= OnLatencyCalculated;
    }

    public override void _Ready()
    {
        _connectingLabel = GetNode<Label>("Menu/ConnectingLabel");
        _spawnButton = GetNode<Button>("Menu/SpawnButton");
        _spawnBallButton = GetNode<Button>("Menu/SpawnBallButton");
        _disconnectButton = GetNode<Button>("Menu/DisconnectButton");
        _simulateTimeoutButton = GetNode<Button>("Menu/SimulateTimeoutButton");
        _cancelButton = GetNode<Button>("Menu/CancelButton");
        _cancelButton.Hide();
        _stopServerButton = GetNode<Button>("Menu/StopServerButton");
        _stopServerButton.Hide();
        _highPingLabel = GetNode<Label>("NetworkStatusPanel/HighPingLabel");
        _packetLossLabel = GetNode<Label>("NetworkStatusPanel/PacketLossLabel");
        _noResponseLabel = GetNode<Label>("NetworkStatusPanel/NoResponseLabel");

        if (OS.HasFeature(FLAG_DEDICATED_SERVER))
        {
            MonkeNetManager.Instance.CreateServer(9999);
        }
    }

    private void OnSpawnButtonPressed()
    {
        ClientManager.Instance.MakeEntityRequest(0);
        _spawnButton.Hide();
    }

    private void OnSpawnBallButtonPressed()
    {
        ClientManager.Instance.MakeEntityRequest(1);
        _spawnBallButton.Hide();
    }

    private void OnHostButtonPressed()
    {
        MonkeLogger.Info("Starting server...");
        MonkeNetManager.Instance.CreateServer(9999);
        GetNode<Button>("Menu/HostButton").Hide();
        GetNode<Button>("Menu/ConnectButton").Hide();
        GetNode<Button>("Menu/HostAndConnectButton").Hide();
        _stopServerButton.Show();
    }

    private void OnStopServerButtonPressed()
    {
        MonkeLogger.Info("Stopping server...");
        MonkeNetManager.Instance.StopServer();
        GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene);
    }

    private void OnConnectButtonPressed()
    {
        MonkeLogger.Info("Connecting...");
        MonkeNetManager.Instance.CreateClient("localhost", 9999);
        GetNode("Menu/HostButton").QueueFree();
        GetNode("Menu/ConnectButton").QueueFree();
        _connectingLabel.Text = "Connecting...";
        _connectingLabel.Show();
        _cancelButton.Show();
        SubscribeClientSignals();
    }

    private void OnHostAndConnectButtonPressed()
    {
        MonkeLogger.Info("Connecting...");
        MonkeNetManager.Instance.CreateListenServer(9999);
        GetNode("Menu/HostButton").QueueFree();
        GetNode("Menu/ConnectButton").QueueFree();
        _connectingLabel.Text = "Connecting...";
        _connectingLabel.Show();
        _cancelButton.Show();
        SubscribeClientSignals();
    }

    private void SubscribeClientSignals()
    {
        ClientManager.Instance.NetworkReady += OnNetworkReady;
        ClientManager.Instance.ConnectionLost += OnConnectionLost;
        ClientManager.Instance.ConnectionFailed += OnConnectionFailed;
        ClientManager.Instance.ServerSilent += OnServerSilent;
        ClientManager.Instance.ServerResponded += OnServerResponded;
        ClientManager.Instance.LatencyCalculated += OnLatencyCalculated;
    }

    private void OnDisconnectButtonPressed()
    {
        MonkeLogger.Info("Disconnect manually requested");
        _disconnectButton.Hide();
        ClientManager.Instance.Disconnect();
        GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene);
    }

    private void OnCancelButtonPressed()
    {
        MonkeLogger.Info("Canceling connection attempt...");
        _cancelButton.Hide();
        ClientManager.Instance.Disconnect();
        GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene);
    }

    private void OnSimulateTimeoutButtonPressed()
    {
        MonkeLogger.Info("Simulating timeout disconnect");
        _disconnectButton.Hide();
        _simulateTimeoutButton.Hide();
        ClientManager.Instance.DisconnectUngraceful();
    }

    private void OnNetworkReady()
    {
        _connectingLabel.Hide();
        _cancelButton.Hide();
        _disconnectButton.Show();
        _simulateTimeoutButton.Show();
    }

    private void OnConnectionLost()
    {
        _disconnectButton.Hide();
        _simulateTimeoutButton.Hide();
        _cancelButton.Hide();
        MonkeLogger.Info("Connection lost. Returning to main menu.");
        _connectingLabel.Text = "Connection lost.";
        _connectingLabel.Show();
        GetTree().CreateTimer(2.0f).Timeout += () =>
            GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene);
    }

    private void OnConnectionFailed()
    {
        _cancelButton.Hide();
        _connectingLabel.Text = "Failed to connect.";
        GetTree().CreateTimer(2.0f).Timeout += () =>
            GetTree().CallDeferred(SceneTree.MethodName.ReloadCurrentScene);
    }

    private void OnServerSilent()
    {
        _noResponseLabel.Show();
    }

    private void OnServerResponded()
    {
        _noResponseLabel.Hide();
    }

    private void OnLatencyCalculated(int latencyTicks, int jitterTicks)
    {
        float tickMs = 1000f / Engine.PhysicsTicksPerSecond;
        _highPingLabel.Visible = latencyTicks * tickMs > 150f;
        _packetLossLabel.Visible = jitterTicks * tickMs > 30f;
    }
}
