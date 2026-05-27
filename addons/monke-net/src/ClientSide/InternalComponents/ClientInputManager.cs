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

    // --- Server-feedback driven input-delay adaptation (Option C) -------
    // When enabled, <see cref="OnServerInputBufferReport"/> drives
    // InputDelayTicks toward a target band based on the server's observed
    // input-buffer depth. Off by default: changing input-delay mid-game
    // changes the player's input-feel, which is a perceptible artifact the
    // game might prefer to handle explicitly (or pin to a fixed value).
    // Equivalent in spirit to Photon Quantum's dynamic InputOffset
    // adjustment — see notes in <see cref="OnServerInputBufferReport"/>.
    [Export] public bool InputDelayAutoAdjust { get; set; } = true;
    [Export] public int MinInputDelay { get; set; } = 0;
    [Export] public int MaxInputDelay { get; set; } = 30;
    /// <summary>Gain applied when buffer is too SHALLOW (need to increase
    /// InputDelayTicks). Higher = faster response to lag. err is in ticks,
    /// so err=10 with gain=0.1 accumulates 1.0 in one sample = one bump up.
    /// Sized for ~33ms response time at 30Hz snapshot rate under severe
    /// lag.</summary>
    [Export] public float InputDelayIncreaseGain { get; set; } = 0.1f;
    /// <summary>Gain applied when buffer is too DEEP (could decrease
    /// InputDelayTicks). Deliberately much smaller than IncreaseGain — a
    /// transient surplus shouldn't immediately strip away the safety margin
    /// the loop just built up. Photon Quantum / Source CClockDriftMgr use
    /// the same asymmetric profile: react fast to starvation, recover slow
    /// from over-buffering. Default 5× slower than IncreaseGain.</summary>
    [Export] public float InputDelayDecreaseGain { get; set; } = 0.02f;
    /// <summary>Safety ticks ABOVE jitter the auto-adjuster aims to keep in
    /// the server-side input buffer. Bigger = more jitter tolerance, more
    /// input lag. <c>jitter + InputDelaySafetyTicks</c> is the target.</summary>
    [Export] public int InputDelaySafetyTicks { get; set; } = 2;
    /// <summary>Deadband in ticks around the target. <c>|err| ≤ this</c> is
    /// treated as zero and contributes nothing to the accumulator. Without
    /// a deadband, the asymmetric increase/decrease gain ratio causes the
    /// accumulator to drift UP under balanced ±1 noise (each +1 err
    /// contributes 5× the magnitude of a balancing −1 err), so
    /// InputDelayTicks ratchets up even when the buffer is healthy.
    /// Deadband of 1 means: anything from <c>target−1</c> to <c>target+1</c>
    /// is fine, no adjustment fires. Outside the band, the gain applies to
    /// the excess past the band edge.</summary>
    [Export] public int InputDelayDeadbandTicks { get; set; } = 1;
    /// <summary>Number of feedback samples to skip at session start before
    /// allowing the auto-adjuster to move <c>InputDelayTicks</c>. The
    /// clock-sync layer needs a few sync cycles to converge to within
    /// ±1 tick of the server, and until it does the <c>bufferDepth</c>
    /// signal is dominated by the cold-start offset error rather than
    /// actual buffer health. Conditions with non-zero latency (C1+) showed
    /// a multi-tick negative bufferDepth at scenario start that the loop
    /// would otherwise interpret as severe under-buffering and ramp
    /// <c>InputDelayTicks</c> to 10-20 within the first second — and the
    /// slow decrease gain couldn't undo it. 30 samples ≈ 1 s at 30 Hz
    /// snapshot rate. Same warm-up shape Photon Quantum / Overwatch use
    /// for their input-offset adjustment.</summary>
    [Export] public int InputDelayWarmupSamples { get; set; } = 30;
    /// <summary>Resting value the auto-adjuster decays back toward when
    /// <c>InputDelayTicks</c> is above this and the buffer is healthy
    /// (effectiveErr stays at 0 for <see cref="InputDelayDecayThresholdSamples"/>
    /// consecutive samples). Without this decay, the loop could ramp UP
    /// to absorb a transient spike and then get stuck there — once the
    /// spike passes, err settles in the deadband so neither the asymmetric
    /// gains nor the regular feedback fires a DOWN bump. The decay
    /// provides a one-way "trickle back home" path. Default 2 matches the
    /// default <c>InputDelayTicks</c> value; games that pin a different
    /// value should set this to match.</summary>
    [Export] public int InputDelayDefault { get; set; } = 2;
    /// <summary>How many consecutive in-deadband samples to wait before
    /// the decay-toward-default ticks <c>InputDelayTicks</c> down by 1.
    /// 60 samples ≈ 2 s of healthy buffer at 30 Hz snapshot rate. From a
    /// stuck-at-8 state, the system decays back to 2 over ~12 s of stable
    /// conditions — fast enough to recover within a 15 s scenario, slow
    /// enough that real intermittent lag (which keeps producing err > 0
    /// and resetting the stability counter) doesn't strip the safety
    /// margin away.</summary>
    [Export] public int InputDelayDecayThresholdSamples { get; set; } = 60;
    private float _inputDelayAdjustAccum = 0f;
    private int _inputDelayFeedbackSamples = 0;
    private int _inputDelayStableSamples = 0;

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

    /// <summary>
    /// Server-feedback hook (Option C). Called per snapshot by the
    /// prediction manager with the server's observed input-buffer depth at
    /// the moment of snapshot generation:
    ///   bufferDepth = LastInputTick − snapshot.Tick
    /// That value is the number of input ticks the server has queued
    /// AHEAD of the tick it just simulated for us. Big = lots of buffer
    /// (low jitter risk, high input lag). Small = close to starving
    /// (every late input becomes a missed-input event, M9).
    ///
    /// When <see cref="InputDelayAutoAdjust"/> is set, drives
    /// <see cref="InputDelayTicks"/> toward a target band of
    /// <c>jitterTicks + InputDelaySafetyTicks</c> by accumulating a
    /// gain-weighted error term and stepping the delay ±1 when the
    /// accumulator crosses unit magnitude. Conceptually the same shape as
    /// Photon Quantum's dynamic InputOffset adjustment: the server tells
    /// the client whether its inputs are arriving on time, the client
    /// adjusts how far AHEAD it stamps inputs accordingly. Unlike B's
    /// clock-stretch, this changes the input-stamping offset only, not
    /// the simulation clock — physics steps still fire on uniform
    /// wall-clock intervals (no visual judder).
    /// </summary>
    public void OnServerInputBufferReport(int bufferDepth, int jitterTicks)
    {
        _inputDelayFeedbackSamples++;
        // Always log so the signal is usable as telemetry even when the
        // auto-adjuster is off.
        int target = jitterTicks + InputDelaySafetyTicks;
        int err = target - bufferDepth;
        bool inWarmup = _inputDelayFeedbackSamples <= InputDelayWarmupSamples;
        MonkeLogger.Debug($"[INPUT-DELAY-FEEDBACK] bufferDepth={bufferDepth} jitter={jitterTicks} target={target} err={err} inputDelay={InputDelayTicks} autoAdjust={InputDelayAutoAdjust} warmup={inWarmup}");
        if (!InputDelayAutoAdjust) return;
        // Skip the cold-start window so a transient negative bufferDepth
        // from clock-sync not yet converging doesn't ramp InputDelayTicks
        // up before the system has a chance to settle.
        if (inWarmup) return;

        // Deadband: small err within ±DeadbandTicks is treated as 0 so
        // balanced ambient noise doesn't drift the accumulator. Past the
        // deadband edge the FULL gain applies — we subtract the deadband
        // from |err| so a small excursion just past the edge produces a
        // proportionally small contribution.
        int effectiveErr;
        if (System.Math.Abs(err) <= InputDelayDeadbandTicks)
        {
            effectiveErr = 0;
        }
        else
        {
            effectiveErr = err - System.Math.Sign(err) * InputDelayDeadbandTicks;
        }

        // Asymmetric gains:
        //   effectiveErr > 0  (buffer too shallow, server about to starve)
        //                     → use the FAST gain so InputDelayTicks rises
        //                       within ~33ms of a load spike.
        //   effectiveErr < 0  (buffer too deep, wasted input lag) → use
        //                     the SLOW gain so a momentary surplus doesn't
        //                     immediately strip away the safety margin the
        //                     loop just built up.
        // This matches Source's CClockDriftMgr and Photon Quantum's
        // conservative-on-decrease shape: starvation is the failure mode
        // we're defending against, so respond to it fast and recover from
        // it slow.
        float gain = effectiveErr > 0 ? InputDelayIncreaseGain : InputDelayDecreaseGain;
        _inputDelayAdjustAccum += effectiveErr * gain;

        // At most one ±1 step per snapshot. The feedback loop has a
        // transport delay of ~2×avgLat ticks (the input we just stamped
        // higher takes avgLat to reach the server, the resulting bumped
        // LastInputTick takes avgLat to come back in a snapshot), so any
        // earlier bumps haven't had a chance to show up yet — multi-bump
        // within a single snapshot causes the loop to overshoot by an
        // amount that the slow decrease gain can't undo.
        if (_inputDelayAdjustAccum >= 1.0f && InputDelayTicks < MaxInputDelay)
        {
            InputDelayTicks++;
            _inputDelayAdjustAccum -= 1.0f;
            MonkeLogger.Debug($"[INPUT-DELAY-ADJUST] bumped UP to {InputDelayTicks}");
        }
        else if (_inputDelayAdjustAccum <= -1.0f && InputDelayTicks > MinInputDelay)
        {
            InputDelayTicks--;
            _inputDelayAdjustAccum += 1.0f;
            MonkeLogger.Debug($"[INPUT-DELAY-ADJUST] bumped DOWN to {InputDelayTicks}");
        }
        // Clamp accumulator at the saturation edges so it can't run away
        // when we're already at a delay-tick limit and the error keeps
        // signalling more pressure in the same direction. Also clamp at
        // ±2 to bound the "credit" the accumulator can carry — sustained
        // high err shouldn't be able to bank 20+ pending bumps that fire
        // continuously over the next 20 snapshots regardless of how the
        // signal looks then.
        if (InputDelayTicks >= MaxInputDelay && _inputDelayAdjustAccum > 0f)
            _inputDelayAdjustAccum = 0f;
        if (InputDelayTicks <= MinInputDelay && _inputDelayAdjustAccum < 0f)
            _inputDelayAdjustAccum = 0f;
        if (_inputDelayAdjustAccum > 2.0f)  _inputDelayAdjustAccum = 2.0f;
        if (_inputDelayAdjustAccum < -2.0f) _inputDelayAdjustAccum = -2.0f;

        // Decay-toward-default. Whenever bufferDepth is at or above target
        // (server has at least the safety margin we want — err ≤ 0), tick
        // the stability counter; once it crosses the decay threshold,
        // drop InputDelayTicks by 1 and reset the counter. Only a
        // genuinely positive err (server-side starvation — err > 0,
        // buffer below target) resets the counter; mild surplus and exact
        // hits both count as "stable enough to consider returning home".
        // Net effect: under sustained healthy conditions InputDelayTicks
        // trickles back toward the resting default; under sustained lag
        // it sits where the feedback loop placed it; under oscillating
        // load it sits somewhere in between, biased toward the default.
        if (err <= 0 && InputDelayTicks > InputDelayDefault)
        {
            _inputDelayStableSamples++;
            if (_inputDelayStableSamples >= InputDelayDecayThresholdSamples)
            {
                InputDelayTicks--;
                _inputDelayStableSamples = 0;
                MonkeLogger.Debug($"[INPUT-DELAY-DECAY] decayed toward default to {InputDelayTicks}");
            }
        }
        else
        {
            _inputDelayStableSamples = 0;
        }
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
