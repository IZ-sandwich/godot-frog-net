using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace MonkeNet.Tests.Infrastructure;

/// <summary>
/// In-process UDP relay used by the quantitative test suite to inject latency,
/// jitter, and packet loss between the client(s) and the ENet server. The relay
/// runs entirely inside the orchestrator process — no admin rights, no
/// platform-specific tooling, no separate process.
///
/// <para>
/// Topology: client → 127.0.0.1:<see cref="ListenPort"/> (relay) → 127.0.0.1:<see cref="ServerPort"/>.
/// The relay binds one outbound socket per distinct client endpoint (standard
/// NAT-style forwarder), so multiple clients can share one server through one
/// relay instance.
/// </para>
///
/// <para>
/// Each packet's delivery is scheduled at <c>now + latencyMs + Uniform(-jitter, +jitter)</c>
/// and independently dropped with probability <c>lossRate</c>. The scheduler
/// runs on a single background thread per direction with a priority queue keyed
/// by delivery time — packets are delivered when their wakeup time elapses, not
/// strictly in arrival order (jitter can swap adjacent packets).
/// </para>
///
/// <para>
/// Conditions can be changed at any time via <see cref="SetConditions"/>; the
/// change takes effect for all subsequently-received packets and does not
/// affect packets already enqueued for delivery.
/// </para>
/// </summary>
public sealed class UdpRelay : IDisposable
{
    /// <summary>The UDP port clients should connect to (replaces the server's
    /// real port in the client's CLI args).</summary>
    public int ListenPort { get; }

    /// <summary>The real ENet server's UDP port. The relay forwards client → server
    /// traffic here.</summary>
    public int ServerPort { get; }

    private readonly UdpClient _listenSocket;
    private readonly object _condLock = new();
    private int _latencyMs;
    private int _jitterMs;
    private float _lossRate;
    private readonly Random _rng = new();

    // Per-client-endpoint forwarder. Each unique client UDP source endpoint
    // gets its own outbound socket to the server so we can route the server's
    // replies back to the right client. Same model a NAT box uses.
    private readonly Dictionary<IPEndPoint, ClientLink> _links = new();
    private readonly object _linksLock = new();

    private readonly Thread _listenThread;
    private readonly Thread _delayThreadInbound;
    private volatile bool _disposed;

    // Priority queue for inbound (client → server) delayed packets. Keyed by
    // delivery time (DateTime.UtcNow ticks). One queue per direction so the
    // worker threads can sleep on a single ManualResetEvent per direction.
    private readonly PriorityQueue<PendingPacket, long> _inboundQueue = new();
    private readonly object _inboundQueueLock = new();
    private readonly ManualResetEventSlim _inboundQueueSignal = new(initialState: false);

