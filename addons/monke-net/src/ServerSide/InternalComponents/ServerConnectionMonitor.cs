using Godot;
using ImGuiNET;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;
using System.Collections.Generic;

namespace MonkeNet.Server;

/// <summary>
/// Issues a server-generated session token to each client on connect, applies the
/// configured entity-retention mode (RemoveEntity or KeepEntity) on disconnect,
/// and stores reclaim tokens so reconnecting clients can recover their entities.
///
/// ENet itself is authoritative for detecting dropped clients: enet_peer_timeout
/// fires EVENT_DISCONNECT, which ENetMultiplayerPeer turns into peer_disconnected.
/// </summary>
[GlobalClass]
public partial class ServerConnectionMonitor : InternalServerComponent
{
    [Export] public DisconnectEntityMode TimeoutDisconnectMode { get; set; } = DisconnectEntityMode.RemoveEntity;
    [Export] public DisconnectEntityMode ManualDisconnectMode { get; set; } = DisconnectEntityMode.RemoveEntity;

    private readonly HashSet<int> _voluntaryDisconnects = new();
    private readonly Dictionary<int, string> _sessionTokenByClient = new();
    private readonly Dictionary<string, List<int>> _reclaimableEntities = new();

    protected override void OnClientConnected(int clientId)
    {
        string token = System.Guid.NewGuid().ToString();
        _sessionTokenByClient[clientId] = token;
        SendCommandToClient(clientId, new SessionTokenMessage { Token = token },
            INetworkManager.PacketModeEnum.Reliable, (int)ChannelEnum.GameReliable);
        MonkeLogger.Info($"ServerConnectionMonitor: issued session token to client {clientId} [tok:{Tok(token)}]");
    }

    protected override void OnCommandReceived(int clientId, IPackableMessage command)
    {
        if (command is DisconnectNotificationMessage)
        {
            _voluntaryDisconnects.Add(clientId);
            string tok = _sessionTokenByClient.TryGetValue(clientId, out var t) ? Tok(t) : "????";
            MonkeLogger.Info($"ServerConnectionMonitor: client {clientId} sent voluntary disconnect notification [tok:{tok}]");
        }
    }

    protected override void OnClientDisconnected(int clientId)
    {
        bool wasVoluntary = _voluntaryDisconnects.Remove(clientId);

        DisconnectEntityMode mode = wasVoluntary ? ManualDisconnectMode : TimeoutDisconnectMode;
        var entityManager = ServerManager.Instance.EntityManager;

        if (mode == DisconnectEntityMode.KeepEntity
            && _sessionTokenByClient.TryGetValue(clientId, out string token))
        {
            var entityIds = entityManager.GetEntityIdsForClient(clientId);
            if (entityIds.Count > 0)
                _reclaimableEntities[token] = entityIds;
            entityManager.OrphanEntitiesForClient(clientId);
            MonkeLogger.Info($"ServerConnectionMonitor: client {clientId} entities orphaned [tok:{Tok(token)}]");
        }
        else
        {
            string tok = _sessionTokenByClient.TryGetValue(clientId, out var t) ? Tok(t) : "????";
            entityManager.DestroyEntitiesForClient(clientId);
            MonkeLogger.Info($"ServerConnectionMonitor: client {clientId} entities destroyed [tok:{tok}]");
        }

        _sessionTokenByClient.Remove(clientId);
    }

    private static string Tok(string token) =>
        token?.Length >= 4 ? token[^4..] : (token ?? "????");

    /// <summary>
    /// Validates a reclaim token and returns the entity IDs it maps to, consuming the token.
    /// Returns null if the token is invalid or already used.
    /// </summary>
    public List<int> ConsumeReclaimToken(string token)
    {
        if (!_reclaimableEntities.TryGetValue(token, out var entityIds))
            return null;
        _reclaimableEntities.Remove(token);
        return entityIds;
    }

    public void DisplayDebugInformation(ServerInputReceiver inputReceiver)
    {
        if (!ImGui.CollapsingHeader("Connected Players")) return;
        var peers = ServerManager.Instance.GetConnectedPeerIds();
        bool any = false;
        foreach (int peerId in peers)
        {
            any = true;
            int rtt = ServerManager.Instance.GetPeerRtt(peerId);
            int missed = inputReceiver.GetMissedInputTotal(peerId);
            float rate = inputReceiver.GetMissedInputRate(peerId) * 100f;
            string tok = _sessionTokenByClient.TryGetValue(peerId, out var t) ? Tok(t) : "----";
            ImGui.TextUnformatted($"Peer {peerId} [tok:{tok}]:  RTT {rtt} ms   Missed {missed} total  ({rate:0.0}% last 64)");
        }
        if (!any) ImGui.Text("No players connected");
    }
}
