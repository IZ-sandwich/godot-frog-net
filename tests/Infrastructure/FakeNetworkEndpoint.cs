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

    // Network condition simulation knobs. All are off by default so existing tests
    // that rely on synchronous delivery are unaffected.
    //
    // PacketLossRate: 0..1 probability that an outgoing packet is silently dropped
    //   on the way to the peer. Deterministic via PacketLossRng (seeded test RNG).
    // QueuePackets: when true, packets sent to the peer are NOT delivered immediately;
    //   they queue on the peer's PendingInbound. Tests then call DeliverNextPending /
    //   DeliverAllPending / DropNextPending to control delivery timing manually,
    //   simulating latency / jitter / burst loss without a real clock.
    public float PacketLossRate { get; set; } = 0f;
    public System.Random PacketLossRng { get; set; } = new(12345);
    public bool QueuePackets { get; set; } = false;
    public int DroppedPackets { get; private set; } = 0;

    /// <summary>Inbound queue: packets that arrived from the peer but have not yet
    /// been delivered (when QueuePackets is true on the peer). Each entry carries
    /// the sender id, raw bytes, and the channel — same data PacketReceived would
    /// receive plus channel/mode info that's needed for selective drop.</summary>
    public Queue<(long FromId, byte[] Data, int Channel, INetworkManager.PacketModeEnum Mode)> PendingInbound { get; }
        = new();

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

        // Drop on the sending side, deterministically per the seeded RNG. Reliable
        // packets are not dropped — mirrors ENet semantics where Reliable is
        // retransmitted until acked, so a "loss rate" applies only to Unreliable.
        if (PacketLossRate > 0f
            && mode == INetworkManager.PacketModeEnum.Unreliable
            && PacketLossRng.NextDouble() < PacketLossRate)
        {
            DroppedPackets++;
            return;
        }

        if (_peer == null) return;

        if (_peer.QueuePackets)
        {
            // Manual delivery — push to the peer's inbound queue instead of firing
            // PacketReceived. Tests call DeliverNextPending/etc. to release. Channel
            // and mode are preserved so tests can drop only specific channels.
            _peer.PendingInbound.Enqueue((_networkId, bin, channel, mode));
            return;
        }

        // Default: synchronous delivery, same as before. Broadcast (id == 0) and
        // unicast (id != 0) collapse to the same single-peer delivery here because
        // FakeNetworkBridge only models a single client/server pair per endpoint.
        _peer.PacketReceived?.Invoke(_networkId, bin);
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

    // ── Manual delivery (network condition simulation) ─────────────────────────
    // When QueuePackets=true, packets the peer sends arrive in PendingInbound.
    // The methods below let tests model latency, jitter, and burst loss by
    // controlling exactly when (and whether) each pending packet is released.

    /// <summary>Releases the next queued packet to PacketReceived. No-op if queue is empty.</summary>
    public void DeliverNextPending()
    {
        if (PendingInbound.Count == 0) return;
        var (from, data, _, _) = PendingInbound.Dequeue();
        PacketReceived?.Invoke(from, data);
    }

    /// <summary>Releases all queued packets in arrival order.</summary>
    public void DeliverAllPending()
    {
        while (PendingInbound.Count > 0)
        {
            var (from, data, _, _) = PendingInbound.Dequeue();
            PacketReceived?.Invoke(from, data);
        }
    }

    /// <summary>Drops the next queued packet without delivering. Counts toward DroppedPackets.</summary>
    public void DropNextPending()
    {
        if (PendingInbound.Count == 0) return;
        PendingInbound.Dequeue();
        DroppedPackets++;
    }

    /// <summary>Drops the next <paramref name="count"/> queued packets (or fewer if the queue is shorter).</summary>
    public void DropNextPending(int count)
    {
        for (int i = 0; i < count && PendingInbound.Count > 0; i++)
        {
            PendingInbound.Dequeue();
            DroppedPackets++;
        }
    }

    /// <summary>Drops the first queued packet matching <paramref name="channel"/>; preserves order
    /// of all other queued packets. Returns true if a packet was dropped. Useful when burst-
    /// dropping snapshots without losing reliable handshake/clock-sync traffic.</summary>
    public bool DropNextOnChannel(int channel)
    {
        if (PendingInbound.Count == 0) return false;
        var keep = new Queue<(long, byte[], int, INetworkManager.PacketModeEnum)>(PendingInbound.Count);
        bool dropped = false;
        while (PendingInbound.Count > 0)
        {
            var entry = PendingInbound.Dequeue();
            if (!dropped && entry.Channel == channel)
            {
                dropped = true;
                DroppedPackets++;
                continue;
            }
            keep.Enqueue(entry);
        }
        while (keep.Count > 0) PendingInbound.Enqueue(keep.Dequeue());
        return dropped;
    }

    /// <summary>Releases queued packets that DON'T match <paramref name="excludeChannel"/>.
    /// Used to keep reliable traffic flowing while holding back snapshots.</summary>
    public void DeliverPendingExcept(int excludeChannel)
    {
        var keep = new Queue<(long, byte[], int, INetworkManager.PacketModeEnum)>(PendingInbound.Count);
        while (PendingInbound.Count > 0)
        {
            var entry = PendingInbound.Dequeue();
            if (entry.Item3 == excludeChannel) keep.Enqueue(entry);
            else PacketReceived?.Invoke(entry.Item1, entry.Item2);
        }
        while (keep.Count > 0) PendingInbound.Enqueue(keep.Dequeue());
    }
}
