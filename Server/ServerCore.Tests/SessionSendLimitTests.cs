using System;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using ServerCore.Network;
using ServerCore.Tests.Support;

namespace ServerCore.Tests
{
    public sealed class SessionSendLimitTests
    {
        private const Int64 SendLimit = 1024 * 1024;

        private static FieldInfo Field(Type type, String name)
        {
            return type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException($"테스트 필드를 찾을 수 없습니다: {name}");
        }

        private static T Read<T>(Session session, String name)
        {
            return (T)Field(typeof(Session), name).GetValue(session)!;
        }

        private static void Set(Session session, String name, Object value)
        {
            Field(typeof(Session), name).SetValue(session, value);
        }

        private static SendBuffer Buffer(Int32 capacity, String content = "A")
        {
            SendBuffer buffer = new SendBuffer(capacity);
            buffer.CopyData(Encoding.ASCII.GetBytes(content));
            return buffer;
        }

        private static void AssertReturned(SendBuffer buffer)
        {
            Assert.True((Boolean)Field(typeof(SendBuffer), "_disposed").GetValue(buffer)!);
        }

        [Theory]
        [InlineData(1, true)]
        [InlineData(2, true)]
        [InlineData(3, false)]
        public async Task Limit_counts_pending_and_queued_capacity_and_accepts_exact_boundary(Int32 incomingSize, Boolean accepted)
        {
            await using var harness = SessionHarness.ForQueue();
            Session session = harness.Session;
            Int64 connectionId = session._sessionID;

            // OS의 송신 속도에 영향을 받지 않도록 송신 등록 상태만 고정한다.
            // 누적량은 직접 주입하지 않고 실제 Send 경로로 증가시킨다.
            Set(session, "_sendRegistered", true);
            SendBuffer pending = Buffer((Int32)(SendLimit / 2));
            session.Send(pending);
            Queue<SendBuffer> queued = Read<Queue<SendBuffer>>(session, "_sendBuffer");
            List<SendBuffer> inFlight = Read<List<SendBuffer>>(session, "_pendingSendBuffers");
            inFlight.Add(queued.Dequeue());

            SendBuffer waiting = Buffer((Int32)(SendLimit / 2 - 2));
            session.Send(waiting);
            Assert.Equal(SendLimit - 2, Read<Int64>(session, "_sendBufferBytes"));

            SendBuffer incoming = Buffer(incomingSize);
            session.Send(incoming);

            if (accepted)
            {
                Assert.True(session.IsCurrentConnection(connectionId));
                Assert.Equal(SendLimit - 2 + incomingSize, Read<Int64>(session, "_sendBufferBytes"));
                Assert.Equal(2, queued.Count);
                Assert.Single(inFlight);
                session.Disconnect();
            }
            else
            {
                Assert.False(session.IsCurrentConnection(connectionId));
            }

            await harness.Owner.Released.Task.WaitAsync(SessionHarness.Timeout);
            AssertReturned(incoming);
            AssertReturned(pending);
            AssertReturned(waiting);
            Assert.Equal(0L, Read<Int64>(session, "_sendBufferBytes"));
            Assert.Empty(queued);
            Assert.Empty(inFlight);
            Assert.Equal(1, harness.Owner.ReleaseCount);
        }

        [Fact]
        public async Task Disconnect_clears_accounting_before_reuse_and_next_connection_can_fill_limit()
        {
            await using var harness = SessionHarness.ForQueue();
            Session session = harness.Session;
            Int64 previousConnectionId = session._sessionID;
            Set(session, "_sendRegistered", true);
            SendBuffer previous = Buffer((Int32)SendLimit);
            session.Send(previous);
            session.Disconnect();
            await harness.Owner.Released.Task.WaitAsync(SessionHarness.Timeout);

            // Initialize가 값을 덮어쓰기 전에 Reset 자체의 정리를 확인한다.
            Assert.Equal(0L, Read<Int64>(session, "_sendBufferBytes"));
            AssertReturned(previous);
            harness.ReinitializeQueueSession();
            Assert.NotEqual(previousConnectionId, session._sessionID);

            Set(session, "_sendRegistered", true);
            SendBuffer next = Buffer((Int32)SendLimit);
            session.Send(next);
            Assert.True(session.IsCurrentConnection(session._sessionID));
            Assert.Equal(SendLimit, Read<Int64>(session, "_sendBufferBytes"));
            session.Disconnect();
            await harness.Owner.Released.Task.WaitAsync(SessionHarness.Timeout);
            AssertReturned(next);
            Assert.Equal(0L, Read<Int64>(session, "_sendBufferBytes"));
        }

        [Theory]
        [Trait("Category", "Loopback")]
        [InlineData(3)]
        [InlineData(8)]
        [InlineData(10)]
        [InlineData(14)]
        public async Task Partial_completion_returns_full_capacity_and_sends_only_remaining_bytes(Int32 transferred)
        {
            await using var harness = await SessionHarness.ConnectAsync();
            Session session = harness.Session;
            Object lifetimeLock = Read<Object>(session, "_lifetimeLock");
            SendBuffer first = Buffer(16, "ABCDEFGH");
            SendBuffer second = Buffer(32, "IJKLMN");
            Byte[] expected = Encoding.ASCII.GetBytes("ABCDEFGHIJKLMN")[transferred..];

            lock (lifetimeLock)
            {
                Set(session, "_sendRegistered", true);
                session.Send(first);
                session.Send(second);
                Assert.Equal(48L, Read<Int64>(session, "_sendBufferBytes"));

                Queue<SendBuffer> queued = Read<Queue<SendBuffer>>(session, "_sendBuffer");
                List<SendBuffer> pending = Read<List<SendBuffer>>(session, "_pendingSendBuffers");
                while (queued.Count > 0)
                {
                    pending.Add(queued.Dequeue());
                }

                SocketAsyncEventArgs args = session.WriteArgs;
                args.BufferList = pending.Select(buffer => buffer.ToArraySegment()).ToList();
                Set(session, "_sendPending", 1);

                // 부분 완료는 OS 타이밍으로 강제할 수 없으므로 완료 바이트 수를 주입한다.
                // 이후 남은 데이터의 재송신은 실제 루프백 TCP를 사용한다.
                Field(typeof(SocketAsyncEventArgs), "_bytesTransferred").SetValue(args, transferred);
                args.SocketError = SocketError.Success;
                typeof(Session).GetMethod("ProcessSend", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(session, new Object[] { args });
            }

            Assert.Equal(expected, await harness.ReceiveAtPeerAsync(expected.Length));
            using var deadline = new CancellationTokenSource(SessionHarness.Timeout);
            while (true)
            {
                lock (lifetimeLock)
                {
                    if (!Read<Boolean>(session, "_sendRegistered"))
                    {
                        Assert.Equal(0L, Read<Int64>(session, "_sendBufferBytes"));
                        Assert.Empty(Read<List<SendBuffer>>(session, "_pendingSendBuffers"));
                        Assert.Equal(0, Read<Int32>(session, "_sendPending"));
                        break;
                    }
                }

                await Task.Delay(1, deadline.Token);
            }

            Assert.True(session.IsCurrentConnection(session._sessionID));
            AssertReturned(first);
            AssertReturned(second);
            Assert.Equal(0, harness.Peer!.Available);
        }
    }
}
