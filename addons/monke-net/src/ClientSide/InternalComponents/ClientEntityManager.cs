using Godot;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.Client;

[GlobalClass]
public partial class ClientEntityManager : InternalClientComponent
{
    private EntitySpawner _entitySpawner;
    private string _sessionToken = null;  // token for the current connection (saved for next reclaim)
    private string _reclaimToken = null;  // saved from before disconnect (sent to server on next NetworkReady)

    public override void _EnterTree()
    {
        _entitySpawner = MonkeNetManager.Instance?.EntitySpawner;
    }

    /// <summary>
    /// Requests the server to spawn an entity
    /// </summary>
    /// <param name="entityType"></param>
    public void MakeEntityRequest(byte entityType)
    {
        var req = new EntityRequestMessage
        {
            EntityType = entityType
        };

        SendCommandToServer(req, INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.EntityEvent);
    }

    protected override void OnCommandReceived(IPackableMessage command)
    {
        if (command is SessionTokenMessage tokenMsg)
        {
            _sessionToken = tokenMsg.Token;
            MonkeLogger.CurrentToken = _sessionToken;
            MonkeLogger.Info("ClientEntityManager: received session token");
            return;
        }

        if (command is EntityEventMessage entityEvent)
        {
            switch (entityEvent.Event)
            {
                case EntityEventEnum.Created:
                    _entitySpawner.SpawnEntity(entityEvent);
                    break;
                case EntityEventEnum.Destroyed:
                    _entitySpawner.DestroyClientEntity(entityEvent);
                    break;
                default:
                    break;
            }
        }
    }

    protected override void OnNetworkReady()
    {
        base.OnNetworkReady();
        if (_reclaimToken == null) return;
        MonkeLogger.Info("ClientEntityManager: sending reclaim token");
        SendCommandToServer(new ReclaimEntityMessage { Token = _reclaimToken },
            INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.GameReliable);
        _reclaimToken = null;
    }

    protected override void OnServerDisconnected()
    {
        _reclaimToken = _sessionToken; // save for reclaim on next reconnect
        _sessionToken = null;          // will be replaced when server issues new token on reconnect
        MonkeLogger.CurrentToken = null;
        _entitySpawner.ClearClientEntities();
    }
}
