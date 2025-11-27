using System;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Proxima.Runtime.Network
{
    public class WebNetworkChannel : NetworkChannel
    {
        public override bool IsConnectionless => true;

        private string _gatewayUrl;
        private readonly int _timeoutSeconds;

        public WebNetworkChannel(NetworkConfig config)
        {
            _timeoutSeconds = Mathf.Max(1, config.RpcTimeout / 1000);
        }

        public override void Connect(string address, int port = 0)
        {
            if (address.StartsWith("http"))
            {
                _gatewayUrl = address;
            }
            else
            {
                string protocol = port == 443 ? "https" : "http";
                _gatewayUrl = $"{protocol}://{address}:{port}/gateway"; // 默认网关路径
            }

            IsConnected = true;
            OnConnect?.Invoke();
        }

        public override void Send(int seq, int rpc, int msg, byte[] data, bool isLE)
        {
            SendAsync(seq, rpc, msg, data, isLE).Forget();
        }

        public override void Close() => IsConnected = false;

        public override void Update()
        {
        }

        private async UniTaskVoid SendAsync(int seq, int rpc, int msg, byte[] data, bool isLE)
        {
            // 1. 组装二进制包 (Length + Seq + Rpc + MsgId + Body)
            // 保持与 TCP 协议一致，方便后端统一解析
            byte[] fullPacket = NetUtils.Pack(seq, rpc, msg, data, isLE);

            using var uwr = new UnityWebRequest(_gatewayUrl, "POST");
            uwr.uploadHandler = new UploadHandlerRaw(fullPacket);
            uwr.downloadHandler = new DownloadHandlerBuffer();
            uwr.timeout = _timeoutSeconds;
            uwr.SetRequestHeader("Content-Type", "application/octet-stream");

            try
            {
                await uwr.SendWebRequest().ToUniTask();

                if (uwr.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[WebChannel] Error: {uwr.error}");
                }
                else
                {
                    // 2. 处理回包
                    HandleResponse(uwr.downloadHandler.data, isLE);
                }
            }
            catch (Exception e)
            {
                Debug.LogError($"[WebChannel] Exception: {e.Message}");
            }
        }

        private void HandleResponse(byte[] data, bool isLE)
        {
            // 校验最小长度 (Seq + Rpc + MsgId = 12字节)
            if (data == null || data.Length < 12) return;

            int offset = 0;

            // 若后端回包包含 Length 前缀，此处需 offset += 4
            // 当前假设回包格式为: [Seq(4)][Rpc(4)][MsgId(4)][Body...]

            int seq = NetUtils.ReadInt(data, offset, isLE);
            offset += 4;
            int rpc = NetUtils.ReadInt(data, offset, isLE);
            offset += 4;
            int msg = NetUtils.ReadInt(data, offset, isLE);
            offset += 4;

            int bodyLen = data.Length - offset;
            byte[] body = null;

            // Zero-GC: 从池中申请内存
            if (bodyLen > 0)
            {
                body = System.Buffers.ArrayPool<byte>.Shared.Rent(bodyLen);
                Buffer.BlockCopy(data, offset, body, 0, bodyLen);
            }

            var packet = PacketPool.Get();
            packet.SeqId = seq;
            packet.RpcId = rpc;
            packet.MsgId = msg;
            packet.Data = body;
            packet.DataLength = bodyLen;

            OnData?.Invoke(packet);
        }
    }
}