using Godot;
using MonkeNet.Client;
using MonkeNet.Serializer;

namespace MonkeNet.Tests.Infrastructure;

/// <summary>
/// Preset input producer for tests. Returns the configured <see cref="NextInput"/> value
/// on every <see cref="GenerateCurrentInput"/> call.
/// </summary>
[GlobalClass]
public partial class FakeInputProducer : InputProducerComponent
{
    public IPackableElement NextInput { get; set; }

    public override IPackableElement GenerateCurrentInput() => NextInput;
}
