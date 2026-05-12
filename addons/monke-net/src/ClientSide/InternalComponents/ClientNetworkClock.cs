using Godot;
using ImGuiNET;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;
using System.Collections.Generic;
using System.Linq;

namespace MonkeNet.Client;

/// <summary>
/// Syncs the clients clock with the servers one, in the process it calculates latency and other debug information.
/// </summary>
[GlobalClass]
public partial class ClientNetworkClock : InternalClientComponent
{
    // Called every time latency is calculated
    [Signal] public delegate void LatencyCalculatedEventHandler(int currentTick, int latencyAverageTicks, int jitterAverageTicks, int averageClockOffset);

    [Export] private int _sampleSize = 11;
    [Export] private float _sampleRateMs = 1000;
    [Export] private int _minLatency = 50;
    [Export] private int _fixedTickMargin = 3;

    // Fast-start: while we have fewer than this many sync replies, poll at the
    // higher fast-start rate so the clock converges within a fraction of a
    // second of connecting. After that, we drop back to the steady-state
    // _sampleRateMs cadence to limit bandwidth.
    [Export] private int _fastStartSampleCount = 6;
    [Export] private float _fastStartRateMs = 100;
    // Coarse-correction threshold. When a single sync reply estimates an
    // offset larger than this, apply it IMMEDIATELY to _currentTick instead of
    // waiting for the averaged window to fill. This is the fast-start path
    // that converges the clock within a fraction of a second of connecting
    // (Photon-Fusion-2-style). Below this threshold the offset is small enough
    // that the windowed averaged correction smooths out network jitter
    // without chasing single-sample noise.
    [Export] private int _immediateCorrectionMinAbsTicks = 10;

    private int _currentTick = 0;               // Client/Server Synced Tick
    private int _immediateLatencyMsec = 0;      // Latest Calculated Latency in Milliseconds
    private int _averageLatencyInTicks = 0;     // Average Latency in Ticks
    private int _jitterInTicks = 0;             // Latency Jitter in ticks
    private int _averageOffsetInTicks = 0;      // Average Client to Server clock offset in Ticks
    private int _lastOffset = 0;
    private int _minLatencyInTicks = 0;
    private int _samplesReceived = 0;
    private Timer _timer;

    private readonly List<int> _offsetValues = new();
    private readonly List<int> _latencyValues = new();

    public override void _Ready()
    {
        base._Ready();
        _timer = GetNode<Timer>("Timer");
        // Start at the fast-start rate; the timer drops to steady-state once
        // _fastStartSampleCount sync replies have come back (see SyncReceived).
        _timer.WaitTime = _fastStartRateMs / 1000.0f;
        _minLatencyInTicks = PhysicsUtils.MsecToTick(_minLatency);
    }

    protected override void OnCommandReceived(IPackableMessage command)
    {
        if (command is ClockSyncMessage sync)
        {
            SyncReceived(sync);
        }
    }

    public void ProcessTick()
    {
        _currentTick += 1 + _lastOffset;
        _lastOffset = 0;
    }

    public int GetCurrentTick()
    {
        return _currentTick + _averageLatencyInTicks + _jitterInTicks + _fixedTickMargin;
    }

    /// <summary>Raw local tick (no latency/jitter/margin applied).</summary>
    public int RawTick => _currentTick;
    /// <summary>Smoothed one-way latency estimate from clock-sync samples, in ticks.</summary>
    public int AverageLatencyInTicks => _averageLatencyInTicks;
    /// <summary>Jitter measured across the most recent sync window, in ticks.</summary>
    public int JitterInTicks => _jitterInTicks;
    /// <summary>Most recently applied (averaged) clock offset, in ticks. Useful for
    /// telemetry and assertion that the clock has reached steady state.</summary>
    public int AverageOffsetInTicks => _averageOffsetInTicks;
    /// <summary>Number of complete clock-sync windows applied so far. 0 until the
    /// first averaged correction has been computed.</summary>
    public int SyncWindowsApplied => _syncWindowsApplied;
    private int _syncWindowsApplied;

    private static int GetLocalTimeMs()
    {
        return (int)Time.GetTicksMsec();
    }

