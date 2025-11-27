using System;

namespace Proxima.Runtime.Network
{
    public class NetworkConfig
    {
        //是否使用小端序 (Little Endian)
        public bool IsLittleEndian = true;

        //每帧处理网络消息的最大耗时(毫秒)，防止卡顿
        public float MaxFrameTime = 5.0f;

        //是否开启静默重连 (需服务器支持 Session Resume)
        public bool EnableSilentReconnect = false;

        //是否开启 RTT 监控
        public bool EnableRttMonitoring = false;

        //心跳发送间隔 (毫秒)
        public int PingInterval = 2000;

        //RPC 请求超时时间 (毫秒)
        public int RpcTimeout = 5000;

        //连接超时时间 (毫秒)
        public int ConnectionTimeout = 15000;

        //最大网络包大小 (字节) 1024*1024 = 1M
        public int MaxPacketSize = 1024 * 1024;

        //是否开启 SeqId 验证
        public bool EnableSeqValidation = false;

        //最大重连次数
        public int MaxReconnectAttempts = 10;

        //重连间隔 (毫秒)
        public float ReconnectInterval = 3000f;

        //重连基础间隔 (毫秒)
        public int ReconnectBaseInterval = 1000; // 1秒

        //重连最大间隔 (毫秒)
        public int ReconnectMaxInterval = 30000;

        //重连指数乘数
        public float ReconnectMultiplier = 2.0f;

        //重连抖动
        public float ReconnectJitter = 0.2f;
    }
}