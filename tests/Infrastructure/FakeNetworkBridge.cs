namespace MonkeNet.Tests.Infrastructure;

/// <summary>
/// Factory that creates a wired pair of <see cref="FakeNetworkEndpoint"/> instances.
/// Packets sent on one endpoint are synchronously delivered to the paired endpoint
/// without any ENet or OS socket involvement.
/// </summary>
public static class FakeNetworkBridge
{
    private static int _nextClientId = 2;

    /// <summary>
    /// Creates a matched (serverEndpoint, clientEndpoint) pair.
    /// Server endpoint has networkId=1; client endpoint gets the next available id (2, 3, ...).
    /// Call <c>serverEndpoint.CreateServer(port)</c> then <c>clientEndpoint.CreateClient(addr, port)</c>
    /// to simulate the ENet handshake and trigger both <c>ClientConnected</c> events.
    /// </summary>
    public static (FakeNetworkEndpoint Server, FakeNetworkEndpoint Client) CreatePair()
    {
        int clientId = _nextClientId++;
        var server = new FakeNetworkEndpoint(networkId: 1);
        var client = new FakeNetworkEndpoint(networkId: clientId);
        server.SetPeer(client);
        client.SetPeer(server);
        return (server, client);
    }

    /// <summary>Resets the client-id counter between test runs.</summary>
    public static void Reset() => _nextClientId = 2;
}
