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

    private readonly List<ProducedInputForTick> _producedInputs = [];
    private int _lastReceivedTick = 0;

    public IPackableElement GenerateAndTransmitInputs(int currentTick)
    {
        IPackableElement input = MonkeNetConfig.Instance?.InputProducer?.GenerateCurrentInput();

        if (input == null)
        {
            return null;
        }

        ProducedInputForTick producedInput = new()
        {
            Tick = currentTick,
            Input = input
        };

        _producedInputs.Add(producedInput);
        SendInputsToServer(currentTick);
        return input;
    }

    // Pack and send current input + recent non-acked inputs (redundant inputs).
    // Capped at MaxRedundantInputs to stay within the unreliable MTU.
    private void SendInputsToServer(int currentTick)
    {
        int start = System.Math.Max(0, _producedInputs.Count - _maxRedundantInputs);
        int count = _producedInputs.Count - start;

        var userCmd = new PackedClientInputMessage
        {
            Tick = currentTick,
            Inputs = new IPackableElement[count]
        };

        for (int i = 0; i < count; i++)
        {
            userCmd.Inputs[i] = _producedInputs[start + i].Input;
        }

        SendCommandToServer(userCmd, INetworkManager.PacketModeEnum.Unreliable, (int)ChannelEnum.ClientInput);
    }

    // When we receive a snapshot back, we delete all inputs prior/equal to it since those were already processed.
    protected override void OnCommandReceived(IPackableMessage command)
    {
        if (command is GameSnapshotMessage snapshot && snapshot.Tick > _lastReceivedTick)
        {
            _lastReceivedTick = snapshot.Tick;
            _producedInputs.RemoveAll(input => input.Tick <= snapshot.Tick);
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
