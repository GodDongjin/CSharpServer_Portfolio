using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Buffers.Binary;
using ServerCore.Interface;
using ServerCore.Network;
using Google.Protobuf;

namespace ServerCore.Packet
{
    public delegate void PacketWriteHandler(Span<byte> payload);

    public readonly struct PacketHeader
    {
        public const ushort HeaderSize = 4;

        public const ushort MaxPacketSize = ushort.MaxValue;

        public ushort Size { get; }
        public ushort Id { get; }

        public PacketHeader(ushort size, ushort id)
        {
            Size = size;
            Id = id;
        }
    }

    abstract public class PacketHandler : IPacketHandler
    {

        public abstract bool HandlePacket(Session session, ReadOnlySpan<byte> data);

        public SendBuffer MakeSendBuffer<T>(ushort packetId              , T message) where T : IMessage<T>
        {
            ArgumentNullException.ThrowIfNull(message);

            int payloadSize = message.CalculateSize();

            int packetSize = PacketHeader.HeaderSize + payloadSize;


            if (packetSize > ushort.MaxValue)
            {
                throw new InvalidOperationException($"패킷이 너무 큽니다: {packetSize}");
            }

            SendBuffer sendBuffer = new SendBuffer(packetSize);
            Span<byte> buffer = sendBuffer.Buffer.Span;

            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(0, 2), (ushort)packetSize);
            BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(2, 2), packetId);

            message.WriteTo(buffer.Slice(PacketHeader.HeaderSize, payloadSize));

            sendBuffer.Close(packetSize);

            return sendBuffer;
        }

        abstract public void Initialize();

       

    }
}
