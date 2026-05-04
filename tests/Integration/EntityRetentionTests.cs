using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GdUnit4;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Server;
using MonkeNet.Shared;
using MonkeNet.Tests.Infrastructure;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// R-01..R-07: Entity retention and session token reclaim tests.
///
/// Verifies that ServerConnectionMonitor issues session tokens, orphans entities in
/// KeepEntity mode, and that reconnecting clients can reclaim their entities using
/// the server-issued token.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class EntityRetentionTests
{
    private const int ClientPeerId = 2;

    private FakeNetworkEndpoint _serverNet;
    private FakeNetworkEndpoint _clientNet;
    private ISceneRunner _serverRunner;
    private ISceneRunner _clientRunner;
    private ISceneRunner _mainSceneRunner;
    private ServerManager _server;
    private MonkeNet.Client.ClientManager _client;
    private ServerConnectionMonitor _monitor;

    [BeforeTest]
    public async Task SetUp()
    {
        MonkeNetConfig.Instance = null;
        FakeNetworkBridge.Reset();
        MessageSerializer.RegisterNetworkMessages();
        (_serverNet, _clientNet) = FakeNetworkBridge.CreatePair();

        _mainSceneRunner = ISceneRunner.Load("res://demo/MainScene.tscn", autoFree: true);
        await _mainSceneRunner.AwaitIdleFrame();

        _serverRunner = ISceneRunner.Load("res://addons/monke-net/scenes/ServerManager.tscn", autoFree: true);
        await _serverRunner.AwaitIdleFrame();
        _server = _serverRunner.Scene() as ServerManager;

        _clientRunner = ISceneRunner.Load("res://addons/monke-net/scenes/ClientManager.tscn", autoFree: true);
        await _clientRunner.AwaitIdleFrame();
        _client = _clientRunner.Scene() as MonkeNet.Client.ClientManager;

        _server!.Initialize(_serverNet, port: 7600);
        _client!.Initialize(_clientNet, "127.0.0.1", 7600);
        await _serverRunner.AwaitIdleFrame();
        await _clientRunner.AwaitIdleFrame();

        _monitor = _server.GetNode<ServerConnectionMonitor>("ServerConnectionMonitor");

        ClearSpawnedEntities();
    }

    [AfterTest]
    public void TearDown()
    {
        _serverRunner?.Dispose();
        _clientRunner?.Dispose();
        _mainSceneRunner?.Dispose();
        MonkeNetConfig.Instance = null;
    }

    // R-01 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task SessionToken_SentToClientOnConnect()
    {
        // The session token is sent by ServerConnectionMonitor.OnClientConnected,
        // which fires when _client.Initialize calls CreateClient, triggering FireClientConnected.
        bool tokenReceived = false;
        foreach (var (data, _, _, _) in _serverNet.SentPackets)
        {
            try
            {
                if (MessageSerializer.Deserialize(data) is SessionTokenMessage)
                {
                    tokenReceived = true;
                    break;
                }
            }
            catch { }
        }

        AssertThat(tokenReceived).IsTrue();
    }

    // R-02 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task ManualDisconnect_RemoveEntityMode_DestroysEntities()
    {
        _monitor.ManualDisconnectMode = DisconnectEntityMode.RemoveEntity;

        SpawnEntityForClient(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        int countBefore = EntitySpawner.Instance.Entities.Count;

        SendDisconnectNotification(ClientPeerId);
        _serverNet.FireClientDisconnected(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();

        int countAfter = EntitySpawner.Instance.Entities.Count;
        AssertThat(countAfter).IsLess(countBefore);
    }

    // R-03 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task ManualDisconnect_KeepEntityMode_OrphansEntities()
    {
        _monitor.ManualDisconnectMode = DisconnectEntityMode.KeepEntity;

        SpawnEntityForClient(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        int countBefore = EntitySpawner.Instance.Entities.Count;
        AssertThat(countBefore).IsGreaterEqual(1);

        SendDisconnectNotification(ClientPeerId);
        _serverNet.FireClientDisconnected(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();

        // Entity count must not decrease — entity is kept alive
        int countAfter = EntitySpawner.Instance.Entities.Count;
        AssertThat(countAfter).IsEqual(countBefore);

        // All entities previously owned by the client must now be orphaned (Authority == 0)
        bool allOrphaned = EntitySpawner.Instance.Entities
            .All(e => e.Authority != ClientPeerId);
        AssertThat(allOrphaned).IsTrue();
    }

    // R-04 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task ReclaimToken_RestoredAfterManualDisconnect()
    {
        _monitor.ManualDisconnectMode = DisconnectEntityMode.KeepEntity;

        // Spawn entity and capture its ID
        SpawnEntityForClient(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        AssertThat(EntitySpawner.Instance.Entities.Count).IsGreaterEqual(1);
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        // Disconnect voluntarily — entity is orphaned, token stored server-side
        SendDisconnectNotification(ClientPeerId);
        _serverNet.FireClientDisconnected(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();

        // Simulate reconnect: new peer ID 3 connects; the client EntityManager received
        // the session token (for peer 2) during initial connection and saved it as _reclaimToken
        // in OnServerDisconnected. Now NetworkReady fires for the reconnected client,
        // which sends ReclaimEntityMessage with the old token.
        // We simulate this by delivering the reclaim message directly from peer 3.
        string savedToken = GetSavedReclaimToken();
        AssertThat(savedToken).IsNotNull();

        int newPeerId = 3;
        _serverNet.FireClientConnected(newPeerId);
        await _serverRunner.AwaitIdleFrame();

        var reclaimMsg = new ReclaimEntityMessage { Token = savedToken };
        _serverNet.SimulateIncomingPacket(newPeerId, MessageSerializer.Serialize(reclaimMsg));
        await _serverRunner.AwaitIdleFrame();

        // Entity authority must now belong to the reconnected peer
        var entity = EntitySpawner.Instance.Entities.FirstOrDefault(e => e.EntityId == entityId);
        AssertThat(entity).IsNotNull();
        AssertThat(entity!.Authority).IsEqual(newPeerId);
    }

    // R-05 ─────────────────────────────────────────────────────────────────────
    // ENet (not the monitor) detects client timeouts and fires PeerDisconnected.
    // A timeout-induced disconnect arrives at OnClientDisconnected without a prior
    // DisconnectNotificationMessage, so the monitor applies TimeoutDisconnectMode.
    [TestCase]
    public async Task TimeoutDisconnect_KeepEntityMode_OrphansEntities()
    {
        _monitor.TimeoutDisconnectMode = DisconnectEntityMode.KeepEntity;

        SpawnEntityForClient(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        int countBefore = EntitySpawner.Instance.Entities.Count;
        AssertThat(countBefore).IsGreaterEqual(1);

        // ENet detects the timeout — no DisconnectNotificationMessage was sent.
        _serverNet.FireClientDisconnected(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();

        // Entity stays alive, orphaned
        int countAfter = EntitySpawner.Instance.Entities.Count;
        AssertThat(countAfter).IsEqual(countBefore);

        bool allOrphaned = EntitySpawner.Instance.Entities.All(e => e.Authority != ClientPeerId);
        AssertThat(allOrphaned).IsTrue();
    }

    // R-06 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task ReclaimToken_Invalid_IsIgnored()
    {
        SpawnEntityForClient(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        int originalAuthority = EntitySpawner.Instance.Entities[0].Authority;

        var fakeReclaim = new ReclaimEntityMessage { Token = "not-a-real-token" };
        _serverNet.SimulateIncomingPacket(ClientPeerId, MessageSerializer.Serialize(fakeReclaim));
        await _serverRunner.AwaitIdleFrame();

        // Authority unchanged
        AssertThat(EntitySpawner.Instance.Entities[0].Authority).IsEqual(originalAuthority);
    }

    // R-07 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task ReclaimToken_SingleUse_SecondAttemptFails()
    {
        _monitor.ManualDisconnectMode = DisconnectEntityMode.KeepEntity;

        SpawnEntityForClient(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();
        int entityId = EntitySpawner.Instance.Entities[0].EntityId;

        SendDisconnectNotification(ClientPeerId);
        _serverNet.FireClientDisconnected(ClientPeerId);
        await _serverRunner.AwaitIdleFrame();

        string token = GetSavedReclaimToken();
        AssertThat(token).IsNotNull();

        // First reclaim succeeds
        int newPeerId = 3;
        _serverNet.FireClientConnected(newPeerId);
        await _serverRunner.AwaitIdleFrame();
        _serverNet.SimulateIncomingPacket(newPeerId, MessageSerializer.Serialize(new ReclaimEntityMessage { Token = token }));
        await _serverRunner.AwaitIdleFrame();
        AssertThat(EntitySpawner.Instance.Entities.First(e => e.EntityId == entityId).Authority).IsEqual(newPeerId);

        // Second reclaim with same token must do nothing (token consumed)
        int anotherPeerId = 4;
        _serverNet.FireClientConnected(anotherPeerId);
        await _serverRunner.AwaitIdleFrame();
        _serverNet.SimulateIncomingPacket(anotherPeerId, MessageSerializer.Serialize(new ReclaimEntityMessage { Token = token }));
        await _serverRunner.AwaitIdleFrame();
        AssertThat(EntitySpawner.Instance.Entities.First(e => e.EntityId == entityId).Authority).IsEqual(newPeerId);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private void SpawnEntityForClient(int clientId)
    {
        var req = new EntityRequestMessage { EntityType = 0 };
        _serverNet.SimulateIncomingPacket(clientId, MessageSerializer.Serialize(req));
    }

    private void SendDisconnectNotification(int clientId)
    {
        var msg = new DisconnectNotificationMessage();
        _serverNet.SimulateIncomingPacket(clientId, MessageSerializer.Serialize(msg));
    }

    private string GetSavedReclaimToken()
    {
        // The token lives in _monitor._reclaimableEntities. We access it via
        // ConsumeReclaimToken to find any entry, then put it back by re-adding.
        // Instead, we scan _serverNet.SentPackets for the SessionTokenMessage sent
        // to ClientPeerId at connection time and use that string directly.
        foreach (var (data, id, _, _) in _serverNet.SentPackets)
        {
            if (id != ClientPeerId) continue;
            try
            {
                if (MessageSerializer.Deserialize(data) is SessionTokenMessage tokenMsg)
                    return tokenMsg.Token;
            }
            catch { }
        }
        return null;
    }

    private void ClearSpawnedEntities()
    {
        var spawner = EntitySpawner.Instance;
        if (spawner == null) return;
        foreach (var e in spawner.Entities.ToList())
        {
            if (Godot.GodotObject.IsInstanceValid(e))
            {
                Godot.Node current = e;
                while (current.GetParent() != spawner && current.GetParent() != null)
                    current = current.GetParent();
                current.QueueFree();
            }
        }
        spawner.Entities.Clear();
        spawner.ClearClientEntities();
    }
}
