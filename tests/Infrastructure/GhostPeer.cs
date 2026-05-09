using GameDemo;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.Tests.Infrastructure;

/// <summary>
/// A "ghost peer" — a connected client peer that exists ONLY at the network
/// layer. Owns server-side entities (server applies physics + inputs from this
/// peer like any other client) but has no real <c>ClientManager</c> in the test
/// process. Use this to test multi-client server behaviour: authority routing,
/// snapshot fan-out, concurrent input handling, and cross-client physics
/// interactions, without needing a second <c>ClientManager</c> singleton.
///
/// The "real" client in a test (a full <c>ClientManager</c> wired to a separate
/// <c>FakeNetworkEndpoint</c>) observes the resulting world. Ghost peers are
/// silent observers from their side — packets the server addresses to a ghost
/// peer ID end up in <see cref="FakeNetworkEndpoint.SentPackets"/> for inspection
/// but do not bounce back into a second prediction loop.
/// </summary>
public class GhostPeer
{
    public int PeerId { get; }
    private readonly FakeNetworkEndpoint _serverNet;

    public GhostPeer(FakeNetworkEndpoint serverNet, int peerId)
    {
        _serverNet = serverNet;
        PeerId = peerId;
    }

    /// <summary>Tells the server this ghost peer just connected. Required before
    /// the server will route any traffic to this peer ID. Mirrors the
    /// <c>FireClientConnected</c> pattern used in <c>AuthorityTransferTests</c>.</summary>
    public void Connect() => _serverNet.FireClientConnected(PeerId);

    /// <summary>Tells the server this ghost peer disconnected. Triggers the
    /// server-side cleanup paths (orphan-or-destroy entities, etc.).</summary>
    public void Disconnect() => _serverNet.FireClientDisconnected(PeerId);

    /// <summary>Sends a single <see cref="CharacterInputMessage"/> as if from
    /// this ghost peer for the given <paramref name="targetServerTick"/>. The
    /// server files this input at <c>targetServerTick</c>; if the server
    /// processes that tick later, it will consume the input. Real clients
    /// stamp inputs with their own ticks via <see cref="ClientInputManager"/>;
    /// tests that drive ghost peers must stamp with the actual server tick
    /// the input is intended to be applied at — typically <c>currentServerTick + 1</c>
    /// so the input arrives before the server's NEXT physics tick processes.
    /// </summary>
    public void SendInput(CharacterInputMessage input, int targetServerTick)
    {
        var packed = new PackedClientInputMessage
        {
            Tick = targetServerTick,
            Inputs = new IPackableElement[] { input },
        };
        byte[] bytes = MessageSerializer.Serialize(packed);
        _serverNet.SimulateIncomingPacket(PeerId, bytes);
    }
}
