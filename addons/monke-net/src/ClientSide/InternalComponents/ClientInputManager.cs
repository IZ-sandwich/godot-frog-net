using Godot;
using ImGuiNET;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;
using System.Collections.Generic;

namespace MonkeNet.Client;

/// <summary>
/// Reads and transmits inputs to the server.
/// Will adjust and send redundant inputs to compensate for bad network conditions.
/// </summary>
[GlobalClass]
public partial class ClientInputManager : InternalClientComponent
{
    // Maximum number of redundant inputs packed into a single unreliable packet.
    // ENet's unreliable MTU is 1392 bytes; exceeding it causes higher packet loss.
    // 15 ticks ≈ 250ms at 60Hz — enough redundancy for realistic packet loss scenarios.
    [Export] public int MaxRedundantInputs { get => _maxRedundantInputs; set => _maxRedundantInputs = value; }
    private int _maxRedundantInputs = 15;

    /// <summary>
    /// Number of ticks the owner buffers an input before applying it locally.
    /// The input read at owner tick T is sent to the server tagged for tick
    /// <c>T + InputDelayTicks</c>; the owner's own prediction at local tick T
    /// applies the input that was read at <c>T - InputDelayTicks</c>. The delay
    /// gives the input time to travel owner → server → observers before any of
    /// those peers' clocks reach the apply tick — observers can then learn the
    /// upcoming input via snapshot history and predict the same tick using the
    /// same input the owner used, eliminating the per-direction-change
    /// reconcile that "latest snapshot input is one tick stale" produces.
    ///
    /// Default 2 ticks (≈ 33ms at 60Hz). Set to 0 to disable the buffer
    /// (input is applied immediately, restoring the pre-delay behaviour).
    /// Higher values absorb more network jitter at the cost of additional
    /// local input latency on the owner's own player.
    /// </summary>
    [Export] public int InputDelayTicks { get; set; } = 2;

    private readonly List<ProducedInputForTick> _producedInputs = [];
    private int _lastReceivedTick = 0;

    /// <summary>
    /// Generate this tick's input via the registered producer, store it in the
    /// outbound history queue, transmit recent inputs to the server, and return
    /// the input scheduled to APPLY this tick locally. With
    /// <see cref="InputDelayTicks"/> &gt; 0 the returned value is the input the
    /// producer generated <c>InputDelayTicks</c> ticks ago — the prediction
    /// loop applies that, while the freshly-generated input is queued to apply
    /// at <c>currentTick + InputDelayTicks</c> (and is already on its way to
    /// the server tagged for that tick).
    /// </summary>
    public IPackableElement GenerateAndTransmitInputs(int currentTick)
    {
        IPackableElement input = MonkeNetConfig.Instance?.InputProducer?.GenerateCurrentInput();

        if (input == null)
        {
            return null;
        }

        // Tag this input with its target APPLY tick: server applies it at this
        // tick, owner applies it locally at this tick, observers replay it at
        // this tick. The server stamps incoming PackedClientInputMessage
        // entries using its trailing-Tick field, so sending the apply-tick as
        // the message's Tick value flows the correct stamp down to
        // ServerInputReceiver._pendingInputs unchanged.
        int applyTick = currentTick + InputDelayTicks;
        ProducedInputForTick producedInput = new()
        {
            Tick = applyTick,
            Input = input
        };

        _producedInputs.Add(producedInput);
        SendInputsToServer(applyTick);

        // Return the input scheduled to APPLY at this tick (read InputDelayTicks
        // ticks ago). With delay = 0 this resolves to the just-generated input,
        // matching the pre-delay behaviour exactly. During the first
        // InputDelayTicks ticks of the session no scheduled input exists yet,
        // so the prediction loop runs with null input (entities use defaults /
        // coast) until the queue fills — equivalent to the engine's existing
        // pre-first-input handling.
        return FindInputForTick(currentTick);
    }

    private IPackableElement FindInputForTick(int tick)
    {
        for (int i = 0; i < _producedInputs.Count; i++)
        {
            if (_producedInputs[i].Tick == tick) return _producedInputs[i].Input;
        }
        return null;
    }

    // Pack and send current input + recent non-acked inputs (redundant inputs).
    // Capped at MaxRedundantInputs to stay within the unreliable MTU. The
    // <paramref name="latestApplyTick"/> is the server tick at which the newest
    // input in this batch should APPLY (= producer's current tick +
    // InputDelayTicks); the server stamps each batched input as
    // <c>latestApplyTick − offset</c>, so the per-tick apply schedule flows
    // through unchanged for any delay value.
    private void SendInputsToServer(int latestApplyTick)
    {
        int start = System.Math.Max(0, _producedInputs.Count - _maxRedundantInputs);
        int count = _producedInputs.Count - start;

        var userCmd = new PackedClientInputMessage
        {
            Tick = latestApplyTick,
            Inputs = new IPackableElement[count]
        };

        for (int i = 0; i < count; i++)
        {
            userCmd.Inputs[i] = _producedInputs[start + i].Input;
        }

        MonkeLogger.Debug($"[NET-INPUT-TX] latestApplyTick={latestApplyTick} batch={count} latest={(count > 0 ? userCmd.Inputs[count - 1].ToString() : "")}");

        SendCommandToServer(userCmd, INetworkManager.PacketModeEnum.Unreliable, (int)ChannelEnum.ClientInput);
    }

    // When we receive a snapshot back, we delete all inputs prior/equal to it since those were already processed.
    protected override void OnCommandReceived(IPackableMessage command)
    {
        if (command is GameSnapshotMessage snapshot && snapshot.Tick > _lastReceivedTick)
        {
            int beforeCount = _producedInputs.Count;
            _lastReceivedTick = snapshot.Tick;
            _producedInputs.RemoveAll(input => input.Tick <= snapshot.Tick);
            MonkeLogger.Debug($"[NET-INPUT-ACK] ackedTick={snapshot.Tick} dropped={beforeCount - _producedInputs.Count} pending={_producedInputs.Count}");
        }
    }

    public void DisplayDebugInformation()
    {
        if (ImGui.CollapsingHeader("Input Manager"))
        {
            ImGui.Text($"Redundant Inputs: {_producedInputs.Count}");
        }
    }

    private struct ProducedInputForTick
    {
        public int Tick;
        public IPackableElement Input;
    }
}
