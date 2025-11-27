using System;
using UnityEngine;

namespace Proxima.Runtime.Network
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public class NetMessageAttribute : Attribute
    {
        public int Id { get; }
        public string ChannelName { get; }

        public NetMessageAttribute(int id, string channelName)
        {
            Id = id;
            ChannelName = channelName;
        }
    }

    public interface IMessageSerializer
    {
        byte[] Serialize(object msg);

        // 直接传递 Buffer，避免 MemoryStream 分配
        T Deserialize<T>(byte[] buffer, int offset, int count);
    }

    public class DefaultSerializer : IMessageSerializer
    {
        public byte[] Serialize(object msg) => msg as byte[];

        public T Deserialize<T>(byte[] buffer, int offset, int count)
        {
            // 默认实现：因为 T 是 byte[]，我们需要把有效数据 Copy 出来
            // 否则业务层会读到 ArrayPool 里的脏数据
            byte[] result = new byte[count];
            if (count > 0)
            {
                Buffer.BlockCopy(buffer, offset, result, 0, count);
            }

            return (T)(object)result;
        }
    }

    public class JsonStringSerializer : IMessageSerializer
    {
        public byte[] Serialize(object msg)
        {
            string json = JsonUtility.ToJson(msg);
            return System.Text.Encoding.UTF8.GetBytes(json);
        }

        public T Deserialize<T>(byte[] buffer, int offset, int count)
        {
            //这里会产生 string 的 GC，生产环境用 Protobuf
            string json = System.Text.Encoding.UTF8.GetString(buffer, offset, count);
            return JsonUtility.FromJson<T>(json);
        }
    }

    // 如果使用 Protobuf-net，实现可能如下：
    /*
    public class ProtobufSerializer : IMessageSerializer
    {
        public byte[] Serialize(object msg)
        {
            using (var ms = new System.IO.MemoryStream())
            {
                ProtoBuf.Serializer.Serialize(ms, msg);
                return ms.ToArray();
            }
        }

        public T Deserialize<T>(byte[] buffer, int offset, int count)
        {
            // Protobuf-net 需要 Stream，这里我们手动 wrap 一个，
            // 虽然这里还是 new 了 MemoryStream，但这是库的限制。
            // 如果换成 MessagePack，可以直接传 buffer 进去实现 Zero-GC。
            using (var ms = new System.IO.MemoryStream(buffer, offset, count))
            {
                return ProtoBuf.Serializer.Deserialize<T>(ms);
            }
        }
    }
    */
}