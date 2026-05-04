using System.Linq;
using System.Threading.Tasks;
using GdUnit4;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Server;
using MonkeNet.Tests.Infrastructure;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// SL-01..SL-02: Server stop / restart lifecycle.
///
/// Verifies that stopping and restarting a server leaves no stale peer IDs or entity
/// state that would cause errors (e.g. "peers.has(p_id) is not true") in the new session.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class ServerLifecycleTests
{
    // SL-01 ────────────────────────────────────────────────────────────────────
    // Regression: NetworkManagerEnet.Disconnect() and CreateServer() were not
    // clearing _connectedPeers. Stale IDs from session 1 caused GetPeer() errors
    // when ServerManager iterated connected peers on the fresh ENet host in session 2.
    [TestCase]
    public async Task Server_StopAndRestart_NoStalePeersInNewSession()
    {
        FakeNetworkBridge.Reset();
        MessageSerializer.RegisterNetworkMessages();
        var (serverNet, _) = FakeNetworkBridge.CreatePair();

        // Session 1
        var runner1 = ISceneRunner.Load("res://addons/monke-net/scenes/ServerManager.tscn", autoFree: true);
        await runner1.AwaitIdleFrame();
        var server1 = runner1.Scene() as ServerManager;
        server1!.Initialize(serverNet, 7601);

        serverNet.FireClientConnected(peerId: 5);
        await runner1.AwaitIdleFrame();
        AssertThat(serverNet.GetConnectedPeerIds().Contains(5)).IsTrue();

        // Stop — mirrors MonkeNetManager.StopServer(): remove manager first, then disconnect
        runner1.Dispose();
        serverNet.Disconnect();

        AssertThat(serverNet.GetConnectedPeerIds().Count).IsEqual(0);

        // Session 2 — restart on the same network endpoint
        serverNet.CreateServer(7601);
        var runner2 = ISceneRunner.Load("res://addons/monke-net/scenes/ServerManager.tscn", autoFree: true);
        await runner2.AwaitIdleFrame();
        var server2 = runner2.Scene() as ServerManager;
        server2!.Initialize(serverNet, 7601);

        // Peer 5 from session 1 must not be visible in session 2
        AssertThat(serverNet.GetConnectedPeerIds().Contains(5)).IsFalse();
        AssertThat(serverNet.GetConnectedPeerIds().Count).IsEqual(0);

        // New clients in session 2 register correctly
        serverNet.FireClientConnected(peerId: 10);
        AssertThat(serverNet.GetConnectedPeerIds().Contains(10)).IsTrue();
        AssertThat(serverNet.GetConnectedPeerIds().Contains(5)).IsFalse();

        runner2.Dispose();
    }

    // SL-02 ────────────────────────────────────────────────────────────────────
    // Verifies that entities spawned in session 1 are fully cleared before session 2
    // begins, so the new server starts with an empty entity list.
    [TestCase]
    public async Task Server_StopAndRestart_EntitiesCleanInNewSession()
    {
        MonkeNet.Shared.MonkeNetConfig.Instance = null;
        FakeNetworkBridge.Reset();
        MessageSerializer.RegisterNetworkMessages();
        var (serverNet, _) = FakeNetworkBridge.CreatePair();

        // Load MainScene so EntitySpawner autoload is present
        var mainRunner = ISceneRunner.Load("res://demo/MainScene.tscn", autoFree: true);
        await mainRunner.AwaitIdleFrame();

        // Session 1: spawn an entity
        var runner1 = ISceneRunner.Load("res://addons/monke-net/scenes/ServerManager.tscn", autoFree: true);
        await runner1.AwaitIdleFrame();
        var server1 = runner1.Scene() as ServerManager;
        server1!.Initialize(serverNet, 7602);

        var req = new EntityRequestMessage { EntityType = 0 };
        serverNet.SimulateIncomingPacket(2, MessageSerializer.Serialize(req));
        await runner1.AwaitIdleFrame();

        // Stop: clear entities then tear down (mirrors StopServer order)
        var spawner = MonkeNet.Shared.EntitySpawner.Instance;
        spawner?.ClearServerEntities();
        runner1.Dispose();
        serverNet.Disconnect();

        await mainRunner.AwaitIdleFrame();

        int entitiesAfterStop = MonkeNet.Shared.EntitySpawner.Instance?.Entities.Count ?? 0;
        AssertThat(entitiesAfterStop).IsEqual(0);

        // Session 2: new server starts with empty entity list
        serverNet.CreateServer(7602);
        var runner2 = ISceneRunner.Load("res://addons/monke-net/scenes/ServerManager.tscn", autoFree: true);
        await runner2.AwaitIdleFrame();
        var server2 = runner2.Scene() as ServerManager;
        server2!.Initialize(serverNet, 7602);
        await runner2.AwaitIdleFrame();

        AssertThat(MonkeNet.Shared.EntitySpawner.Instance?.Entities.Count ?? 0).IsEqual(0);

        runner2.Dispose();
        mainRunner.Dispose();
        MonkeNet.Shared.MonkeNetConfig.Instance = null;
    }
}
