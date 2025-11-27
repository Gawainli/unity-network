using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Cysharp.Threading.Tasks;
using UnityEngine;

namespace Proxima.Runtime.Network
{
    /// <summary>
    /// 代表一个独立的网络连接实例 (如 Lobby连接，Battle连接)
    /// 由 NetworkService 统一管理生命周期
    /// </summary>
    public class NetworkClient : IDisposable
    {
        public string Name { get; private set; }
        public ProtocolType Protocol { get; private set; }
        public string Ip { get; private set; }
        public int Port { get; private set; }

        public NetworkConfig Config { get; set; } // 由外部注入

        public NetworkState State { get; private set; } = NetworkState.Disconnected;
        public int RTT { get; private set; }

        // 调试日志队列
        public readonly ConcurrentQueue<NetLog> DebugLogs = new();

        private NetworkChannel _channel;
        private IMessageSerializer _serializer;
        private Action<object> _externalEventBus;

        // 状态管理
        private int _seq;
        private int _rpcIdCounter;

        // 接收端序列号记录 (用于防重放)
        private int _lastRecvSeq = 0;

        // RPC 上下文定义
        private interface IRpcContext
        {
            void ProcessResult(byte[] buffer, int length, IMessageSerializer serializer);
            void Cancel();
        }

        private class RpcContext<T> : IRpcContext
        {
            public AutoResetUniTaskCompletionSource<T> Source { get; }
            public RpcContext(AutoResetUniTaskCompletionSource<T> s) => Source = s;

            public void ProcessResult(byte[] b, int l, IMessageSerializer s)
            {
                try
                {
                    Source.TrySetResult(s.Deserialize<T>(b, 0, l));
                }
                catch (Exception e)
                {
                    Source.TrySetException(e);
                }
            }

            public void Cancel() => Source.TrySetCanceled();
        }

        private readonly Dictionary<int, IRpcContext> _rpcPending = new();
        private readonly ConcurrentQueue<NetEvent> _eventQueue = new();
        private readonly Dictionary<int, Action<byte[], int>> _msgHandlers = new();
        private readonly Dictionary<int, Action<byte[], int>> _eventBinders = new();
        private readonly Dictionary<Type, int> _msgIdCache = new();

        private float _lastSendTime, _lastRecvTime, _lastPingTime;
        private int _reconnectCount;
        private float _nextReconnectTime;
        private bool _isUserDisconnect;

        private const int PID_PING = 1001, PID_PONG = 1002;
        private readonly byte[] _pingBuffer = new byte[4];

        public NetworkClient(string name, NetworkConfig config)
        {
            Name = name;
            Config = config;
        }

        public void Dispose()
        {
            Disconnect();
            _channel = null;
        }

        public void Setup(IMessageSerializer serializer, Action<object> eventBus)
        {
            _serializer = serializer;
            _externalEventBus = eventBus;
        }

        private void CreateChannel(ProtocolType type)
        {
            _channel?.Close();
            Protocol = type;

            bool isLE = Config.IsLittleEndian;
            int maxSz = Config.MaxPacketSize;

            _channel = type switch
            {
                ProtocolType.Tcp => new TcpNetworkChannel(isLE, maxSz),
                ProtocolType.WebSocket => new WsNetworkChannel(isLE, maxSz),
                ProtocolType.Http => new WebNetworkChannel(Config),
                ProtocolType.Kcp => new KcpNetworkChannel(isLE, maxSz),
                _ => throw new NotSupportedException($"Protocol {type} is not supported"),
            };

            _channel.OnConnect = () => EnqueueEvent(NetEventType.ConnectSuccess);
            _channel.OnFail = e => EnqueueEvent(NetEventType.ConnectFail, e);
            _channel.OnClose = e => EnqueueEvent(NetEventType.Disconnect, e);
            _channel.OnData = p => EnqueueEvent(NetEventType.Data, NetworkErrorCode.None, p);
        }

        private void EnqueueEvent(NetEventType t, NetworkErrorCode e = NetworkErrorCode.None, NetPacket p = null)
            => _eventQueue.Enqueue(new NetEvent { Type = t, ErrorCode = e, Packet = p });

