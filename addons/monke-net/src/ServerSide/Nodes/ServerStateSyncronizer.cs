using Godot;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.Server;

[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class ServerStateSyncronizer : ServerNetworkBehaviour
{
    public virtual IEntityStateData PackEntityState() { return null; }
    public virtual void OnProcessTick(int tick, IPackableElement genericInput) { }
}
