using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ServerCore.Packet
{
    public static class PacketSize
    {
        public const int Bool = 1;
        public const int Byte = 1;
        public const int Int16 = 2;
        public const int UInt16 = 2;
        public const int Int32 = 4;
        public const int UInt32 = 4;
        public const int Int64 = 8;
        public const int UInt64 = 8;
        public const int Float = 4;
        public const int Double = 8;

        public static int String(string value)
        {
            return PacketWriter.GetStringSize(value);
        }
    }

    public ref struct PacketWriter
    {
        private Span<byte> _buffer;
        private Int32 _offset;

        public PacketWriter(Span<byte> buffer)
        {
            _buffer = buffer;
            _offset = 0;
        }

        public Int32 WrittenSize => _offset;

        public void WriteBool(bool value)
        {
            _buffer[_offset++] = value ? (byte)1 : (byte)0;
        }

        public void WriteByte(byte value)
        {
            _buffer[_offset++] = (byte)value;
        }

        public void WriteInt16(Int16 value)
        {
            BinaryPrimitives.WriteInt16LittleEndian(_buffer.Slice(_offset, 2), value);
            _offset += 2;
        }

        public void WriteUInt16(UInt16 value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.Slice(_offset, 2), value);
            _offset += 2;
        }

        public void WriteInt32(Int32 value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.Slice(_offset, 4), value);
            _offset += 4;
        }

        public void WriteUInt32(UInt32 value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.Slice(_offset, 4), value);
            _offset += 4;
        }

        public void WriteInt64(Int64 value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.Slice(_offset, 8), value);
            _offset += 8;
        }

        public void WriteUInt64(UInt64 value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_buffer.Slice(_offset, 8), value);
            _offset += 8;
        }

        public void WriteFloat(float value)
        {
            int raw = BitConverter.SingleToInt32Bits(value);
            WriteInt32(raw);
        }

        public void WriteDouble(double value)
        {
            long raw = BitConverter.DoubleToInt64Bits(value);
            WriteInt64(raw);
        }

        public void WriteString(string value)
        {
            int byteCount = Encoding.UTF8.GetByteCount(value);

            if (byteCount > ushort.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(value));

            WriteUInt16((ushort)byteCount);

            Encoding.UTF8.GetBytes(value, _buffer.Slice(_offset, byteCount));
            _offset += byteCount;
        }

        public static Int32 GetStringSize(string value)
        {
            if(value == null)
                value = string.Empty;

            Int32 byteCount = Encoding.UTF8.GetByteCount(value);

            if(byteCount > UInt16.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(value), "string is too long");

            return sizeof(UInt16) + byteCount;
        }
    }

    public ref struct PacketReader
    {
        private ReadOnlySpan<byte> _buffer;
        private Int32 _offset;

        public PacketReader(ReadOnlySpan<byte> buffer)
        {
            _buffer = buffer;
            _offset = 0;
        }

        public bool TryReadBool(out bool value)
        {
            if (!TryReadByte(out Byte rawValue))
            {
                value = default;
                return false;
            }

            value = rawValue != 0;
            return true;
        }

        public bool TryReadByte(out Byte value)
        {
            if (!CanRead(PacketSize.Byte))
            {
                value = default;
                return false;
            }

            value = _buffer[_offset++];
            return true;
        }

        public bool TryReadInt16(out Int16 value)
        {
            if (!CanRead(PacketSize.Int16))
            {
                value = default;
                return false;
            }

            value = BinaryPrimitives.ReadInt16LittleEndian(_buffer.Slice(_offset, PacketSize.Int16));
            _offset += PacketSize.Int16;
            return true;
        }

        public bool TryReadUInt16(out UInt16 value)
        {
            if (!CanRead(PacketSize.UInt16))
            {
                value = default;
                return false;
            }

            value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.Slice(_offset, PacketSize.UInt16));
            _offset += PacketSize.UInt16;
            return true;
        }

        public bool TryReadInt32(out Int32 value)
        {
            if (!CanRead(PacketSize.Int32))
            {
                value = default;
                return false;
            }

            value = BinaryPrimitives.ReadInt32LittleEndian(_buffer.Slice(_offset, PacketSize.Int32));
            _offset += PacketSize.Int32;
            return true;
        }

        public bool TryReadUInt32(out UInt32 value)
        {
            if (!CanRead(PacketSize.UInt32))
            {
                value = default;
                return false;
            }

            value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.Slice(_offset, PacketSize.UInt32));
            _offset += PacketSize.UInt32;
            return true;
        }

        public bool TryReadInt64(out Int64 value)
        {
            if (!CanRead(PacketSize.Int64))
            {
                value = default;
                return false;
            }

            value = BinaryPrimitives.ReadInt64LittleEndian(_buffer.Slice(_offset, PacketSize.Int64));
            _offset += PacketSize.Int64;
            return true;
        }

        public bool TryReadUInt64(out UInt64 value)
        {
            if (!CanRead(PacketSize.UInt64))
            {
                value = default;
                return false;
            }

            value = BinaryPrimitives.ReadUInt64LittleEndian(_buffer.Slice(_offset, PacketSize.UInt64));
            _offset += PacketSize.UInt64;
            return true;
        }

        public bool TryReadFloat(out float value)
        {
            if (!TryReadInt32(out Int32 rawValue))
            {
                value = default;
                return false;
            }

            value = BitConverter.Int32BitsToSingle(rawValue);
            return true;
        }

        public bool TryReadDouble(out double value)
        {
            if (!TryReadInt64(out Int64 rawValue))
            {
                value = default;
                return false;
            }

            value = BitConverter.Int64BitsToDouble(rawValue);
            return true;
        }

        public bool TryReadString(out string value)
        {
            value = string.Empty;
            Int32 originalOffset = _offset;

            if (!TryReadUInt16(out UInt16 byteCount) || !CanRead(byteCount))
            {
                _offset = originalOffset;
                return false;
            }

            value = Encoding.UTF8.GetString(_buffer.Slice(_offset, byteCount));
            _offset += byteCount;
            return true;
        }

        private readonly bool CanRead(Int32 size)
        {
            return size >= 0 && _offset <= _buffer.Length - size;
        }
    }
}