        //主循环
        public void Update()
        {
            _channel?.Update();

            // 分帧处理消息队列，防止一帧内处理过多消息导致卡顿
            float t0 = Time.realtimeSinceStartup;
            while (_eventQueue.TryDequeue(out var evt))
            {
                ProcessEvent(evt);
                if ((Time.realtimeSinceStartup - t0) * 1000f > Config.MaxFrameTime) break;
            }

            if (_channel != null && _channel.IsConnectionless)
            {
                return; // HTTP 不需要做下面的心跳和重连检查
            }

            if (State == NetworkState.Connected)
            {
                float now = Time.time;
                // 超时检查
                if (now - _lastRecvTime > Config.ConnectionTimeout / 1000f)
                {
                    NetworkModule.Logger.LogWarning($"[{Name}] Timeout");
                    EnqueueEvent(NetEventType.Disconnect, NetworkErrorCode.Timeout);
                    _channel.Close();
                    return;
                }

                // 心跳检查
                if (now - _lastPingTime > Config.PingInterval / 1000f)
                {
                    _lastPingTime = now;
                    SendHeartbeatOrPing();
                }
            }
            else if (Config.EnableSilentReconnect && State == NetworkState.Reconnecting &&
                     Time.time >= _nextReconnectTime)
            {
                PerformReconnect();
            }
        }

        private void SendHeartbeatOrPing()
        {
            // Ping包 SeqId 固定为 0，不占用业务序列号
            // 这样可以避免心跳包影响业务包的防重放逻辑
            if (Config.EnableRttMonitoring && _channel.SupportRTT)
            {
                NetUtils.WriteFloat(_pingBuffer, 0, Time.realtimeSinceStartup, Config.IsLittleEndian);
                _channel.Send(0, 0, PID_PING, _pingBuffer, Config.IsLittleEndian);
            }
            else
            {
                // 发送空包，SeqId=0, RpcId=0
                _channel.Send(0, 0, PID_PING, null, Config.IsLittleEndian);
            }

            _lastSendTime = Time.time;
        }

        private void PerformReconnect()
        {
            if (_reconnectCount < Config.MaxReconnectAttempts)
            {
                _reconnectCount++;

                // 计算下一次重试的间隔 (如果本次失败)
                float delay = CalculateNextDelay(_reconnectCount);
                _nextReconnectTime = Time.time + delay;

                NetworkModule.Logger.LogWarning(
                    $"[{Name}] Silent Reconnect Attempt: {_reconnectCount}/{Config.MaxReconnectAttempts}. (Next retry in {delay:F2}s if fail)");

                _channel.Connect(Ip, Port);
            }
            else
            {
                NetworkModule.Logger.LogWarning($"[{Name}] Max reconnect attempts reached. Giving up.");
                State = NetworkState.Disconnected;
            }
        }

        //指数避退
        private float CalculateNextDelay(int retryCount)
        {
            // 基础公式: Base * (Multiplier ^ Count)
            // 例如 Base=1000, Multi=2:
            // Count 0: 1000ms (第一次重连前的等待)
            // Count 1: 2000ms
            // Count 2: 4000ms
            // ...

            double delayMs = Config.ReconnectBaseInterval * Math.Pow(Config.ReconnectMultiplier, retryCount);

            // 限制最大值
            delayMs = Math.Min(delayMs, Config.ReconnectMaxInterval);

            // 添加随机抖动 (Jitter) 防止大量客户端同时重连
            // 范围: [delay * (1 - jitter), delay * (1 + jitter)]
            if (Config.ReconnectJitter > 0)
            {
                float jitter = UnityEngine.Random.Range(-Config.ReconnectJitter, Config.ReconnectJitter);
                delayMs *= (1 + jitter);
            }

            return (float)(delayMs / 1000.0); // 转为秒
        }

