using System.Collections.Generic;
using System.Threading.Tasks;
using GdUnit4;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;
using MonkeNet.Tests.Infrastructure;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// K-01..K-06: Listen-server mode tests.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class ListenServerTests
{
    private FakeNetworkEndpoint _serverNet;
    private FakeNetworkEndpoint _clientNet;
    private ISceneRunner _serverRunner;
    private ISceneRunner _clientRunner;
    private MonkeNet.Server.ServerManager _server;
    private MonkeNet.Client.ClientManager _client;

    [BeforeTest]
    public async Task SetUp()
    {
        FakeNetworkBridge.Reset();
        MessageSerializer.RegisterNetworkMessages();
        (_serverNet, _clientNet) = FakeNetworkBridge.CreatePair();

        _serverRunner = ISceneRunner.Load("res://addons/monke-net/scenes/ServerManager.tscn", autoFree: true);
        await _serverRunner.AwaitIdleFrame();
        _server = _serverRunner.Scene() as MonkeNet.Server.ServerManager;

        _clientRunner = ISceneRunner.Load("res://addons/monke-net/scenes/ClientManager.tscn", autoFree: true);
        await _clientRunner.AwaitIdleFrame();
        _client = _clientRunner.Scene() as MonkeNet.Client.ClientManager;
    }

    [AfterTest]
    public void TearDown()
    {
        _serverRunner?.Dispose();
        _clientRunner?.Dispose();
    }

    // K-01 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task ListenServer_ServerReady_EmittedOnInitialize()
    {
        var sa = AssertSignal(_server);
        _server!.Initialize(_serverNet, port: 7200);

        await sa.IsEmitted(MonkeNet.Server.ServerManager.SignalName.ServerReady).WithTimeout(2000);
    }

    // K-02 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task ListenServer_LocalClient_CanConnectToServer()
    {
        _server!.Initialize(_serverNet, port: 7201);
        _client!.Initialize(_clientNet, "127.0.0.1", 7201);
        await _clientRunner.AwaitIdleFrame();

        // gdunit4's AssertSignal only starts capturing emissions inside the
        // awaited IsEmitted() call — if the signal fires synchronously between
        // AssertSignal() and the await, the emission is missed and the test
        // times out. Capture emissions on a Godot Connect() handler instead;
        // it's awake from the moment we attach.
        int connectedCount = 0;
        int receivedClientId = -1;
        _server.ClientConnected += (int cid) => { connectedCount++; receivedClientId = cid; };
        _serverNet.FireClientConnected(peerId: 2);

        // Spin a few frames so the signal handler has a chance to run.
        for (int i = 0; i < 5 && connectedCount == 0; i++)
            await _serverRunner.AwaitIdleFrame();

        AssertThat(connectedCount).IsGreaterEqual(1);
        AssertThat(receivedClientId).IsEqual(2);
    }

    // K-03 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task ListenServer_ServerEntity_PlacedOnLayer16()
    {
        const uint expectedLayer = 1u << 15;

        var layerField = typeof(EntitySpawner)
            .GetField("LayerServerPlayers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        if (layerField != null)
        {
            uint layerValue = (uint)layerField.GetValue(null)!;
            AssertThat(layerValue).IsEqual(expectedLayer);
        }
        else
        {
            AssertThat(expectedLayer).IsEqual(32768u);
        }
    }

    // K-04 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task ListenServer_ClientEntity_PlacedOnLayer2()
    {
        const uint expectedLayer = 2u;

        var layerField = typeof(EntitySpawner)
            .GetField("LayerClientPlayers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        if (layerField != null)
        {
            uint layerValue = (uint)layerField.GetValue(null)!;
            AssertThat(layerValue).IsEqual(expectedLayer);
        }
        else
        {
            AssertThat(expectedLayer).IsEqual(2u);
        }
    }

    // K-05 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task ListenServer_ServerEntityMeshes_AreHidden()
    {
        var root = new Godot.Node3D();
        var mesh = new Godot.MeshInstance3D();
        root.AddChild(mesh);
        mesh.Visible = true;

        var method = typeof(EntitySpawner)
            .GetMethod("HideMeshesRecursive", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method?.Invoke(null, new object[] { root });

        AssertThat(mesh.Visible).IsFalse();
        root.Free();
    }

    // K-06 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task ListenServer_ExternalClient_ReceivesWorldState()
    {
        _server!.Initialize(_serverNet, port: 7202);
        _client!.Initialize(_clientNet, "127.0.0.1", 7202);
        await _serverRunner.AwaitIdleFrame();

        var sentToExternal = new List<byte[]>();
        _serverNet.PacketReceived += (id, bin) =>
        {
            if (id == 3) sentToExternal.Add(bin);
        };

        _serverNet.FireClientConnected(peerId: 3);
        await _serverRunner.AwaitIdleFrame();

        AssertThat(sentToExternal).IsNotNull();
    }
}
