using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ServerCore.Network
{
    public sealed class RecvBuffer : IDisposable
    {
        private byte[] _buffer;
        private Int32 _readPos;
        private Int32 _writePos;
        private Int32 _capacity;

        private bool _disposed = false;

        public Memory<byte> ReadMemory => _buffer.AsMemory(_readPos, _writePos - _readPos);

        public Memory<byte> WriteMemory => _buffer.AsMemory(_writePos, _capacity - _writePos);

        public Int32 DataSize => _writePos - _readPos;

        public Int32 FreeSize => _capacity - _writePos;

        public RecvBuffer(int bufferSize = 64 * 1024)
        {
            if (bufferSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(bufferSize));

            _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

            _capacity = bufferSize;
        }

        public void Clear()
        {
            int dataSize = DataSize;

            if (dataSize == 0)
            {
                _readPos = 0;
                _writePos = 0;
                return;
            }

            ReadMemory.CopyTo(_buffer.AsMemory(0, dataSize));

            _readPos = 0;
            _writePos = dataSize;
        }

        public bool OnRead(Int32 numOfBytes)
        {
            if (numOfBytes < 0 || numOfBytes > DataSize)
                return false;

            _readPos += numOfBytes;
            return true;
        }

        public bool OnWrite(Int32 numOfBytes)
        {
            if (numOfBytes < 0 || numOfBytes > FreeSize)
                return false;

            _writePos += numOfBytes;
            return true;
        }

        public void Reset()
        {
            _readPos = 0;
            _writePos = 0;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            ArrayPool<byte>.Shared.Return(_buffer, clearArray: false);
            _buffer = Array.Empty<byte>();
            _disposed = true;
        }
    }



    public sealed class SendBuffer : IDisposable
    {
        private byte[] _buffer;
        private Int32 _writeSize;
        private Int32 _capacity;

        private bool _disposed = false;

        public Memory<byte> Buffer => _buffer.AsMemory(0, _capacity);

        public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _writeSize);

        public Int32 WriteSize => _writeSize;

        public Int32 Capacity => _capacity;


        public ArraySegment<byte> ToArraySegment()
        {
            return new ArraySegment<byte>(_buffer, 0, WriteSize);
        }

        public SendBuffer(Int32 bufferSize)
        {
            if (bufferSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(bufferSize));

            _buffer = ArrayPool<byte>.Shared.Rent(bufferSize);

            _capacity = bufferSize;
        }

        public void CopyData(ReadOnlyMemory<byte> data)
        {
            if (data.Length > _capacity)
                throw new ArgumentOutOfRangeException(nameof(data));

            data.CopyTo(_buffer.AsMemory(0, _capacity));

            _writeSize = data.Length;
        }

        public void Close(Int32 writeSize)
        {
            if (writeSize < 0 || writeSize > _capacity)
                throw new ArgumentOutOfRangeException(nameof(writeSize));

            _writeSize = writeSize;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            ArrayPool<byte>.Shared.Return(_buffer, clearArray: false);
            _buffer = Array.Empty<byte>();
            _disposed = true;
        }
    }
}
