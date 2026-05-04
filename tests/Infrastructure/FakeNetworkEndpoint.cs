using System.Collections.Generic;
using MonkeNet.Shared;

namespace MonkeNet.Tests.Infrastructure;

/// <summary>
/// In-memory INetworkManager implementation. Routes packets synchronously to its paired
/// endpoint so integration tests can drive the full client/server protocol without ENet.
/// </summary>
public class FakeNetworkEndpoint : INetworkManager
{
    public event INetworkManager.ClientConnectedEventHandler ClientConnected;
    public event INetworkManager.ClientDisconnectedEventHandler ClientDisconnected;
    public event INetworkManager.PacketReceivedEventHandler PacketReceived;

    public List<(byte[] Data, int Id, int Channel, INetworkManager.PacketModeEnum Mode)> SentPackets { get; } = new();
    public List<int> DisconnectedClients { get; } = new();
    public List<int> ForceDisconnectedClients { get; } = new();

    private int _networkId;
    private FakeNetworkEndpoint _peer;
    private bool _isServer;

    // Tracks which client peer IDs are connected to this server endpoint (for multi-client scenarios).
    private readonly List<long> _connectedClientIds = new();

    public FakeNetworkEndpoint(int networkId)
    {
        _networkId = networkId;
    }

    public void SetPeer(FakeNetworkEndpoint peer)
    {
        _peer = peer;
    }

    public void CreateServer(int port, int maxClients = 32)
    {
        _connectedClientIds.Clear();
        _isServer = true;
    }

    // Simulate a full ENet handshake: fire ClientConnected on both endpoints.
    // The client always sees the server as peer id=1; the server sees this client id.
    // Only completes the handshake if the peer has actually called CreateServer,
    // mirroring ENet's behaviour where a connection attempt to a non-listening server
    // never produces a ClientConnected event.
    public void CreateClient(string address, int port)
    {
        _isServer = false;
        if (_peer?._isServer != true) return;
        ClientConnected?.Invoke(1);
        _peer.FireClientConnected(_networkId);
    }

    public void SendBytes(byte[] bin, int id, int channel, INetworkManager.PacketModeEnum mode)
    {
        SentPackets.Add((bin, id, channel, mode));

        if (id == 0)
        {
            // Broadcast: deliver to all connected peers on the other endpoint
            _peer?.PacketReceived?.Invoke(_networkId, bin);
        }
        else
        {
            // Unicast: deliver to the single paired endpoint
            _peer?.PacketReceived?.Invoke(_networkId, bin);
        }
    }

    public void Disconnect() { _connectedClientIds.Clear(); }

    public void DisconnectClient(int clientId, bool force = false)
    {
        DisconnectedClients.Add(clientId);
        if (force) ForceDisconnectedClients.Add(clientId);
        ClientDisconnected?.Invoke(clientId);
        _peer?.FireClientDisconnected(clientId);
    }

    public int GetNetworkId() => _networkId;

    public IReadOnlyCollection<int> GetConnectedPeerIds() =>
        _connectedClientIds.ConvertAll(id => (int)id);

    public int GetPeerRtt(int peerId) => 0;

    public int PopStatistic(INetworkManager.NetworkStatisticEnum statistic) => 0;

    // ── Test helpers ───────────────────────────────────────────────────────────

    /// <summary>Simulates the server disconnecting (client receives ClientDisconnected(1)).</summary>
    public void SimulateServerDisconnected()
    {
        ClientDisconnected?.Invoke(1);
    }

    /// <summary>Simulates a client dropping (fires on both endpoints).</summary>
    public void SimulateClientDisconnected(long peerId)
    {
        ClientDisconnected?.Invoke(peerId);
        _peer?.FireClientDisconnected(peerId);
    }

    /// <summary>Called by the peer to fire ClientConnected on this endpoint.</summary>
    public void FireClientConnected(long peerId)
    {
        _connectedClientIds.Add(peerId);
        ClientConnected?.Invoke(peerId);
    }

    /// <summary>Called by the peer to fire ClientDisconnected on this endpoint.</summary>
    public void FireClientDisconnected(long peerId)
    {
        _connectedClientIds.Remove(peerId);
        ClientDisconnected?.Invoke(peerId);
    }

    public void ClearSentPackets() => SentPackets.Clear();

    /// <summary>Simulates a packet arriving from <paramref name="fromId"/> at this endpoint.</summary>
    public void SimulateIncomingPacket(long fromId, byte[] data) => PacketReceived?.Invoke(fromId, data);
}
