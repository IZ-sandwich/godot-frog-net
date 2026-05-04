using Godot;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.Client;

/// <summary>
/// Monitors server-side silence and emits quality signals.
/// Idle→Connected on the first message from the server.
/// After SilenceThresholdSec of no messages, emits ServerSilent.
/// Disconnect authority belongs entirely to ENet (PeerDisconnected).
/// </summary>
[GlobalClass]
public partial class ClientConnectionMonitor : InternalClientComponent
{
    [Export] public float SilenceThresholdSec { get => _silenceThresholdSec; set => _silenceThresholdSec = value; }

    private float _silenceThresholdSec = 3f;

    // Injectable for tests; production code uses real wall-clock time.
    public ITimestampProvider TimestampProvider { get; set; } = new RealTimestampProvider();

    private enum State { Idle, Connected }

    private State _state = State.Idle;
    private ulong _lastServerMessageMsec;
    private bool _silenceWarningEmitted;

    protected override void OnCommandReceived(IPackableMessage command)
    {
        _lastServerMessageMsec = TimestampProvider.GetTicksMsec();

        switch (_state)
        {
            case State.Idle:
                _state = State.Connected;
                MonkeLogger.Info("ClientConnectionMonitor: started monitoring server connection");
                break;
            case State.Connected:
                if (_silenceWarningEmitted)
                {
                    _silenceWarningEmitted = false;
                    ClientManager.Instance.EmitSignal(ClientManager.SignalName.ServerResponded);
                }
                break;
        }
    }

    public override void _Process(double delta)
    {
        if (_state != State.Connected)
            return;

        ulong now = TimestampProvider.GetTicksMsec();
        ulong silenceMs = now - _lastServerMessageMsec;

        if (!_silenceWarningEmitted && silenceMs > (ulong)(_silenceThresholdSec * 1000))
        {
            _silenceWarningEmitted = true;
            ClientManager.Instance.EmitSignal(ClientManager.SignalName.ServerSilent);
            MonkeLogger.Info("ClientConnectionMonitor: server silence detected");
        }
    }
}
