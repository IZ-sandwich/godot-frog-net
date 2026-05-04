using Godot;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.Server;

[GlobalClass, Icon("res://addons/monke-net/resources/link-solid.png")]
public abstract partial class InternalServerComponent : Node
{
    protected virtual void OnCommandReceived(int clientId, IPackableMessage command) { }
    protected virtual void OnProcessTick(int currentTick) { }
    protected virtual void OnNetworkProcessTick(int currentTick) { }
    protected virtual void OnClientConnected(int peerId) { }
    protected virtual void OnClientDisconnected(int peerId) { }

    private ServerManager _subscribedManager;
    private Callable _serverTickCallable;
    private Callable _serverNetworkTickCallable;
    private Callable _clientConnectedCallable;
    private Callable _clientDisconnectedCallable;

    public override void _Ready()
    {
        var sm = ServerManager.Instance;
        _subscribedManager = sm;
        _serverTickCallable = Callable.From<int>(OnProcessTick);
        _serverNetworkTickCallable = Callable.From<int>(OnNetworkProcessTick);
        _clientConnectedCallable = Callable.From<int>(OnClientConnected);
        _clientDisconnectedCallable = Callable.From<int>(OnClientDisconnected);
        sm.Connect(ServerManager.SignalName.ServerTick, _serverTickCallable);
        sm.Connect(ServerManager.SignalName.ServerNetworkTick, _serverNetworkTickCallable);
        sm.CommandReceived += OnCommandReceived;
        sm.Connect(ServerManager.SignalName.ClientConnected, _clientConnectedCallable);
        sm.Connect(ServerManager.SignalName.ClientDisconnected, _clientDisconnectedCallable);
    }

    public override void _ExitTree()
    {
        var sm = _subscribedManager;
        if (sm == null || !IsInstanceValid(sm)) return;
        _subscribedManager = null;
        sm.Disconnect(ServerManager.SignalName.ServerTick, _serverTickCallable);
        sm.Disconnect(ServerManager.SignalName.ServerNetworkTick, _serverNetworkTickCallable);
        sm.CommandReceived -= OnCommandReceived;
        sm.Disconnect(ServerManager.SignalName.ClientConnected, _clientConnectedCallable);
        sm.Disconnect(ServerManager.SignalName.ClientDisconnected, _clientDisconnectedCallable);
    }

    protected static void SendCommandToClient(int peerId, IPackableMessage command, INetworkManager.PacketModeEnum mode, int channel)
    {
        ServerManager.Instance.SendCommandToClient(peerId, command, mode, channel);
    }

    protected int NetworkId
    {
        get { return ServerManager.Instance.GetNetworkId(); }
    }
}
