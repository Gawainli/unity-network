namespace  Proxima.Runtime.Network
{
    public class KcpNetworkChannel : NetworkChannel
    {
        // 缓存配置，避免跨线程访问
        protected readonly bool IsLittleEndian;

        //避免OOM
        protected readonly int MaxPacketSize;

        public KcpNetworkChannel(bool isLittleEndian, int maxPacketSize)
        {
            IsLittleEndian = isLittleEndian;
            MaxPacketSize = maxPacketSize;
        }

        public override void Connect(string address, int port = 0)
        {
            throw new System.NotImplementedException();
        }

        public override void Send(int seq, int rpc, int msg, byte[] data, bool isLittleEndian)
        {
            throw new System.NotImplementedException();
        }

        public override void Close()
        {
            throw new System.NotImplementedException();
        }

        public override void Update()
        {
            throw new System.NotImplementedException();
        }
    }
}