using Godot;
using ImGuiNET;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;
using System.Collections.Generic;

namespace MonkeNet.Server;

[GlobalClass]
public partial class ServerInputReceiver : InternalServerComponent
{
    // After a client stops sending input, the last input is repeated for this many seconds
    // (so brief packet loss is masked) and then default-valued inputs are used so a
    // disconnected client doesn't keep moving forever.
    [Export] public float StaleInputTimeoutSec { get; set; } = 1.0f;

    private readonly Dictionary<int, Dictionary<NetworkBehaviour, IPackableElement>> _pendingInputs = [];
    private readonly Dictionary<NetworkBehaviour, IPackableElement> _lastInputStored = [];
    private readonly Dictionary<NetworkBehaviour, int> _lastReceivedTick = [];
    private readonly Dictionary<NetworkBehaviour, IPackableElement> _defaultInputCache = [];

    private int _missedInput = 0;
    private readonly Dictionary<int, int> _missedInputTotal = [];
    private readonly Dictionary<int, Queue<bool>> _missedInputWindow = [];
    private const int MissedInputWindowSize = 64;

    public IPackableElement GetInputForEntityTick(NetworkBehaviour serverEntity, int tick)
    {
        bool received;
        IPackableElement result;

        if (_pendingInputs.TryGetValue(tick, out var tickInputs)
            && tickInputs.TryGetValue(serverEntity, out result))
        {
            _lastInputStored[serverEntity] = result;
            _lastReceivedTick[serverEntity] = tick;
            if (!_defaultInputCache.ContainsKey(serverEntity))
                _defaultInputCache[serverEntity] = (IPackableElement)System.Activator.CreateInstance(result.GetType());
            received = true;
        }
        else
        {
            _missedInput++;
            received = false;

            int maxStaleTicks = (int)(StaleInputTimeoutSec * Engine.PhysicsTicksPerSecond);
            int staleTicks = _lastReceivedTick.TryGetValue(serverEntity, out int last)
                ? tick - last
                : int.MaxValue;

            if (staleTicks <= maxStaleTicks
                && _lastInputStored.TryGetValue(serverEntity, out IPackableElement repeat))
            {
                result = repeat;
            }
            else
            {
                _defaultInputCache.TryGetValue(serverEntity, out result);
            }
        }

        int authority = serverEntity.Authority;
        _missedInputTotal.TryAdd(authority, 0);
        _missedInputWindow.TryAdd(authority, new Queue<bool>());
        if (!received) _missedInputTotal[authority]++;
        var window = _missedInputWindow[authority];
        window.Enqueue(received);
        if (window.Count > MissedInputWindowSize) window.Dequeue();

        return result;
    }

    public int GetMissedInputTotal(int clientId) =>
        _missedInputTotal.TryGetValue(clientId, out int v) ? v : 0;

    public float GetMissedInputRate(int clientId)
    {
        if (!_missedInputWindow.TryGetValue(clientId, out var w) || w.Count == 0) return 0f;
        int missed = 0;
        foreach (bool received in w) if (!received) missed++;
        return missed / (float)w.Count;
    }

    protected override void OnCommandReceived(int clientId, IPackableMessage command)
    {
        if (command is not PackedClientInputMessage inputCommand)
            return;

        // Find the ServerEntity target for this input command
        foreach (var entity in MonkeNetManager.Instance.EntitySpawner.Entities)
        {
            if (entity is NetworkBehaviour serverEntity && clientId == serverEntity.Authority)
            {
                RegisterCommand(serverEntity, inputCommand);
            }
        }
    }

    private void RegisterCommand(NetworkBehaviour serverEntity, PackedClientInputMessage inputCommand)
    {
        int offset = inputCommand.Inputs.Length - 1;
        foreach (IPackableElement input in inputCommand.Inputs)
        {
            int tick = inputCommand.Tick - (offset--);

            // Check if we have an entry for this tick
            if (!_pendingInputs.TryGetValue(tick, out Dictionary<NetworkBehaviour, IPackableElement> value))
            {
                value = ([]);
                _pendingInputs.Add(tick, value);
            }

            value.TryAdd(serverEntity, input);
        }
    }

    public void DropOutdatedInputs(int currentTick)
    {
        foreach (int key in _pendingInputs.Keys)
        {
            if (key <= currentTick)
            {
                _pendingInputs.Remove(key);
            }
        }
    }

    public void DisplayDebugInformation()
    {
        if (ImGui.CollapsingHeader("Input Receiver"))
        {
            ImGui.Text($"Input Queue {_pendingInputs.Count}");
            ImGui.Text($"Missed Inputs {_missedInput}");
        }
    }
}