        private void ProcessEvent(NetEvent evt)
        {
            switch (evt.Type)
            {
                case NetEventType.ConnectSuccess:
                    if (_isUserDisconnect)
                    {
                        _channel.Close();
                        return;
                    }

                    State = NetworkState.Connected;
                    _reconnectCount = 0;
                    _lastRecvTime = Time.time;

                    //重连成功，重置接收序列号
                    _lastRecvSeq = 0;

                    NetworkModule.Logger.Log($"[{Name}] Connected to {Ip}:{Port}");
                    break;

                case NetEventType.ConnectFail:
                case NetEventType.Disconnect:
                    HandleDisconnect(evt.ErrorCode);
                    break;

                case NetEventType.Data:
                    if (State != NetworkState.Connected)
                    {
                        evt.Packet.Dispose();
                        return;
                    }

                    try
                    {
                        HandlePacket(evt.Packet);
                    }
                    finally
                    {
                        evt.Packet.Dispose();
                    }

                    break;
            }
        }

        private void HandleDisconnect(NetworkErrorCode err)
        {
            // 清理 RPC (保持不变)
            foreach (var ctx in _rpcPending.Values) ctx.Cancel();
            _rpcPending.Clear();

            // 如果是 HTTP 请求失败，不要进入 Reconnecting 状态
            // HTTP 的错误通常是单次请求失败，应该由业务层 catch 异常，而不是底层自动重连
            if (_channel != null && _channel.IsConnectionless)
            {
                State = NetworkState.Connected; // 或者保持 Connected，因为 HTTP 只要 BaseUrl 没变就算连着
                NetworkModule.Logger.LogWarning($"[{Name}] HTTP Request Failed: {err}");
                return;
            }

            // 致命错误不重连
            // 如果是因为包体过大导致的断开，不能自动重连，否则会陷入死循环
            if (err == NetworkErrorCode.PacketTooLarge)
            {
                NetworkModule.Logger.LogError($"[{Name}] Fatal Error: {err}. Stopping reconnect.");
                State = NetworkState.Disconnected;
                return;
            }

            // 处理用户主动断开
            if (_isUserDisconnect)
            {
                State = NetworkState.Disconnected;
                NetworkModule.Logger.Log($"[{Name}] User Disconnected");
            }
            else
            {
                // 处理被动断开 (Timeout, SocketError, PeerDisconnect 等)
                NetworkModule.Logger.LogWarning($"[{Name}] Disconnected: {err}");

                if (Config.EnableSilentReconnect)
                {
                    // 刚掉线 (包括 Timeout)，进入重连倒计时
                    if (State == NetworkState.Connected)
                    {
                        State = NetworkState.Reconnecting;
                        _reconnectCount = 0;

                        // 计算第一次重连时间 (使用基准间隔)
                        float delay = CalculateNextDelay(0);
                        _nextReconnectTime = Time.time + delay;

                        NetworkModule.Logger.LogWarning($"[{Name}] Connection lost. Reconnecting in {delay:F2}s...");
                    }
                    // 已经在重连中（例如 ConnectError），保持状态
                    else if (State == NetworkState.Reconnecting)
                    {
                        // 等待下次重连
                    }
                    else
                    {
                        State = NetworkState.Disconnected;
                    }
                }
                else
                {
                    State = NetworkState.Disconnected;
                }
            }
        }

        private void HandlePacket(NetPacket p)
        {
            _lastRecvTime = Time.time;
            LogDebug("Recv", p.MsgId, p.DataLength);

            // 防重放
            if (Config.EnableSeqValidation)
            {
                // SeqId = 0 保留给心跳直接放行, >0 为业务包需验证
                if (p.SeqId > 0)
                {
                    // 如果收到的包 Seq 小于等于上次收到的，视为过期或重放
                    if (p.SeqId <= _lastRecvSeq)
                    {
                        NetworkModule.Logger.LogWarning(
                            $"[{Name}] [Security] Ignored Replay Packet. Seq: {p.SeqId}, Last: {_lastRecvSeq}");
                        return; // 丢弃，不处理
                    }

                    // 更新最大序列号
                    _lastRecvSeq = p.SeqId;
                }
            }

            // RTT
            if (Config.EnableRttMonitoring && _channel.SupportRTT && p.MsgId == PID_PONG && p.DataLength >= 4)
            {
                float sendTime = NetUtils.ReadFloat(p.Data, 0, Config.IsLittleEndian);
                int rtt = (int)((Time.realtimeSinceStartup - sendTime) * 1000);
                // 平滑 RTT 计算
                RTT = RTT == 0 ? rtt : (int)(RTT * 0.7f + rtt * 0.3f);
                return;
            }

            // RPC
            if (p.RpcId > 0 && _rpcPending.TryGetValue(p.RpcId, out var ctx))
            {
                // 立即反序列化，无需拷贝 Buffer
                ctx.ProcessResult(p.Data, p.DataLength, _serializer);
                _rpcPending.Remove(p.RpcId);
                return;
            }

            // 消息分发
            if (_msgHandlers.TryGetValue(p.MsgId, out var h)) h?.Invoke(p.Data, p.DataLength);
            if (_eventBinders.TryGetValue(p.MsgId, out var b)) b?.Invoke(p.Data, p.DataLength);
        }

