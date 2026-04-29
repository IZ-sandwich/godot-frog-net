using Godot;
using System.IO;

namespace MonkeNet.Serializer;

public class MessageWriter(MemoryStream stream) : BinaryWriter(stream)
{
    /// <summary>
    /// Packs a List of a IPackableMessage into the stream. Will use 4 bytes for the list size and 1 extra byte for each element type.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="data"></param>
    public void Write(IPackableMessage[] data)
    {
        Write(data.Length);
        for (int i = 0; i < data.Length; i++)
        {
            Write(MessageSerializer.GetByteTypeFromMessage(data[i]));
            data[i].WriteBytes(this);
        }
    }

    /// <summary>
    /// Packs a List of a IPackableMessage into the stream. Will use 4 bytes for the list size and 1 extra byte the list type.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="data"></param>
    public void WriteSingleTypeArray(IPackableMessage[] data)
    {
        Write(data.Length);
        Write(MessageSerializer.GetByteTypeFromMessage(data[0])); //TODO: data[0] what?

        for (int i = 0; i < data.Length; i++)
        {
            data[i].WriteBytes(this);
        }
    }

    public void Write(Vector3 vector)
    {
        Write(vector.X);
        Write(vector.Y);
        Write(vector.Z);
    }

    public void Write(Quaternion quaternion)
    {
        Write(quaternion.X);
        Write(quaternion.Y);
        Write(quaternion.Z);
        Write(quaternion.W);
    }

    public void Write(Transform3D transform)
    {
        Write(transform.Origin);
        Write(transform.Basis.Column0);
        Write(transform.Basis.Column1);
        Write(transform.Basis.Column2);
    }

    public void Write(IPackableMessage message)
    {
        message.WriteBytes(this);
    }
}
