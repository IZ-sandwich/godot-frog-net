using Godot;
using MonkeNet.Client;

namespace MonkeNet.Shared;

/// <summary>
/// Main MonkeNet configuration singleton.
/// </summary>
[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class MonkeNetConfig : MonkeNetNode
{
    public static MonkeNetConfig Instance { get; set; } = null;

    [ExportGroup("Shared")]
    /// <summary>
    /// Controls how different entities are spawned on both the client and server.
    /// </summary>
    [Export] public EntitySpawner EntitySpawner { get; set; }

    [ExportGroup("Client")]
    /// <summary>
    /// If set, CustomClientScene will be instantiated on this node's scene upon starting the Client, useful for managers, singletons, etc.
    /// </summary>
    [Export] public PackedScene CustomClientScene { get; set; }

    /// <summary>
    /// Local input producer when running on the client.
    /// </summary>
    [Export] public InputProducerComponent InputProducer { get; set; }

    [ExportGroup("Server")]
    /// <summary>
    /// If set, CustomServerScene will be instantiated on this node's scene upon starting the Server, useful for managers, singletons, etc.
    /// </summary>
    [Export] public PackedScene CustomServerScene { get; set; }

    public override void _EnterTree()
    {
        if (Instance != null) { throw new MonkeNetException($"There are multiple {typeof(MonkeNetConfig).Name} instances!"); }
        Instance = this;
    }
}
