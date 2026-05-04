using System.Collections;
using System.Reflection;
using System.Threading.Tasks;
using GameDemo;
using GdUnit4;
using Godot;
using MonkeNet.Client;
using MonkeNet.NetworkMessages;
using MonkeNet.Serializer;
using MonkeNet.Shared;
using MonkeNet.Tests.Infrastructure;
using static GdUnit4.Assertions;

namespace MonkeNet.Tests.Integration;

/// <summary>
/// I-06: Rollback execution path in <see cref="ClientPredictionManager"/>.
///
/// Exercises the full flow: <c>ProcessServerState</c> → <c>HasMisspredicted</c> →
/// <c>RollbackAndResimulate</c> (which calls <c>HandleReconciliation</c> once per entity,
/// then iterates remaining predicted ticks calling <c>ResimulateTick</c>). A fake
/// <see cref="ClientPredictedEntity"/> records the call sequence so tests can assert
/// that the right methods were called in the right order with the right arguments.
/// </summary>
[TestSuite]
[RequireGodotRuntime]
public class RollbackTests
{
    private FakeNetworkEndpoint _serverNet;
    private FakeNetworkEndpoint _clientNet;
    private ISceneRunner _mainSceneRunner;
    private ISceneRunner _clientRunner;
    private ClientManager _client;
    private ClientPredictionManager _predictionManager;
    private FakeClientPredictedEntity _fakeEntity;
    private Node3D _entityRoot;

    [BeforeTest]
    public async Task SetUp()
    {
        MonkeNetConfig.Instance = null;
        FakeNetworkBridge.Reset();
        MessageSerializer.RegisterNetworkMessages();
        (_serverNet, _clientNet) = FakeNetworkBridge.CreatePair();

        _mainSceneRunner = ISceneRunner.Load("res://demo/MainScene.tscn", autoFree: true);
        await _mainSceneRunner.AwaitIdleFrame();

        _clientRunner = ISceneRunner.Load("res://addons/monke-net/scenes/ClientManager.tscn", autoFree: true);
        await _clientRunner.AwaitIdleFrame();
        _client = _clientRunner.Scene() as ClientManager;
        _client!.Initialize(_clientNet, "127.0.0.1", 7100);
        await _clientRunner.AwaitIdleFrame();

        _predictionManager = _client.GetNode<ClientPredictionManager>("PredictionManager");

        // OnCommandReceived guards on _networkReady — flip it so snapshots are processed
        var networkReadyField = typeof(InternalClientComponent)
            .GetField("_networkReady", BindingFlags.NonPublic | BindingFlags.Instance);
        networkReadyField!.SetValue(_predictionManager, true);

        // Strip any leftover entities from previous tests
        var spawner = EntitySpawner.Instance;
        spawner.ClearClientEntities();
        spawner.Entities.Clear();
        await _clientRunner.AwaitIdleFrame();

        // Build a fake entity tree. FakeClientPredictedEntity IS a NetworkBehaviour
        // (via ClientPredictedEntity → ClientNetworkBehaviour → NetworkBehaviour), so the
        // same instance acts as both the "entity" registered in ClientEntities AND the
        // "component" returned by GetComponent<ClientPredictedEntity>. This matches how
        // production scenes work — one NetworkBehaviour-derived node plays both roles.
        //
        //   _entityRoot (Node3D)
        //     FakeClientPredictedEntity (EntityId=99)
        _entityRoot = new Node3D();
        _fakeEntity = new FakeClientPredictedEntity { EntityId = 99 };
        _entityRoot.AddChild(_fakeEntity);
        spawner.AddChild(_entityRoot);
        spawner.ClientEntities.Add(_fakeEntity);
        await _clientRunner.AwaitIdleFrame();

        // _PhysicsProcess will have populated _predictedStates with phantom entries
        // during the AwaitIdleFrame above — clear them so the test starts clean
        var predictedStatesField = typeof(ClientPredictionManager)
            .GetField("_predictedStates", BindingFlags.NonPublic | BindingFlags.Instance);
        ((IList)predictedStatesField!.GetValue(_predictionManager)!).Clear();

        // Same for the call counters on the fake (one GetPosition per phantom registration)
        _fakeEntity.HasMisspredictedCalls.Clear();
        _fakeEntity.HandleReconciliationCalls.Clear();
        _fakeEntity.ResimulateTickCalls.Clear();
    }