    private void SyncReceived(ClockSyncMessage sync)
    {
        // Latency as the difference between when the packet was sent and when it came back divided by 2
        _immediateLatencyMsec = (GetLocalTimeMs() - sync.ClientTime) / 2;
        int immediateLatencyInTicks = PhysicsUtils.MsecToTick(_immediateLatencyMsec);

        // Time difference between our clock and the server clock accounting for latency
        int immediateOffsetInTicks = (sync.ServerTime - _currentTick) + immediateLatencyInTicks;

        _offsetValues.Add(immediateOffsetInTicks);
        _latencyValues.Add(immediateLatencyInTicks);
        _samplesReceived++;

        // Photon-Fusion-2-style fast correction: when this single sample
        // estimates a large clock offset, apply it IMMEDIATELY to _currentTick
        // instead of waiting for the averaged window to fill. This converges
        // the clock within a few hundred ms of connecting (during which the
        // raw client tick can be off by tens of ticks). Once the gap is small,
        // we drop into the windowed-average path below for smooth steady-state
        // tracking that's resistant to per-sample network jitter.
        if (System.Math.Abs(immediateOffsetInTicks) >= _immediateCorrectionMinAbsTicks)
        {
            _lastOffset = immediateOffsetInTicks;
            // Best-effort latency estimate for GetCurrentTick(): use the
            // immediate value until the first averaged window provides a
            // smoothed one. Otherwise GetCurrentTick is off by ~latency ticks.
            if (_averageLatencyInTicks == 0)
                _averageLatencyInTicks = immediateLatencyInTicks;
            // The just-applied offset will land on _currentTick in the next
            // ProcessTick(); drop the buffered samples so the upcoming averaged
            // window doesn't re-add the same big pre-correction offsets on top
            // of the immediate correction (causing a step backward right when
            // the first window completes). The next window will fill purely
            // from post-correction samples.
            _offsetValues.Clear();
            _latencyValues.Clear();
            return;
        }

        // Once we have enough samples for fast-start to be over, drop the
        // timer to its steady-state rate. The timer is currently firing at
        // _fastStartRateMs; switching it after the warm-up reduces the
        // bandwidth + processing cost of clock sync to ~1 packet/s.
        if (_samplesReceived == _fastStartSampleCount && _timer != null)
        {
            _timer.WaitTime = _sampleRateMs / 1000.0f;
        }

        if (_offsetValues.Count >= _sampleSize)
        {
            // Calculate average clock offset for the lasts n samples
            _offsetValues.Sort();
            _averageOffsetInTicks = SimpleAverage(_offsetValues);
            _lastOffset = _averageOffsetInTicks; // For adjusting the clock

            // Calculate average latency for the lasts n samples
            _latencyValues.Sort();
            _jitterInTicks = _latencyValues[^1] - _latencyValues[0];
            _averageLatencyInTicks = SmoothAverage(_latencyValues, _minLatencyInTicks);

            EmitSignal(SignalName.LatencyCalculated, GetCurrentTick(), _averageLatencyInTicks, _jitterInTicks, _averageOffsetInTicks);

            _offsetValues.Clear();
            _latencyValues.Clear();
            _syncWindowsApplied++;
        }
    }

    private static int SimpleAverage(List<int> samples)
    {
        return (int)samples.Average();
    }

    private static int SmoothAverage(List<int> samplesSorted, int minValue)
    {
        if (samplesSorted == null || samplesSorted.Count == 0)
            return minValue;

        int median = samplesSorted[samplesSorted.Count / 2];
        int threshold = System.Math.Max(2 * median, minValue);

        long sum = 0;
        int count = 0;

        foreach (var v in samplesSorted)
        {
            if (v <= threshold)
            {
                sum += v;
                count++;
            }
        }

        return count == 0 ? median : (int)(sum / count);
    }

    //Called every _sampleRateMs
    private void OnTimerOut()
    {
        var sync = new ClockSyncMessage
        {
            ClientTime = GetLocalTimeMs(),
            ServerTime = 0
        };

        SendCommandToServer(sync, INetworkManager.PacketModeEnum.Unreliable, (int)ChannelEnum.Clock);
    }

    public void DisplayDebugInformation()
    {
        if (ImGui.CollapsingHeader("Network Clock"))
        {
            ImGui.Text($"Synced Tick {GetCurrentTick()}");
            ImGui.Text($"Immediate Latency {_immediateLatencyMsec}ms");
            ImGui.Text($"Average Latency {_averageLatencyInTicks} ticks");
            ImGui.Text($"Latency Jitter {_jitterInTicks} ticks");
            ImGui.Text($"Average Offset {_averageOffsetInTicks} ticks");
        }
    }
}