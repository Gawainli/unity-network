using System;

namespace Proxima.Runtime.Network
{
    public class NetworkChannel
    {
        public Action OnConnect;
        public Action<NetworkErrorCode> OnFail;
        public Action<NetworkErrorCode> OnClose;
        public Action<NetPacket> OnData;

        //连接版本号, 解决 Connect->Close->ConnectCallback 的竞态问题
        protected int ConnectVersion = 0;

        public bool IsConnected { get; protected set; }

        public virtual bool IsConnectionless => false;

        public virtual bool SupportRTT => !IsConnectionless;

        public virtual void Connect(string address, int port = 0)
        {
        }

        public virtual void Send(int seq, int rpc, int msg, byte[] data, bool isLittleEndian)
        {
        }

        public virtual void Close()
        {
        }

        public virtual void Update()
        {
        }

        protected void Notify(Action<NetworkErrorCode> action, NetworkErrorCode err)
        {
            Close();
            action?.Invoke(err);
        }
    }
}