    [AfterTest]
    public void TearDown()
    {
        // _entityRoot may already be freed if the surrounding ISceneRunner cleaned up
        // its parent (EntitySpawner) — guard the QueueFree call so TearDown is idempotent
        if (Godot.GodotObject.IsInstanceValid(_entityRoot))
            _entityRoot.QueueFree();
        _clientRunner?.Dispose();
        _mainSceneRunner?.Dispose();
        MonkeNetConfig.Instance = null;
    }

    // I-06 ─────────────────────────────────────────────────────────────────────
    [TestCase]
    public void Misprediction_TriggersReconciliationAndResimulationInOrder()
    {
        // Register predictions for ticks 7, 8, 9, 10. Each input encodes its tick
        // in MoveX so we can verify ResimulateTick receives them in order.
        for (int t = 7; t <= 10; t++)
        {
            var input = new CharacterInputMessage { MoveX = t / 100f };
            _predictionManager.RegisterPrediction(t, input);
        }

        _fakeEntity.HasMisspredictedReturnValue = true;

        // Server snapshot for tick 7 — should match the predicted tick and
        // trigger HasMisspredicted, which returns true and triggers rollback.
        var snap = new GameSnapshotMessage
        {
            Tick = 7,
            States = new IEntityStateData[]
            {
                new EntityStateMessage
                {
                    EntityId = 99,
                    Position = new Vector3(1f, 2f, 3f),
                    Velocity = Vector3.Zero,
                    AngularVelocity = Vector3.Zero,
                    Rotation = Vector3.Zero,
                    Yaw = 0f,
                }
            }
        };

        // SimulateIncomingPacket is synchronous, so all rollback work is
        // complete before the next line runs
        _clientNet.SimulateIncomingPacket(1, MessageSerializer.Serialize(snap));

        // HasMisspredicted called once for tick 7
        AssertThat(_fakeEntity.HasMisspredictedCalls.Count).IsEqual(1);
        AssertThat(_fakeEntity.HasMisspredictedCalls[0].Tick).IsEqual(7);

        // HandleReconciliation called once with the authoritative state for entity 99
        AssertThat(_fakeEntity.HandleReconciliationCalls.Count).IsEqual(1);
        var receivedState = (EntityStateMessage)_fakeEntity.HandleReconciliationCalls[0];
        AssertThat(receivedState.EntityId).IsEqual(99);
        AssertThat(receivedState.Position.X).IsEqualApprox(1f, 1e-5f);

        // ResimulateTick called for ticks 8, 9, 10 in order (ticks <= 7 were removed)
        AssertThat(_fakeEntity.ResimulateTickCalls.Count).IsEqual(3);
        AssertThat(((CharacterInputMessage)_fakeEntity.ResimulateTickCalls[0]).MoveX).IsEqualApprox(0.08f, 1e-5f);
        AssertThat(((CharacterInputMessage)_fakeEntity.ResimulateTickCalls[1]).MoveX).IsEqualApprox(0.09f, 1e-5f);
        AssertThat(((CharacterInputMessage)_fakeEntity.ResimulateTickCalls[2]).MoveX).IsEqualApprox(0.10f, 1e-5f);

        // _misspredictionsCount incremented exactly once
        var misspredField = typeof(ClientPredictionManager)
            .GetField("_misspredictionsCount", BindingFlags.NonPublic | BindingFlags.Instance);
        AssertThat((int)misspredField!.GetValue(_predictionManager)!).IsEqual(1);
    }

}