    public UdpRelay(int serverPort)
    {
        ServerPort = serverPort;

        // Bind ephemeral local UDP port (port 0 = OS-assigned). Read the actual
        // port back from LocalEndPoint after Bind. This matches the pattern
        // used by MultiProcessTestBase.NextPort.
        _listenSocket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        ListenPort = ((IPEndPoint)_listenSocket.Client.LocalEndPoint!).Port;

        // Default to zero conditions — relay is transparent until SetConditions
        // is called. This makes the relay safe to wire in unconditionally even
        // for baseline (C0) test runs.
        _latencyMs = 0;
        _jitterMs = 0;
        _lossRate = 0f;

        _listenThread = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = $"UdpRelay-Listen-{ListenPort}",
        };
        _delayThreadInbound = new Thread(InboundDelayLoop)
        {
            IsBackground = true,
            Name = $"UdpRelay-Delay-IN-{ListenPort}",
        };
        _listenThread.Start();
        _delayThreadInbound.Start();
    }

    /// <summary>Sets the simulated network conditions applied to every subsequent
    /// packet. Safe to call from any thread; takes effect immediately for new
    /// packets but does not retroactively re-time packets already enqueued.</summary>
    /// <param name="latencyMs">One-way delivery delay in milliseconds.</param>
    /// <param name="jitterMs">Uniform random variance (±) added to latency.</param>
    /// <param name="lossRate">Independent per-packet drop probability in [0,1].</param>
    public void SetConditions(int latencyMs, int jitterMs, float lossRate)
    {
        if (latencyMs < 0) throw new ArgumentOutOfRangeException(nameof(latencyMs));
        if (jitterMs < 0) throw new ArgumentOutOfRangeException(nameof(jitterMs));
        if (lossRate < 0f || lossRate > 1f) throw new ArgumentOutOfRangeException(nameof(lossRate));
        lock (_condLock)
        {
            _latencyMs = latencyMs;
            _jitterMs = jitterMs;
            _lossRate = lossRate;
        }
    }

    private void ListenLoop()
    {
        while (!_disposed)
        {
            try
            {
                IPEndPoint src = null!;
                byte[] data = _listenSocket.Receive(ref src);
                OnPacketFromClient(src, data);
            }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) when (_disposed) { return; }
            catch (Exception)
            {
                // Best-effort — never tear down the relay on a transient socket
                // hiccup. A real failure will surface as test timeout when
                // clients can't reach the server.
            }
        }
    }

    private void OnPacketFromClient(IPEndPoint src, byte[] data)
    {
        if (ShouldDrop()) return;
        long deliveryTicks = ComputeDeliveryTicks();
        var packet = new PendingPacket
        {
            Source = src,
            Data = data,
        };
        lock (_inboundQueueLock)
        {
            _inboundQueue.Enqueue(packet, deliveryTicks);
        }
        _inboundQueueSignal.Set();
    }

    private void InboundDelayLoop()
    {
        while (!_disposed)
        {
            PendingPacket? toDeliver = null;
            int sleepMs = 50;

            lock (_inboundQueueLock)
            {
                if (_inboundQueue.TryPeek(out var top, out long whenTicks))
                {
                    long now = DateTime.UtcNow.Ticks;
                    if (whenTicks <= now)
                    {
                        _inboundQueue.Dequeue();
                        toDeliver = top;
                    }
                    else
                    {
                        sleepMs = (int)Math.Min(50, (whenTicks - now) / TimeSpan.TicksPerMillisecond + 1);
                    }
                }
                else
                {
                    sleepMs = 50;
                    _inboundQueueSignal.Reset();
                }
            }

            if (toDeliver.HasValue)
            {
                DeliverInbound(toDeliver.Value);
            }
            else
            {
                _inboundQueueSignal.Wait(sleepMs);
            }
        }
    }

    private void DeliverInbound(PendingPacket pkt)
    {
        ClientLink link = GetOrCreateLink(pkt.Source);
        try
        {
            link.OutSocket.Send(pkt.Data, pkt.Data.Length, "127.0.0.1", ServerPort);
        }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private ClientLink GetOrCreateLink(IPEndPoint clientEp)
    {
        lock (_linksLock)
        {
            if (_links.TryGetValue(clientEp, out var existing)) return existing;

            // One ephemeral UDP socket per client endpoint. The server sees
            // this socket's local port as the client identity and replies to
            // it; the receive loop on this socket forwards replies back to the
            // original client through the listen socket.
            var sock = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var link = new ClientLink
            {
                ClientEndpoint = clientEp,
                OutSocket = sock,
                Queue = new PriorityQueue<PendingPacket, long>(),
                Signal = new ManualResetEventSlim(initialState: false),
            };

            link.RecvThread = new Thread(() => ServerReplyLoop(link))
            {
                IsBackground = true,
                Name = $"UdpRelay-ReplyRecv-{((IPEndPoint)sock.Client.LocalEndPoint!).Port}",
            };
            link.DelayThread = new Thread(() => OutboundDelayLoop(link))
            {
                IsBackground = true,
                Name = $"UdpRelay-Delay-OUT-{((IPEndPoint)sock.Client.LocalEndPoint!).Port}",
            };
            link.RecvThread.Start();
            link.DelayThread.Start();

            _links[clientEp] = link;
            return link;
        }
    }

    private void ServerReplyLoop(ClientLink link)
    {
        while (!_disposed)
        {
            try
            {
                IPEndPoint src = null!;
                byte[] data = link.OutSocket.Receive(ref src);
                if (ShouldDrop()) continue;
                long deliveryTicks = ComputeDeliveryTicks();
                var pkt = new PendingPacket
                {
                    Source = src,
                    Data = data,
                };
                lock (link.Queue) link.Queue.Enqueue(pkt, deliveryTicks);
                link.Signal.Set();
            }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) when (_disposed) { return; }
            catch (Exception) { /* see ListenLoop comment */ }
        }
    }

    private void OutboundDelayLoop(ClientLink link)
    {
        while (!_disposed)
        {
            PendingPacket? toDeliver = null;
            int sleepMs = 50;

            lock (link.Queue)
            {
                if (link.Queue.TryPeek(out var top, out long whenTicks))
                {
                    long now = DateTime.UtcNow.Ticks;
                    if (whenTicks <= now)
                    {
                        link.Queue.Dequeue();
                        toDeliver = top;
                    }
                    else
                    {
                        sleepMs = (int)Math.Min(50, (whenTicks - now) / TimeSpan.TicksPerMillisecond + 1);
                    }
                }
                else
                {
                    sleepMs = 50;
                    link.Signal.Reset();
                }
            }

            if (toDeliver.HasValue)
            {
                try
                {
                    _listenSocket.Send(toDeliver.Value.Data, toDeliver.Value.Data.Length, link.ClientEndpoint);
                }
                catch (ObjectDisposedException) { }
                catch (SocketException) { }
            }
            else
            {
                link.Signal.Wait(sleepMs);
            }
        }
    }

    private bool ShouldDrop()
    {
        float rate;
        lock (_condLock) rate = _lossRate;
        if (rate <= 0f) return false;
        double sample;
        lock (_rng) sample = _rng.NextDouble();
        return sample < rate;
    }

    private long ComputeDeliveryTicks()
    {
        int latency, jitter;
        lock (_condLock) { latency = _latencyMs; jitter = _jitterMs; }
        int j;
        if (jitter <= 0) j = 0;
        else { lock (_rng) j = _rng.Next(-jitter, jitter + 1); }
        int delayMs = Math.Max(0, latency + j);
        return DateTime.UtcNow.Ticks + (long)delayMs * TimeSpan.TicksPerMillisecond;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _listenSocket.Dispose(); } catch { }
        lock (_linksLock)
        {
            foreach (var link in _links.Values)
            {
                try { link.OutSocket.Dispose(); } catch { }
                try { link.Signal.Set(); } catch { }
            }
            _links.Clear();
        }
        _inboundQueueSignal.Set();
    }

    private struct PendingPacket
    {
        public IPEndPoint Source;
        public byte[] Data;
    }

    private sealed class ClientLink
    {
        public IPEndPoint ClientEndpoint = null!;
        public UdpClient OutSocket = null!;
        public PriorityQueue<PendingPacket, long> Queue = null!;
        public ManualResetEventSlim Signal = null!;
        public Thread RecvThread = null!;
        public Thread DelayThread = null!;
    }
}
