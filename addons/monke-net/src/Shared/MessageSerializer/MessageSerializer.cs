using BidirectionalDict;
using Godot;
using MonkeNet.Shared;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace MonkeNet.Serializer;

/// <summary>
/// Defines methods to pack/unpack fields into byte array
/// </summary>
public interface IPackableMessage
{
    public void WriteBytes(MessageWriter writer);
    public void ReadBytes(MessageReader reader);
}

/// <summary>
/// Workaround interface to pack/unpack IPackableMessage into other IPackableMessage (as arrays, lists, etc)
/// </summary>
public interface IPackableElement : IPackableMessage
{
    public IPackableElement GetCopy();
}

public class MessageSerializer
{
    private static readonly Dictionary<IPackableMessage, byte> Types = [];
    private static readonly BiDictionary<byte, Type> TypeMap = [];

    /// <summary>
    /// Takes a IPackableMessage <paramref name="message"/> and packs it into a byte array as <paramref name="messageType"/>.
    /// </summary>
    /// <param name="type"></param>
    /// <param name="message"></param>
    /// <returns></returns>
    public static byte[] Serialize(IPackableMessage message)
    {
        using var stream = new MemoryStream();
        using var writer = new MessageWriter(stream);

        writer.Write(GetByteTypeFromMessage(message));
        message.WriteBytes(writer);
        return stream.ToArray();
    }

    /// <summary>
    /// Reads from a byte array <paramref name="byteArray"/> and produces an IPackableMessage.
    /// </summary>
    /// <param name="byteArray"></param>
    /// <returns></returns>
    public static IPackableMessage Deserialize(byte[] byteArray)
    {
        using var stream = new MemoryStream(byteArray);
        using var reader = new MessageReader(stream);

        byte typeByte = reader.ReadByte();

        // Get instance of the message and "fill it"
        IPackableMessage instance = GetMessageFromByteType(typeByte);
        instance.ReadBytes(reader);

        // Return the struct, essentialy creating a copy of it (in c# structs are passed by value)
        return instance;
    }

    public static IPackableMessage GetMessageFromByteType(byte type)
    {
        if (TypeMap.Contains(type)) {
            return Activator.CreateInstance(TypeMap[type]) as IPackableMessage;
		}

        throw new MonkeNetException($"Couldn't find type {type}");
    }

    public static byte GetByteTypeFromMessage(IPackableMessage message)
    {
        if (TypeMap.Contains(message.GetType())) {
            return TypeMap[message.GetType()];
		}

        throw new MonkeNetException($"Couldn't find message {message}");
    }

    // Scans the assembly and registers all Messages for the MessageSerializer
    public static void RegisterNetworkMessages()
    {
        Type[] registeredMessages = GetTypesImplementingInterface(typeof(IPackableMessage));
        byte key = 0;

        foreach (Type t in registeredMessages)
        {
            TypeMap.AddOrUpdate(key, t);
            key++;
            GD.Print($"Registered network message {t.FullName}");
        }
    }

    private static Type[] GetTypesImplementingInterface(Type type)
    {
        return Assembly.GetExecutingAssembly()
                       .GetTypes()
                       .Where(t => type.IsAssignableFrom(t) && !t.IsAbstract)
                       .ToArray();
    }
}