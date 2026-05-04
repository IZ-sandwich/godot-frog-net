using System.Linq;
using GdUnit4;
using MonkeNet.Shared;
using MonkeNet.Tests.Infrastructure;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Unit;

/// <summary>
/// N-01, N-02: FakeNetworkEndpoint — GetConnectedPeerIds / GetPeerRtt, no Godot runtime required.
/// </summary>
[TestSuite]
public class NetworkManagerTests
{
    // N-01 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void FakeEndpoint_GetConnectedPeerIds_ReflectsConnectedClients()
    {
        FakeNetworkBridge.Reset();
        var (serverNet, _) = FakeNetworkBridge.CreatePair();

        serverNet.FireClientConnected(5);
        AssertThat(serverNet.GetConnectedPeerIds().Contains(5)).IsTrue();

        serverNet.FireClientDisconnected(5);
        AssertThat(serverNet.GetConnectedPeerIds().Contains(5)).IsFalse();
    }

    // N-02 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void FakeEndpoint_GetPeerRtt_ReturnsZero()
    {
        FakeNetworkBridge.Reset();
        var (serverNet, _) = FakeNetworkBridge.CreatePair();

        AssertThat(serverNet.GetPeerRtt(99)).IsEqual(0);
    }

    // N-03 ─────────────────────────────────────────────────────────────────────
    // Regression: NetworkManagerEnet.Disconnect() was not clearing _connectedPeers,
    // causing stale peer IDs to leak into the next server session and produce
    // "peers.has(p_id) is not true" errors when broadcasting to a fresh ENet host.
    [TestCase]
    public void FakeEndpoint_Disconnect_ClearsConnectedPeers()
    {
        FakeNetworkBridge.Reset();
        var (serverNet, _) = FakeNetworkBridge.CreatePair();

        serverNet.FireClientConnected(5);
        serverNet.FireClientConnected(6);
        AssertThat(serverNet.GetConnectedPeerIds().Count).IsEqual(2);

        serverNet.Disconnect();

        AssertThat(serverNet.GetConnectedPeerIds().Count).IsEqual(0);
    }

    // N-04 ─────────────────────────────────────────────────────────────────────
    // Regression: NetworkManagerEnet.CreateServer() was not clearing _connectedPeers,
    // so stale peer IDs from a previous session survived into the new one.
    [TestCase]
    public void FakeEndpoint_CreateServer_ClearsStaleConnectedPeers()
    {
        FakeNetworkBridge.Reset();
        var (serverNet, _) = FakeNetworkBridge.CreatePair();

        serverNet.FireClientConnected(5);
        AssertThat(serverNet.GetConnectedPeerIds().Contains(5)).IsTrue();

        serverNet.CreateServer(7700);

        AssertThat(serverNet.GetConnectedPeerIds().Count).IsEqual(0);
    }

    // N-05 ─────────────────────────────────────────────────────────────────────
    // Full stop-restart cycle: confirms peer IDs from session 1 are not visible
    // in session 2 after Disconnect() + CreateServer().
    [TestCase]
    public void FakeEndpoint_StopRestart_NoStalePeersInNewSession()
    {
        FakeNetworkBridge.Reset();
        var (serverNet, _) = FakeNetworkBridge.CreatePair();

        // Session 1: server accepts client 5
        serverNet.CreateServer(7700);
        serverNet.FireClientConnected(5);
        AssertThat(serverNet.GetConnectedPeerIds().Contains(5)).IsTrue();

        // Stop server
        serverNet.Disconnect();

        // Restart server (mimics MonkeNetManager.CreateServer calling NetworkManagerEnet.CreateServer)
        serverNet.CreateServer(7700);

        // Session 2: only new clients, no stale peers from session 1
        serverNet.FireClientConnected(10);
        AssertThat(serverNet.GetConnectedPeerIds().Contains(5)).IsFalse();
        AssertThat(serverNet.GetConnectedPeerIds().Contains(10)).IsTrue();
    }
}
