using Godot;
using MonkeNet.Serializer;
using MonkeNet.Shared;

namespace MonkeNet.Client;

[GlobalClass, Icon("res://addons/monke-net/resources/circle_nodes_solid.png")]
public partial class ClientPredictedEntity : ClientNetworkBehaviour
{
    public virtual void OnProcessTick(int tick, IPackableElement input) { }
    public virtual bool HasMisspredicted(int tick, IEntityStateData receivedState, Vector3 savedState) { return false; }
    public virtual void HandleReconciliation(IEntityStateData receivedState) { }
    public virtual void ResimulateTick(IPackableElement input) { }
    public virtual Vector3 GetPosition() { return Vector3.Zero; }
}
