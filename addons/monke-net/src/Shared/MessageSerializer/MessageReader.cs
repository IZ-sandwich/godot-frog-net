using Godot;
using System.IO;

namespace MonkeNet.Serializer;

public class MessageReader(MemoryStream stream) : BinaryReader(stream)
{
    public T[] ReadArray<T>() where T : IPackableElement
    {
        int collectionSize = ReadInt32();
        var res = new T[collectionSize];

        for (int i = 0; i < collectionSize; i++)
        {
            byte elementType = ReadByte();
            IPackableElement instance = MessageSerializer.GetMessageFromByteType(elementType) as IPackableElement;

            instance.ReadBytes(this); // Read bytes and update internal state
            res[i] = (T)instance.GetCopy();
        }

        return res;
    }

    public T[] ReadSingleTypeArray<T>() where T : IPackableElement
    {
        int collectionSize = ReadInt32();
        var res = new T[collectionSize];

        byte elementType = ReadByte();
        IPackableElement instance = MessageSerializer.GetMessageFromByteType(elementType) as IPackableElement;

        for (int i = 0; i < collectionSize; i++)
        {
            instance.ReadBytes(this); // Read bytes and update internal state
            res[i] = (T)instance.GetCopy();
        }

        return res;
    }

    public Vector3 ReadVector3()
    {
        return new(ReadSingle(), ReadSingle(), ReadSingle());
    }

    public Quaternion ReadQuaternion()
    {
        return new(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
    }

    public Transform3D ReadTransform()
    {
        return new()
        {
            Origin = ReadVector3(),
            Basis = new Basis(ReadVector3(), ReadVector3(), ReadVector3())
        };
    }

    public T ReadPackable<T>() where T : IPackableMessage, new()
    {
        T res = new();
        res.ReadBytes(this);
        return res;
    }
}
