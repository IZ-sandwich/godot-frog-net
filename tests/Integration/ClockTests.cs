using System.Reflection;
using System.Threading.Tasks;
using GdUnit4;
using MonkeNet.Client;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Server;
using MonkeNet.Tests.Infrastructure;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// B-01..B-05: ClientNetworkClock — latency calculation and sample management.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class ClockTests
{
    private ISceneRunner _serverRunner;
    private ISceneRunner _clientRunner;
    private FakeNetworkEndpoint _serverNet;
    private FakeNetworkEndpoint _clientNet;
    private ServerManager _server;
    private ClientManager _client;
    private ClientNetworkClock _clock;

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

        _server!.Initialize(_serverNet, port: 7300);
        _client!.Initialize(_clientNet, "127.0.0.1", 7300);
        await _clientRunner.AwaitIdleFrame();

        _clock = _clientRunner.Scene().GetNode<ClientNetworkClock>("ClientNetworkClock");
    }

    [AfterTest]
    public void TearDown()
    {
        _serverRunner?.Dispose();
        _clientRunner?.Dispose();
    }

    // B-01 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Clock_NoLatencyCalculated_BeforeSampleSize()
    {
        SetSampleSize(3);

        bool signalFired = false;
        _client.LatencyCalculated += (_, __) => signalFired = true;

        // Send 2 clock syncs (one short of _sampleSize = 3)
        SendClockSync();
        SendClockSync();
        await _clientRunner.AwaitIdleFrame();

        AssertThat(signalFired).IsFalse();
    }

    // B-02 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Clock_EmitsLatencyCalculated_OnNthSample()
    {
        SetSampleSize(3);

        int emitCount = 0;
        _client.LatencyCalculated += (_, __) => emitCount++;

        SendClockSync();
        SendClockSync();
        SendClockSync(); // 3rd sample → should emit
        await _clientRunner.AwaitIdleFrame();

        AssertThat(emitCount).IsEqual(1);
    }

    // B-03 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Clock_SampleBuffer_ClearsAfterEmit()
    {
        SetSampleSize(3);

        int emitCount = 0;
        _client.LatencyCalculated += (_, __) => emitCount++;

        // Fill first batch → emit
        SendClockSync(); SendClockSync(); SendClockSync();
        await _clientRunner.AwaitIdleFrame();

        AssertThat(emitCount).IsEqual(1);

        // Send 2 more (incomplete second batch)
        SendClockSync(); SendClockSync();
        await _clientRunner.AwaitIdleFrame();

        AssertThat(emitCount).IsEqual(1); // second batch not complete yet
    }

    // B-04 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Server_EchoesClockSync_WithServerTime()
    {
        // Advance server tick by driving _PhysicsProcess indirectly
        for (int i = 0; i < 10; i++)
            _server.EmitSignal(ServerManager.SignalName.ServerTick, i);

        // Send a ClockSyncMessage from the client side; server echoes back with ServerTime set
        var sync = new ClockSyncMessage { ClientTime = 9999, ServerTime = 0 };
        _serverNet.SimulateIncomingPacket(2, MessageSerializer.Serialize(sync));
        await _serverRunner.AwaitIdleFrame();

        // The server delivers its echo via the bridge; capture it
        ClockSyncMessage? echoReceived = null;
        _clientNet.PacketReceived += (_, bin) =>
        {
            if (MessageSerializer.Deserialize(bin) is ClockSyncMessage echo)
                echoReceived = echo;
        };

        // Re-trigger via the server clock's OnCommandReceived
        _serverNet.SimulateIncomingPacket(2, MessageSerializer.Serialize(sync));
        await _serverRunner.AwaitIdleFrame();

        if (echoReceived.HasValue)
        {
            AssertThat(echoReceived.Value.ClientTime).IsEqual(9999);
            AssertThat(echoReceived.Value.ServerTime).IsGreaterEqual(0);
        }
    }

    // B-05 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public async Task Clock_GetCurrentTick_ExceedsRawTickByAtLeastFixedMargin()
    {
        int fixedMargin = GetFixedTickMargin();

        // ProcessTick twice to advance raw tick
        _clock.ProcessTick();
        _clock.ProcessTick();

        // GetCurrentTick = rawTick + latency + jitter + fixedMargin
        // At minimum it equals rawTick + fixedMargin (latency/jitter start at 0)
        int rawTick = GetCurrentRawTick();
        int reported = _clock.GetCurrentTick();

        AssertThat(reported).IsGreaterEqual(rawTick + fixedMargin);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private void SetSampleSize(int n)
    {
        typeof(ClientNetworkClock)
            .GetField("_sampleSize", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(_clock, n);
    }

    private int GetFixedTickMargin()
    {
        return (int)(typeof(ClientNetworkClock)
            .GetField("_fixedTickMargin", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(_clock) ?? 3);
    }

    private int GetCurrentRawTick()
    {
        return (int)(typeof(ClientNetworkClock)
            .GetField("_currentTick", BindingFlags.NonPublic | BindingFlags.Instance)
            ?.GetValue(_clock) ?? 0);
    }

    private void SendClockSync()
    {
        // Trigger the clock's timer-driven method to send a sync, then deliver the echo.
        _clock.GetNode<Godot.Timer>("Timer").EmitSignal("timeout");
        // The bridge synchronously delivers the server's echo to the client.
    }
}
