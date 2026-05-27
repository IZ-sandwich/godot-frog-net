using Godot;
using ImGuiNET;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;
using System.Collections.Generic;
using System.Linq;

namespace MonkeNet.Server;

[GlobalClass]
public partial class ServerInputReceiver : InternalServerComponent
{
    // After a client stops sending input, the last input is repeated for this many seconds
    // (so brief packet loss is masked) and then default-valued inputs are used so a
    // disconnected client doesn't keep moving forever.
    [Export] public float StaleInputTimeoutSec { get; set; } = 1.0f;

    /// <summary>
    /// Hard cap on how many ticks of unconsumed input may be queued per entity. Above
    /// this, the oldest queued ticks are evicted. Protects the server from a client
    /// (malicious or buggy) that floods far-future inputs and would otherwise grow the
    /// pending dictionary without bound. Default ≈ 60 ticks (1s @ 60Hz) — well past any
    /// reasonable client lookahead, but still bounded.
    /// </summary>
    [Export] public int MaximumServerReplicates { get; set; } = 60;

    private readonly Dictionary<int, Dictionary<NetworkBehaviour, IPackableElement>> _pendingInputs = [];
    private readonly Dictionary<NetworkBehaviour, IPackableElement> _lastInputStored = [];
    private readonly Dictionary<NetworkBehaviour, int> _lastReceivedTick = [];
    // Option C clock-sync feedback: highest input tick the server has
    // RECEIVED (not yet consumed) from each connected client. Updated in
    // OnCommandReceived. Stamped into outgoing snapshots so each client
    // can compare against its own _currentTick and detect engine-tick-rate
    // drift the RTT ping-pong is blind to.
    private readonly Dictionary<int, int> _lastReceivedInputTickByClient = [];
    /// <summary>Snapshot the per-client highest received input ticks for
    /// inclusion in <see cref="GameSnapshotMessage.InputFrontiers"/>.
    /// Returns a sparse list — only clients that have sent at least one
    /// input appear.</summary>
    public IReadOnlyDictionary<int, int> LastReceivedInputTickByClient => _lastReceivedInputTickByClient;
    private readonly Dictionary<NetworkBehaviour, IPackableElement> _defaultInputCache = [];

    // Side index of which ticks each entity currently has queued, for O(log n) eviction
    // of the oldest tick when MaximumServerReplicates is exceeded.
    private readonly Dictionary<NetworkBehaviour, SortedSet<int>> _pendingTicksPerEntity = [];

    /// <summary>
    /// Maximum per-entity rolling history of (tick, applied-input) pairs kept by the
    /// server so <see cref="GetRecentAppliedInputs"/> can return the last N inputs
    /// each snapshot. Capped at 30 (2× the largest N <see cref="ServerEntityManager"/>
    /// is likely to request) to bound the memory footprint per entity while leaving
    /// headroom for future tuning.
    /// </summary>
    private const int AppliedInputHistoryCap = 30;

    // Per-entity sorted history of inputs ACTUALLY APPLIED at each tick (i.e. the
    // result of GetInputForEntityTick, which may be "received", "repeat-stale" or
    // "default" depending on whether the owner sent input that tick). Maintained
    // here so PackSnapshot can hand observers a per-tick history they can replay
    // exactly — without it, the resim loop on the client side falls back to
    // "latest cached input for all replayed ticks", which is wrong whenever the
    // owner's input changed during the rollback window.
    private readonly Dictionary<NetworkBehaviour, SortedDictionary<int, IPackableElement>> _appliedInputHistory = [];

    private int _trimmedInputTotal = 0;

    private int _missedInput = 0;
    private readonly Dictionary<int, int> _missedInputTotal = [];
    private readonly Dictionary<int, Queue<bool>> _missedInputWindow = [];
    private const int MissedInputWindowSize = 64;

    /// <summary>Cumulative count of (tick × entity) events where the server
    /// ticked an entity owned by a real client and didn't find a fresh
    /// client-stamped input in <c>_pendingInputs</c> — server fell back to
    /// repeat-stale or default. Aggregated across all client-owned
    /// entities. Quantitative-suite M13 metric: the EVENT version of
    /// "server-side input buffer ran empty". Distinct from M9 (client-side
    /// prediction replay couldn't find an input in snapshot history) — M9
    /// is a replay-time event at the client; M13 is an apply-time event
    /// at the server.
    ///
    /// Excludes server-authoritative entities (authority=0) because those
    /// passive props never have a client input — they'd inflate the count
    /// every tick if included, masking the real signal.</summary>
    public int TotalMissedInputs
    {
        get
        {
            int sum = 0;
            foreach (var kv in _missedInputTotal)
            {
                if (kv.Key > 0) sum += kv.Value;
            }
            return sum;
        }
    }

