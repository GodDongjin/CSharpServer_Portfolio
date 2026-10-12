using System;
using System.Buffers.Binary;
using System.Net.Sockets;
using ServerCore.Network;
using ServerCore.Packet;
using ServerCore.Tests.Support;

namespace ServerCore.Tests
{
    [Trait("Category", "Loopback")]
    public sealed class SessionSocketTests
    {
        private static Byte[] Frame(UInt16 id, Int32 payloadSize = 16)
        {
            Byte[] bytes = new Byte[PacketHeader.HeaderSize + payloadSize];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((UInt16)bytes.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2), id);
            for (Int32 i = PacketHeader.HeaderSize; i < bytes.Length; i++)
                bytes[i] = (Byte)(i % 251);
            return bytes;
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(11)]
        public async Task Frame_written_in_two_parts_is_delivered_intact(Int32 split)
        {
            await using var harness = await SessionHarness.ConnectAsync();
            Byte[] frame = Frame(42);
            await harness.SendFromPeerAsync(frame.AsMemory(0, split));
            await harness.SendFromPeerAsync(frame.AsMemory(split));
            Assert.Equal(frame, await harness.Handler.NextAsync());
            Assert.False(harness.Handler.HasPacket);
        }

        [Fact]
        public async Task Several_frames_in_one_write_are_dispatched_in_order()
        {
            await using var harness = await SessionHarness.ConnectAsync();
            Byte[][] frames = [Frame(10), Frame(11, 0), Frame(12, 100)];
            await harness.SendFromPeerAsync(frames.SelectMany(frame => frame).ToArray());
            foreach (Byte[] frame in frames)
            {
                Assert.Equal(frame, await harness.Handler.NextAsync());
            }

            Assert.False(harness.Handler.HasPacket);
        }

        [Fact]
        public async Task Maximum_length_frame_is_delivered_intact()
        {
            await using var harness = await SessionHarness.ConnectAsync();
            Byte[] frame = Frame(42, PacketHeader.MaxPacketSize - PacketHeader.HeaderSize);
            await harness.SendFromPeerAsync(frame);
            Assert.Equal(frame, await harness.Handler.NextAsync());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(3)]
        public async Task Size_smaller_than_header_disconnects_without_dispatch(Int32 size)
        {
            await using var harness = await SessionHarness.ConnectAsync();
            Byte[] frame = Frame(42, 0);
            BinaryPrimitives.WriteUInt16LittleEndian(frame, (UInt16)size);
            await harness.SendFromPeerAsync(frame);
            await harness.Owner.Released.Task.WaitAsync(SessionHarness.Timeout);
            Assert.False(harness.Handler.HasPacket);
            Assert.Equal(1, harness.Owner.ReleaseCount);
        }

        [Fact]
        public async Task Peer_close_with_incomplete_frame_releases_without_dispatch()
        {
            await using var harness = await SessionHarness.ConnectAsync();
            await harness.SendFromPeerAsync(Frame(42).AsMemory(0, 6));
            harness.Peer!.Shutdown(SocketShutdown.Send);
            await harness.Owner.Released.Task.WaitAsync(SessionHarness.Timeout);
            Assert.False(harness.Handler.HasPacket);
            Assert.Equal(1, harness.Owner.ReleaseCount);
        }

        [Fact]
        public async Task Handler_rejection_disconnects_the_connection()
        {
            await using var harness = await SessionHarness.ConnectAsync();
            harness.Handler.AcceptPackets = false;
            await harness.SendFromPeerAsync(Frame(42));
            await harness.Owner.Released.Task.WaitAsync(SessionHarness.Timeout);
            Assert.Equal(1, harness.Owner.ReleaseCount);
        }

        [Fact]
        public async Task Send_preserves_byte_order_across_multiple_buffers()
        {
            await using var harness = await SessionHarness.ConnectAsync();
            Byte[][] frames = [Frame(10, 3000), Frame(11, 5000), Frame(12, 7000)];
            foreach (Byte[] frame in frames)
            {
                var buffer = new SendBuffer(frame.Length);
                buffer.CopyData(frame);
                harness.Session.Send(buffer); // Session owns and returns the buffer from here.
            }

            Byte[] expected = frames.SelectMany(frame => frame).ToArray();
            Assert.Equal(expected, await harness.ReceiveAtPeerAsync(expected.Length));
        }
    }
}
