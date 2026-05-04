using Godot;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.Client;

[GlobalClass, Icon("res://addons/monke-net/resources/link-solid.png")]
public abstract partial class InternalClientComponent : Node
{
    protected virtual void OnCommandReceived(IPackableMessage command) { }
    protected virtual void OnLatencyCalculated(int latencyAverageTicks, int jitterAverageTicks) { }
    protected virtual void OnProcessTick(int currentTick, IPackableElement input) { }
    protected virtual void OnServerDisconnected() { }

    private bool _networkReady = false;
    private ClientManager _subscribedManager;
    private Callable _networkReadyCallable;
    private Callable _latencyCalculatedCallable;

    public override void _Ready()
    {
        var cm = ClientManager.Instance;
        _subscribedManager = cm;
        cm.ClientTick += OnProcessTick;
        _networkReadyCallable = Callable.From(OnNetworkReady);
        _latencyCalculatedCallable = Callable.From<int, int>(OnLatencyCalculated);
        cm.Connect(ClientManager.SignalName.NetworkReady, _networkReadyCallable);
        cm.Connect(ClientManager.SignalName.LatencyCalculated, _latencyCalculatedCallable);
        cm.CommandReceived += OnCommandReceived;
        cm.ServerDisconnectedInternal += OnServerDisconnected;
    }

    public override void _ExitTree()
    {
        var cm = _subscribedManager;
        if (cm == null || !IsInstanceValid(cm)) return;
        _subscribedManager = null;
        cm.ClientTick -= OnProcessTick;
        cm.Disconnect(ClientManager.SignalName.NetworkReady, _networkReadyCallable);
        cm.Disconnect(ClientManager.SignalName.LatencyCalculated, _latencyCalculatedCallable);
        cm.CommandReceived -= OnCommandReceived;
        cm.ServerDisconnectedInternal -= OnServerDisconnected;
    }

    protected static void SendCommandToServer(IPackableMessage command, INetworkManager.PacketModeEnum mode, int channel)
    {
        ClientManager.Instance.SendCommandToServer(command, mode, channel);
    }

    protected virtual void OnNetworkReady()
    {
        _networkReady = true;
    }

    protected static int NetworkId
    {
        get { return ClientManager.Instance.GetNetworkId(); }
    }

    protected bool NetworkReady
    {
        get { return _networkReady; }
    }
}
