using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

namespace Proxima.Runtime.Network
{
    public class TcpNetworkChannel : NetworkChannel
    {
        private Socket _socket;
        private readonly SocketAsyncEventArgs _connectArgs = new();
        private readonly SocketAsyncEventArgs _receiveArgs = new();
        private readonly SocketAsyncEventArgs _sendArgs = new();
        private readonly ByteBuffer _recvBuffer = new();
        private readonly ConcurrentQueue<byte[]> _sendQueue = new();
        private readonly byte[] _rawBuffer = new byte[8192];

        private int _isSending = 0;
        private volatile bool _isClosed = true;

        // 缓存配置，避免跨线程访问
        protected readonly bool IsLittleEndian;

        //避免OOM
        protected readonly int MaxPacketSize;

        public TcpNetworkChannel(bool isLittleEndian, int maxPacketSize)
        {
            IsLittleEndian = isLittleEndian;
            MaxPacketSize = maxPacketSize;

            _connectArgs.Completed += OnIoCompleted;
            _receiveArgs.SetBuffer(_rawBuffer, 0, _rawBuffer.Length);
            _receiveArgs.Completed += OnIoCompleted;
            _sendArgs.Completed += OnIoCompleted;
        }

        public override void Connect(string address, int port = 0)
        {
            if (!_isClosed) Close();
            _isClosed = false;

            // 版本号机制解决竞态
            int curVersion = Interlocked.Increment(ref ConnectVersion);

            NetUtils.ResolveIP(address, addr =>
            {
                // 如果版本号变了（说明中间有人调用了 Close 或新的 Connect），或者已关闭，则终止
                if (curVersion != ConnectVersion || _isClosed) return;

                try
                {
                    _socket = new Socket(addr.AddressFamily, SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
                        { NoDelay = true };
                    _connectArgs.RemoteEndPoint = new IPEndPoint(addr, port);
                    if (!_socket.ConnectAsync(_connectArgs)) OnIoCompleted(null, _connectArgs);
                }
                catch
                {
                    Notify(OnFail, NetworkErrorCode.ConnectError);
                }
            }, () =>
            {
                // 只有版本号匹配才通知失败
                if (curVersion == ConnectVersion && !_isClosed) Notify(OnFail, NetworkErrorCode.DnsError);
            });
        }

        public override void Send(int seq, int rpc, int msg, byte[] data, bool isLittleEndian)
        {
            if (!IsConnected) return;

            _sendQueue.Enqueue(NetUtils.Pack(seq, rpc, msg, data, isLittleEndian));

            if (Interlocked.CompareExchange(ref _isSending, 1, 0) == 0)
            {
                ProcessSend();
            }
        }

        public override void Close()
        {
            Interlocked.Increment(ref ConnectVersion);

            if (_isClosed) return;
            _isClosed = true;
            IsConnected = false;

            try
            {
                _socket?.Close();
            }
            catch
            {
                /* Ignore */
            }

            _socket = null;

            while (_sendQueue.TryDequeue(out _))
            {
            }

            _isSending = 0;
        }

        private void OnIoCompleted(object sender, SocketAsyncEventArgs e)
        {
            // 如果已关闭，忽略所有回调
            if (_isClosed) return;

            if (e.SocketError != SocketError.Success)
            {
                Debug.LogError($"Socket Error: {e.SocketError} Code: {(int)e.SocketError}");
                var err = e.LastOperation switch
                {
                    SocketAsyncOperation.Connect => NetworkErrorCode.ConnectError,
                    SocketAsyncOperation.Send => NetworkErrorCode.SendError,
                    _ => NetworkErrorCode.SocketError
                };
                Notify(e.LastOperation == SocketAsyncOperation.Connect ? OnFail : OnClose, err);
                return;
            }

            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Connect:
                    // 连接成功再次检查状态
                    if (_isClosed)
                    {
                        try
                        {
                            _socket?.Close();
                        }
                        catch
                        {
                            // ignored
                        }

                        return;
                    }

                    IsConnected = true;
                    OnConnect?.Invoke();
                    StartReceive();
                    break;
                case SocketAsyncOperation.Receive:
                    if (e.BytesTransferred == 0)
                    {
                        //使用 PeerDisconnect 表示对方关闭连接
                        Notify(OnClose, NetworkErrorCode.PeerDisconnect);
                        return;
                    }

                    lock (_recvBuffer)
                    {
                        _recvBuffer.Write(e.Buffer, e.BytesTransferred);
                        while (true)
                        {
                            var result = _recvBuffer.Read(out var pkt, IsLittleEndian, MaxPacketSize);

                            if (result == DecodeResult.Success)
                            {
                                OnData?.Invoke(pkt);
                            }
                            else if (result == DecodeResult.Error)
                            {
                                //遇到非法包，立即断开连接
                                Notify(OnClose, NetworkErrorCode.PacketTooLarge);
                                return;
                            }
                            else
                            {
                                //NotEnoughData，跳出循环等待更多数据
                                break;
                            }
                        }
                    }

                    StartReceive();
                    break;
                case SocketAsyncOperation.Send:
                    ProcessSend();
                    break;
            }
        }

        private void StartReceive()
        {
            if (!_isClosed && _socket != null && !_socket.ReceiveAsync(_receiveArgs))
            {
                OnIoCompleted(null, _receiveArgs);
            }
        }

        private void ProcessSend()
        {
            if (_isClosed)
            {
                _isSending = 0;
                return;
            }

            if (_sendQueue.TryDequeue(out var bytes))
            {
                _sendArgs.SetBuffer(bytes, 0, bytes.Length);
                if (!_socket.SendAsync(_sendArgs)) OnIoCompleted(null, _sendArgs);
            }
            else
            {
                _isSending = 0;
                if (!_sendQueue.IsEmpty && Interlocked.CompareExchange(ref _isSending, 1, 0) == 0)
                {
                    ProcessSend();
                }
            }
        }
    }
}