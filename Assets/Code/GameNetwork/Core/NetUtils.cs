using System;
using System.Net;
using System.Net.Sockets;
using Cysharp.Threading.Tasks;

namespace Proxima.Runtime.Network
{
    public static class NetUtils
    {
        public const int HeaderLen = 16;

        public static void WriteInt(byte[] buf, int offset, int value, bool isLE)
        {
            if (isLE)
            {
                buf[offset] = (byte)value;
                buf[offset + 1] = (byte)(value >> 8);
                buf[offset + 2] = (byte)(value >> 16);
                buf[offset + 3] = (byte)(value >> 24);
            }
            else
            {
                buf[offset] = (byte)(value >> 24);
                buf[offset + 1] = (byte)(value >> 16);
                buf[offset + 2] = (byte)(value >> 8);
                buf[offset + 3] = (byte)value;
            }
        }

        public static int ReadInt(byte[] buf, int offset, bool isLE)
        {
            if (isLE)
            {
                return buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16) | (buf[offset + 3] << 24);
            }
            else
            {
                return (buf[offset] << 24) | (buf[offset + 1] << 16) | (buf[offset + 2] << 8) | buf[offset + 3];
            }
        }

        public static void WriteFloat(byte[] buf, int offset, float value, bool isLE)
        {
            // Zero-GC 转换
            int intValue = BitConverter.SingleToInt32Bits(value);
            WriteInt(buf, offset, intValue, isLE);
        }

        public static float ReadFloat(byte[] buf, int offset, bool isLE)
        {
            int intValue = ReadInt(buf, offset, isLE);
            // Zero-GC 转换
            return BitConverter.Int32BitsToSingle(intValue);
        }

        public static byte[] Pack(int seq, int rpc, int msgId, byte[] body, bool isLE)
        {
            int bodyLen = body?.Length ?? 0;
            int totalLen = 12 + bodyLen; // 12 = Seq(4) + Rpc(4) + MsgId(4)
            // 此处有GC，非超高频可忽略
            byte[] buf = new byte[4 + totalLen]; // 4 = Length

            WriteInt(buf, 0, totalLen, isLE);
            WriteInt(buf, 4, seq, isLE);
            WriteInt(buf, 8, rpc, isLE);
            WriteInt(buf, 12, msgId, isLE);

            if (bodyLen > 0)
            {
                Buffer.BlockCopy(body, 0, buf, 16, bodyLen);
            }

            return buf;
        }

        // 智能解析 IP 地址 (支持 IP 字面量和域名，优先 IPv6)
        public static async UniTaskVoid ResolveIP(string host, Action<IPAddress> onResult, Action onFail)
        {
            try
            {
                // 如果是host是IP则直接返回
                if (IPAddress.TryParse(host, out IPAddress ipAddress))
                {
                    onResult?.Invoke(ipAddress);
                    return;
                }

                // 如果host是Dns 则解析
                // 切换到线程池防止卡顿
                await UniTask.SwitchToThreadPool();

                // GetHostEntryAsync如果传入的是 IP 字符串直接返回，如果是域名则解析
                var entry = await Dns.GetHostEntryAsync(host);

                IPAddress target = null;

                foreach (var ip in entry.AddressList)
                {
                    if (ip.AddressFamily == AddressFamily.InterNetworkV6)
                    {
                        target = ip;
                        break;
                    }
                }

                //如果没有 IPv6，查找 IPv4 地址
                if (target == null)
                {
                    foreach (var ip in entry.AddressList)
                    {
                        if (ip.AddressFamily == AddressFamily.InterNetwork)
                        {
                            target = ip;
                            break;
                        }
                    }
                }

                if (target != null)
                {
                    onResult?.Invoke(target);
                }
                else
                {
                    onFail?.Invoke();
                }
            }
            catch (Exception)
            {
                onFail?.Invoke();
            }
        }
    }
}