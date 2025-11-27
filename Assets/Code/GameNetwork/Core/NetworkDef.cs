using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;

namespace Proxima.Runtime.Network
{
    public enum NetworkState
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting
    }

    public enum ProtocolType
    {
        Tcp,
        Kcp,
        WebSocket,
        Http,
    }

    public enum NetEventType
    {
        ConnectSuccess,
        ConnectFail,
        Disconnect,
        Data
    }

    public enum NetworkErrorCode
    {
        None, // 无错误
        Timeout, // 超时
        ConnectError, // 连接失败
        SendError, // 发送错误
        DnsError, // DNS解析失败
        SocketError, // 底层Socket异常
        PeerDisconnect, // 服务器关闭了连接
        PacketTooLarge, // 数据包过大
    }

    //ByteBuffer 的读取结果状态
    public enum DecodeResult
    {
        Success, // 成功读出一个包
        NotEnoughData, // 数据不足，等待更多数据
        Error // 数据异常（如包过大），需断开
    }

    public struct NetEvent
    {
        public NetEventType Type;
        public NetPacket Packet;
        public NetworkErrorCode ErrorCode;
    }

    /// <summary>
    /// 网络包对象，实现了 IDisposable 以便回收到对象池
    /// </summary>
    public class NetPacket : IDisposable
    {
        public int SeqId;
        public int RpcId;
        public int MsgId;

        /// <summary>
        /// 指向 ArrayPool 的引用，包含脏数据
        /// </summary>
        public byte[] Data;

        /// <summary>
        /// 数据的真实有效长度
        /// </summary>
        public int DataLength;

        public void Dispose()
        {
            if (Data != null)
            {
                ArrayPool<byte>.Shared.Return(Data);
                Data = null;
            }

            DataLength = 0;
            SeqId = 0;
            RpcId = 0;
            MsgId = 0;
            PacketPool.Return(this);
        }
    }
    
    public struct NetLog
    {
        public string Time;
        public string Type;
        public int Id;
        public int Size;
    }

    /// <summary>
    /// 线程安全的对象池
    /// </summary>
    public static class PacketPool
    {
        private static readonly ConcurrentStack<NetPacket> _pool = new();
        private static int _count = 0;
        private const int MaxCount = 1000;

        public static NetPacket Get()
        {
            if (_pool.TryPop(out var packet))
            {
                Interlocked.Decrement(ref _count);
                return packet;
            }

            return new NetPacket();
        }

        public static void Return(NetPacket p)
        {
            if (p == null) return;
            // 超过容量直接丢弃，让GC回收
            if (_count >= MaxCount) return;
            _pool.Push(p);

            // 使用 Interlocked 计数，避免 ConcurrentStack.Count 的性能开销
            Interlocked.Increment(ref _count);
        }
    }
}