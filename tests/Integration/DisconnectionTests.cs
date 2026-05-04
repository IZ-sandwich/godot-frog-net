using System.Threading.Tasks;
using GdUnit4;
using MonkeNet.Client;
using MonkeNet.Server;
using MonkeNet.Serializer;
using MonkeNet.Tests.Infrastructure;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// D-01..D-04: Disconnection tests.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class DisconnectionTests
{
    private ISceneRunner _serverRunner;
    private ISceneRunner _clientRunner;
    private FakeNetworkEndpoint _serverNet;
    private FakeNetworkEndpoint _clientNet;
    private ServerManager _server;
    private ClientManager _client;

    [BeforeTest]
    public async Task SetUp()
    {
        FakeNetworkBridge.Reset();
        MessageSerializer.RegisterNetworkMessages();
        (_serverNet, _clientNet) = FakeNetworkBridge.CreatePair();

        _serverRunner = ISceneRunner.Load("res://addons/monke-net/scenes/ServerManager.tscn", autoFree: true);
        await _serverRunner.AwaitIdleFrame();
        _server = _serverRunner.Scene() as ServerManager;

        _clientRunner = ISceneRunner.Load("res://addons/monke-net/scenes/ClientManager.tscn", autoFree: true);
        await _clientRunner.AwaitIdleFrame();
        _client = _clientRunner.Scene() as ClientManager;

        _server!.Initialize(_serverNet, port: 7700);
        _client!.Initialize(_clientNet, "127.0.0.1", 7700);
        await _clientRunner.AwaitIdleFrame();
    }

    [AfterTest]
    public void TearDown()
    {
        _serverRunner?.Dispose();
        _clientRunner?.Dispose();
    }

    // D-01 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Server_EmitsClientDisconnected_WhenPeerDrops()
    {
        var sa = AssertSignal(_server);
        _serverNet.FireClientDisconnected(peerId: 2);

        await sa.IsEmitted(ServerManager.SignalName.ClientDisconnected, (Godot.Variant)2).WithTimeout(2000);
    }

    // D-02 ─────────────────────────────────────────────────────────────────────
    // Regression: ClientManager was emitting ServerDisconnected for ALL disconnects.
    // Unexpected server drops must emit ConnectionLost so the UI can show the right message.
    [TestCase]
    public async Task Client_EmitsConnectionLost_WhenServerDropsUnexpectedly()
    {
        var sa = AssertSignal(_client);
        _clientNet.SimulateServerDisconnected();

        await sa.IsEmitted(ClientManager.SignalName.ConnectionLost).WithTimeout(2000);
        AssertThat(_client.IsNetworkReady).IsFalse();
    }

    // D-05 ─────────────────────────────────────────────────────────────────────
    // Voluntary Disconnect() must emit ServerDisconnected (not ConnectionLost).
    [TestCase]
    public async Task Client_EmitsServerDisconnected_OnVoluntaryDisconnect()
    {
        var sa = AssertSignal(_client);
        _client.Disconnect();

        await sa.IsEmitted(ClientManager.SignalName.ServerDisconnected).WithTimeout(2000);
        AssertThat(_client.IsNetworkReady).IsFalse();
    }

    // D-03 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Client_DoesNotEmitServerDisconnected_WhenNotConnected()
    {
        // Fire two consecutive disconnects. The first transitions _serverConnected to false;
        // the second should be suppressed (duplicate) and not emit ServerDisconnected again.
        _clientNet.SimulateServerDisconnected();
        await _clientRunner.AwaitIdleFrame();

        var sa = AssertSignal(_client);
        _clientNet.SimulateServerDisconnected();

        await sa.IsNotEmitted(ClientManager.SignalName.ServerDisconnected).WithTimeout(500);
    }

    // D-04 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Server_DisconnectClient_RoutesToNetworkManager()
    {
        int sentClientId = 0;
        _serverNet.ClientDisconnected += id => sentClientId = (int)id;

        _server.DisconnectClient(clientId: 2);

        AssertThat(sentClientId).IsEqual(2);
    }
}