    public IPackableElement GetInputForEntityTick(NetworkBehaviour serverEntity, int tick)
    {
        bool received;
        IPackableElement result;
        string source;

        if (_pendingInputs.TryGetValue(tick, out var tickInputs)
            && tickInputs.TryGetValue(serverEntity, out result))
        {
            _lastInputStored[serverEntity] = result;
            _lastReceivedTick[serverEntity] = tick;
            if (!_defaultInputCache.ContainsKey(serverEntity))
                _defaultInputCache[serverEntity] = (IPackableElement)System.Activator.CreateInstance(result.GetType());
            received = true;
            source = "received";
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
                source = $"repeat(stale={staleTicks})";
            }
            else
            {
                _defaultInputCache.TryGetValue(serverEntity, out result);
                source = "default";
            }
            // Critical: keep _lastInputStored in sync with the input ACTUALLY
            // APPLIED this tick — not just the last received one. PackSnapshot
            // calls GetLastInputFor() to fill GameSnapshotMessage.Inputs[],
            // and observers cache that and re-apply it for their local
            // prediction of the entity. Without this assignment, when an
            // owner stops sending input and the server falls back to "default"
            // after the stale timeout, the server applies (move=0) locally
            // but the snapshot keeps reporting the LAST RECEIVED input
            // (e.g. move=(1,0)) forever — so every observer's prediction
            // accelerates the body each resim tick while the server keeps
            // it stopped, producing a constant stream of mispredictions
            // whenever the snapshot arrives (observed as "constant mispredicts
            // when a player changes direction" — i.e. when they stop pressing
            // keys after a directional change).
            _lastInputStored[serverEntity] = result;
        }
        // Record the input we actually applied at this tick into the per-entity
        // history so PackSnapshot can later replay the last N (tick, input) pairs
        // to observers. Skip null entries (server-owned passive props with no
        // input ever) so the snapshot doesn't carry meaningless rows.
        if (result != null)
        {
            if (!_appliedInputHistory.TryGetValue(serverEntity, out var history))
            {
                history = new SortedDictionary<int, IPackableElement>();
                _appliedInputHistory[serverEntity] = history;
            }
            history[tick] = result;
            while (history.Count > AppliedInputHistoryCap)
            {
                history.Remove(history.Keys.First());
            }
        }
        MonkeLogger.Debug($"[NET-INPUT-CONSUME] tick={tick} eid={serverEntity.EntityId} authority={serverEntity.Authority} source={source} input={result?.ToString() ?? "null"}");

        int authority = serverEntity.Authority;
        _missedInputTotal.TryAdd(authority, 0);
        _missedInputWindow.TryAdd(authority, new Queue<bool>());
        // Only count misses (both per-authority total and the rolling window
        // used by ServerConnectionMonitor) for entities that have received
        // at least one input from their owner. Before the first received,
        // the entity exists on the server but the client hasn't started
        // sending — that's not a real "missed input" event, it's just
        // "input pipeline hasn't started yet". Without this gate, the
        // multi-second pre-drive warm-up window at scenario start would
        // dominate the M13 metric (S7 C0 traces show ~270 default events
        // from tick 220-489 before the first input arrives at tick 490,
        // entirely accounting for the ~30% M13 floor) and would also
        // falsely trip the connection monitor's "client is lagging"
        // detection. _lastReceivedTick is the right gate: it's only set
        // in the received branch above, so its presence means "we have
        // seen at least one fresh input from this entity".
        bool hasEverReceived = _lastReceivedTick.ContainsKey(serverEntity);
        if (hasEverReceived)
        {
            if (!received) _missedInputTotal[authority]++;
            var window = _missedInputWindow[authority];
            window.Enqueue(received);
            if (window.Count > MissedInputWindowSize) window.Dequeue();
        }

