using System.Threading.Tasks;
using GdUnit4;
using MonkeNet.Client;
using MonkeNet.Serializer;
using MonkeNet.Tests.Infrastructure;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// E-01..E-03: Client-side silence-flag tests.
/// Disconnect authority belongs to ENet; the monitor only emits quality signals.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class ClientTimeoutTests
{
    private ISceneRunner _clientRunner;
    private ClientManager _client;
    private FakeNetworkEndpoint _serverNet;
    private FakeNetworkEndpoint _clientNet;
    private ClientConnectionMonitor _monitor;
    private FakeTimestampProvider _clock;

    [BeforeTest]
    public async Task SetUp()
    {
        FakeNetworkBridge.Reset();
        MessageSerializer.RegisterNetworkMessages();
        (_serverNet, _clientNet) = FakeNetworkBridge.CreatePair();

        _clientRunner = ISceneRunner.Load("res://addons/monke-net/scenes/ClientManager.tscn", autoFree: true);
        await _clientRunner.AwaitIdleFrame();

        _client = _clientRunner.Scene() as ClientManager;
        _client!.Initialize(_clientNet, "127.0.0.1", 7800);
        await _clientRunner.AwaitIdleFrame();

        _monitor = _client.GetNode<ClientConnectionMonitor>("ClientConnectionMonitor");
        _clock = new FakeTimestampProvider { CurrentMs = 0 };
        _monitor.TimestampProvider = _clock;

        _monitor.SilenceThresholdSec = 3f;
    }

    [AfterTest]
    public void TearDown()
    {
        _clientRunner?.Dispose();
    }

    // E-01 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task NoAction_InIdleState_EvenAfterSilenceThreshold()
    {
        var sa = AssertSignal(_client);
        _clock.CurrentMs = 10_000;
        _monitor._Process(0.1);

        await sa.IsNotEmitted(ClientManager.SignalName.ServerSilent).WithTimeout(500);
        await sa.IsNotEmitted(ClientManager.SignalName.ConnectionLost).WithTimeout(500);
    }

    // E-02 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task ServerSilent_EmittedAfterSilenceThreshold()
    {
        DeliverServerMessage();
        _clock.CurrentMs = 0;

        var sa = AssertSignal(_client);
        _clock.AdvanceBy(3_001);
        _monitor._Process(0.1);

        await sa.IsEmitted(ClientManager.SignalName.ServerSilent).WithTimeout(2000);
    }

    // E-03 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task ServerResponded_EmittedWhenMessageArrivesAfterSilence()
    {
        DeliverServerMessage();
        _clock.AdvanceBy(3_001);
        _monitor._Process(0.1); // triggers ServerSilent

        var sa = AssertSignal(_client);
        DeliverServerMessage(); // arrives — clears silence flag

        await sa.IsEmitted(ClientManager.SignalName.ServerResponded).WithTimeout(2000);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private void DeliverServerMessage()
    {
        var syncMsg = new MonkeNet.NetworkMessages.ClockSyncMessage { ClientTime = 0, ServerTime = 0 };
        byte[] bytes = MessageSerializer.Serialize(syncMsg);
        _clientNet.SimulateIncomingPacket(1, bytes);
        _clock.CurrentMs = (ulong)Godot.Time.GetTicksMsec();
    }
}
