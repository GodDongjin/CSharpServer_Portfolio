using System;
using System.Buffers.Binary;
using Google.Protobuf.WellKnownTypes;
using ServerCore.Network;
using ServerCore.Packet;
using ServerCore.Tests.Support;

namespace ServerCore.Tests
{
    [Trait("Category", "Unit")]
    public sealed class BufferAndPacketTests
    {
        [Fact]
        public void Compaction_preserves_unconsumed_tail_and_restores_write_space()
        {
            using var buffer = new RecvBuffer(8);
            new Byte[]
            {
                1,
                2,
                3,
                4,
                5,
                6
            }.CopyTo(buffer.WriteMemory);
            Assert.True(buffer.OnWrite(6));
            Assert.True(buffer.OnRead(4));
            buffer.Clear();
            Assert.Equal(new Byte[] { 5, 6 }, buffer.ReadMemory.ToArray());
            Assert.Equal(6, buffer.FreeSize);
            new Byte[]
            {
                7,
                8
            }.CopyTo(buffer.WriteMemory);
            Assert.True(buffer.OnWrite(2));
            Assert.Equal(new Byte[] { 5, 6, 7, 8 }, buffer.ReadMemory.ToArray());
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(9)]
        public void Invalid_write_does_not_change_buffer_state(Int32 length)
        {
            using var buffer = new RecvBuffer(8);
            Assert.False(buffer.OnWrite(length));
            Assert.Equal(0, buffer.DataSize);
            Assert.Equal(8, buffer.FreeSize);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(5)]
        public void Invalid_read_does_not_consume_available_data(Int32 length)
        {
            using var buffer = new RecvBuffer(8);
            new Byte[]
            {
                1,
                2,
                3,
                4
            }.CopyTo(buffer.WriteMemory);
            Assert.True(buffer.OnWrite(4));
            Assert.False(buffer.OnRead(length));
            Assert.Equal(new Byte[] { 1, 2, 3, 4 }, buffer.ReadMemory.ToArray());
        }

        [Fact]
        public void Packet_buffer_contains_little_endian_header_and_round_trippable_payload()
        {
            var handler = new RecordingHandler();
            var message = new StringValue
            {
                Value = "한글 payload"
            };
            using var buffer = handler.MakeSendBuffer(0x1234, message);
            Assert.Equal(buffer.WriteSize, BinaryPrimitives.ReadUInt16LittleEndian(buffer.WrittenMemory.Span));
            Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16LittleEndian(buffer.WrittenMemory.Span[2..]));
            Assert.Equal(message, StringValue.Parser.ParseFrom(buffer.WrittenMemory.Span[PacketHeader.HeaderSize..]));
        }

        [Fact]
        public void Oversized_protobuf_message_is_rejected_before_send()
        {
            var handler = new RecordingHandler();
            var message = new StringValue
            {
                Value = new string ('x', PacketHeader.MaxPacketSize)
            };
            Assert.Throws<InvalidOperationException>(() => handler.MakeSendBuffer(1, message));
        }

        [Fact]
        public void Send_buffer_exposes_only_committed_bytes()
        {
            using var buffer = new SendBuffer(16);
            buffer.CopyData(new Byte[] { 1, 2, 3 });
            Assert.Equal(new Byte[] { 1, 2, 3 }, buffer.WrittenMemory.ToArray());
            Assert.Equal(3, buffer.ToArraySegment().Count);
            Assert.Throws<ArgumentOutOfRangeException>(() => buffer.Close(17));
            Assert.Equal(3, buffer.WriteSize);
        }
    }
}