        return result;
    }

    /// <summary>
    /// Returns the most recent input the server processed for <paramref name="serverEntity"/>,
    /// or null if no input has ever been consumed (e.g. server-authoritative passive prop).
    /// Used by <see cref="ServerEntityManager"/> when packing the snapshot so observers
    /// receive each entity's owner-supplied input and can apply it to their local
    /// prediction — without it, observers can only coast at last-known velocity and
    /// reconcile against every snapshot.
    /// </summary>
    public IPackableElement GetLastInputFor(NetworkBehaviour serverEntity)
    {
        return _lastInputStored.TryGetValue(serverEntity, out var input) ? input : null;
    }

    /// <summary>
    /// Returns the most recent <paramref name="maxPastCount"/> applied (tick, input)
    /// pairs PLUS any pending future (tick, input) pairs the server has received
    /// from the owner but not yet applied (typical with an owner-side input
    /// delay: the input is sent ahead of its apply tick so it propagates to the
    /// server with time to spare). Sorted oldest-first so observers can ingest
    /// in tick order.
    ///
    /// Used by <see cref="ServerEntityManager.PackSnapshot"/>:
    /// - Past entries let an observer's rollback resim apply the correct
    ///   per-tick input at each replayed tick.
    /// - Future entries let an observer learn an upcoming input BEFORE its own
    ///   clock reaches the apply tick — so its forward prediction at tick
    ///   T uses the same input the server will apply at T, eliminating the
    ///   "one-tick-of-input-lag" reconcile that fires on every direction change
    ///   when an owner-side delay is paired with snapshot-relayed inputs.
    /// </summary>
    public List<(int Tick, IPackableElement Input)> GetRecentAppliedInputs(
        NetworkBehaviour serverEntity, int maxPastCount, int currentServerTick)
    {
        var result = new List<(int, IPackableElement)>();

        // Past applied history — most recent N ticks.
        if (maxPastCount > 0
            && _appliedInputHistory.TryGetValue(serverEntity, out var history)
            && history.Count > 0)
        {
            int take = System.Math.Min(maxPastCount, history.Count);
            foreach (var kv in history.Skip(history.Count - take))
                result.Add((kv.Key, kv.Value));
        }

        // Future pending inputs the server has received but not yet applied.
        // _pendingInputs is keyed by tick → (entity → input); iterate ticks
        // > currentServerTick that have an entry for this entity. Bounded by
        // MaximumServerReplicates so the loop is short.
        if (_pendingTicksPerEntity.TryGetValue(serverEntity, out var futureTicks))
        {
            foreach (int tick in futureTicks)
            {
                if (tick <= currentServerTick) continue;
                if (_pendingInputs.TryGetValue(tick, out var perTick)
                    && perTick.TryGetValue(serverEntity, out var input))
                {
                    result.Add((tick, input));
                }
            }
        }

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

        // Option C: update the per-client input-frontier tracker as soon as
        // we observe an input message arrive. Use the message's latest tick
        // (Tick field — see PackedClientInputMessage XML doc: "Tick stamp
        // for the latest generated input"). Monotonic — newer wins.
        if (!_lastReceivedInputTickByClient.TryGetValue(clientId, out int prev) || inputCommand.Tick > prev)
            _lastReceivedInputTickByClient[clientId] = inputCommand.Tick;

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
        MonkeLogger.Debug($"[NET-INPUT-RX] eid={serverEntity.EntityId} authority={serverEntity.Authority} latestTick={inputCommand.Tick} batch={inputCommand.Inputs.Length}");
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

            if (value.TryAdd(serverEntity, input))
            {
                MonkeLogger.Debug($"[NET-INPUT-RX]   tick={tick} input={input}");
                if (!_pendingTicksPerEntity.TryGetValue(serverEntity, out var ticks))
                {
                    ticks = new SortedSet<int>();
                    _pendingTicksPerEntity[serverEntity] = ticks;
                }
                ticks.Add(tick);
            }
        }

        EnforceCap(serverEntity);
    }

    private void EnforceCap(NetworkBehaviour serverEntity)
    {
        if (!_pendingTicksPerEntity.TryGetValue(serverEntity, out var ticks)) return;
        while (ticks.Count > MaximumServerReplicates)
        {
            int oldest = ticks.Min;
            ticks.Remove(oldest);
            if (_pendingInputs.TryGetValue(oldest, out var perTick))
            {
                perTick.Remove(serverEntity);
                if (perTick.Count == 0) _pendingInputs.Remove(oldest);
            }
            _trimmedInputTotal++;
        }
    }

    public void DropOutdatedInputs(int currentTick)
    {
        // Materialise the keys list first — modifying _pendingInputs while enumerating
        // its Keys collection throws InvalidOperationException.
        var stale = new List<int>();
        foreach (int key in _pendingInputs.Keys)
        {
            if (key <= currentTick) stale.Add(key);
        }
        foreach (int key in stale)
        {
            _pendingInputs.Remove(key);
        }

        // Trim the per-entity tick index too so EnforceCap doesn't think long-consumed
        // ticks are still in flight.
        foreach (var ticks in _pendingTicksPerEntity.Values)
        {
            while (ticks.Count > 0 && ticks.Min <= currentTick) ticks.Remove(ticks.Min);
        }
    }

    public int GetTrimmedInputTotal() => _trimmedInputTotal;

    public void DisplayDebugInformation()
    {
        if (ImGui.CollapsingHeader("Input Receiver"))
        {
            ImGui.Text($"Input Queue {_pendingInputs.Count}");
            ImGui.Text($"Missed Inputs {_missedInput}");
            ImGui.Text($"Trimmed (over cap) {_trimmedInputTotal}");
        }
    }
}
