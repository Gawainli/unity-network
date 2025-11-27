using System;
using System.Buffers;

namespace Proxima.Runtime.Network
{
    public class ByteBuffer
    {
        private byte[] _buf;
        private int _wIdx, _rIdx, _cap;
        public int Readable => _wIdx - _rIdx;

        public ByteBuffer(int capacity = 65536)
        {
            _cap = capacity;
            _buf = new byte[capacity];
        }

        public void Write(byte[] data, int len)
        {
            if (len <= 0) return;
            EnsureCapacity(len);
            Buffer.BlockCopy(data, 0, _buf, _wIdx, len);
            _wIdx += len;
        }

        public DecodeResult Read(out NetPacket packet, bool isLE, int maxSize)
        {
            packet = null;
            if (Readable < 4) return DecodeResult.NotEnoughData;

            int len = NetUtils.ReadInt(_buf, _rIdx, isLE);

            // OOM 防御检查
            if (len < 12 || len > maxSize)
            {
                return DecodeResult.Error;
            }

            if (Readable < 4 + len) return DecodeResult.NotEnoughData;

            _rIdx += 4; // Skip Length
            int seq = NetUtils.ReadInt(_buf, _rIdx, isLE);
            _rIdx += 4;
            int rpc = NetUtils.ReadInt(_buf, _rIdx, isLE);
            _rIdx += 4;
            int msg = NetUtils.ReadInt(_buf, _rIdx, isLE);
            _rIdx += 4;

            int bodyLen = len - 12;
            byte[] body = null;
            if (bodyLen > 0)
            {
                body = ArrayPool<byte>.Shared.Rent(bodyLen);
                Buffer.BlockCopy(_buf, _rIdx, body, 0, bodyLen);
                _rIdx += bodyLen;
            }

            packet = PacketPool.Get();
            packet.SeqId = seq;
            packet.RpcId = rpc;
            packet.MsgId = msg;
            packet.Data = body;
            packet.DataLength = bodyLen;

            CheckAndMoveBytes();
            return DecodeResult.Success;
        }

        private void EnsureCapacity(int len)
        {
            if (_wIdx + len <= _cap) return;
            int newCap = _cap * 2;
            while (_wIdx + len > newCap) newCap *= 2;
            byte[] newBuf = new byte[newCap];
            Buffer.BlockCopy(_buf, _rIdx, newBuf, 0, Readable);
            _wIdx = Readable;
            _rIdx = 0;
            _buf = newBuf;
            _cap = newCap;
        }

        private void CheckAndMoveBytes()
        {
            if (Readable == 0) _rIdx = _wIdx = 0;
            else if (_rIdx > _cap / 2)
            {
                int count = Readable;
                Buffer.BlockCopy(_buf, _rIdx, _buf, 0, count);
                _rIdx = 0;
                _wIdx = count;
            }
        }
    }
}