        private int GetMsgId(Type type)
        {
            if (_msgIdCache.TryGetValue(type, out int id)) return id;
            var attr = type.GetCustomAttribute<NetMessageAttribute>();
            if (attr != null)
            {
                _msgIdCache[type] = attr.Id;
                return attr.Id;
            }

            NetworkModule.Logger.LogError($"[{Name}] Type {type.Name} missing [NetMessage] attribute!");
            return 0;
        }

        private void LogDebug(string type, int id, int size)
        {
            if (DebugLogs.Count > 50) DebugLogs.TryDequeue(out _);
            DebugLogs.Enqueue(new NetLog
                { Time = DateTime.Now.ToString("HH:mm:ss.fff"), Type = type, Id = id, Size = size });
        }

        public void Connect(string ip, int port, ProtocolType protocol)
        {
            if (State == NetworkState.Connected) return;
            Ip = ip;
            Port = port;

            if (_channel == null || Protocol != protocol) CreateChannel(protocol);

            _isUserDisconnect = false;
            _reconnectCount = 0;
            State = NetworkState.Connecting;
            _channel.Connect(ip, port);
        }

        public void Disconnect()
        {
            _isUserDisconnect = true;
            _channel?.Close();
            State = NetworkState.Disconnected;
            while (_eventQueue.TryDequeue(out var e))
                if (e.Type == NetEventType.Data)
                    e.Packet?.Dispose();
        }

        public void SendMessage(int msgId, object msgObj)
        {
            byte[] data = msgObj == null ? null : _serializer.Serialize(msgObj);
            LogDebug("Send", msgId, data?.Length ?? 0);
            _channel.Send(++_seq, 0, msgId, data, Config.IsLittleEndian);
            _lastSendTime = Time.time;
        }

        public void Send<T>(T msg)
        {
            int id = GetMsgId(typeof(T));
            if (id > 0) SendMessage(id, msg);
        }

        public async UniTask<TResp> Call<TReq, TResp>(TReq req)
        {
            int id = GetMsgId(typeof(TReq));
            if (id == 0) throw new Exception("Invalid Msg ID");
            return await Call<TResp>(id, req);
        }

        public async UniTask<T> Call<T>(int msgId, object req)
        {
            int rpcId = ++_rpcIdCounter;

            var source = AutoResetUniTaskCompletionSource<T>.Create();
            var context = new RpcContext<T>(source);

            _rpcPending[rpcId] = context;

            byte[] data = req == null ? null : _serializer.Serialize(req);
            LogDebug("RPC", msgId, data?.Length ?? 0);
            _channel.Send(++_seq, rpcId, msgId, data, Config.IsLittleEndian);
            _lastSendTime = Time.time;

            try
            {
                return await source.Task.Timeout(TimeSpan.FromMilliseconds(Config.RpcTimeout));
            }
            finally
            {
                _rpcPending.Remove(rpcId);
            }
        }

        public void Register<T>(Action<T> cb)
        {
            int id = GetMsgId(typeof(T));
            if (id > 0) _msgHandlers[id] = (buf, len) => cb(_serializer.Deserialize<T>(buf, 0, len));
        }

        public void BindEvent<T>()
        {
            int id = GetMsgId(typeof(T));
            if (id > 0)
                _eventBinders[id] = (buf, len) => _externalEventBus?.Invoke(_serializer.Deserialize<T>(buf, 0, len));
        }
    }
}