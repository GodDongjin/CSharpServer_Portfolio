using System.Net.Sockets;
using System.Diagnostics;
using System.Reflection;
using ServerCore.Network;
using ServerCore.Tests.Support;

namespace ServerCore.Tests
{
    public sealed class HeartbeatTests
    {
        private sealed class ProbeSession : Session
        {
            public List<Int64> Pings { get; } = new();
            protected override void SendHeartbeatPing(Int64 connectionId, Int64 sequence)
            {
                Pings.Add(sequence);
            }
        }

        private sealed class Fixture : IAsyncDisposable
        {
            public ProbeSession Session { get; }
            public RecordingOwner Owner { get; private set; } = new();
            private readonly SocketAsyncEventArgs _read = new();
            private readonly SocketAsyncEventArgs _write = new();
            public Int64 Id => Session._sessionID;
            public Fixture()
            {
                Session = new ProbeSession();
                Initialize();
            }

            private void Initialize()
            {
                Session.Initialize(Owner, new RecordingHandler(),
                    new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp),
                    _read, _write, Owner.ReleaseSession);
            }

            public void Reuse()
            {
                Assert.True(Owner.Released.Task.IsCompletedSuccessfully);
                Owner = new RecordingOwner();
                Initialize();
            }

            public void Advance(Int32 seconds)
            {
                // 테스트에서 기록 시점만 과거로 이동한다. 운영 코드에는 테스트용 시계를 두지 않는다.
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                Object lifetimeLock = typeof(Session).GetField("_lifetimeLock", flags)!.GetValue(Session)!;
                lock (lifetimeLock)
                {
                    foreach (String name in new[] { "_connectedTimestamp", "_lastHeartbeatTimestamp", "_pingTimestamp" })
                    {
                        FieldInfo field = typeof(Session).GetField(name, flags)!;
                        field.SetValue(Session, (Int64)field.GetValue(Session)! - seconds * Stopwatch.Frequency);
                    }
                }
            }

            public async ValueTask DisposeAsync()
            {
                Session.Disconnect();
                await Owner.Released.Task.WaitAsync(SessionHarness.Timeout);
                Session.Dispose();
                _read.Dispose();
                _write.Dispose();
            }
        }

        [Fact]
        public async Task Unauthenticated_pong_does_not_extend_login_deadline()
        {
            await using Fixture fixture = new();
            fixture.Advance(29);
            fixture.Session.ReceiveHeartbeatPong(fixture.Id, 1);
            Assert.True(fixture.Session.CheckHeartbeat(fixture.Id));
            Assert.Empty(fixture.Session.Pings);
            fixture.Advance(1);
            Assert.False(fixture.Session.CheckHeartbeat(fixture.Id));
            Assert.True(fixture.Owner.Released.Task.IsCompletedSuccessfully);
        }

        [Fact]
        public async Task Late_login_is_rejected_even_before_next_tick()
        {
            await using Fixture fixture = new();
            fixture.Advance(30);
            fixture.Session.MarkLoginCompleted(fixture.Id);
            Assert.False(fixture.Session.IsCurrentConnection(fixture.Id));
        }

        [Fact]
        public async Task Ping_interval_and_pong_deadline_enforce_thresholds()
        {
            await using Fixture fixture = new();
            fixture.Session.MarkLoginCompleted(fixture.Id);
            fixture.Advance(9);
            Assert.True(fixture.Session.CheckHeartbeat(fixture.Id));
            Assert.Empty(fixture.Session.Pings);
            fixture.Advance(1);
            Assert.True(fixture.Session.CheckHeartbeat(fixture.Id));
            Assert.Equal(new Int64[] { 1 }, fixture.Session.Pings);
            fixture.Advance(29);
            Assert.True(fixture.Session.CheckHeartbeat(fixture.Id));
            Assert.Single(fixture.Session.Pings);
            fixture.Advance(1);
            Assert.False(fixture.Session.CheckHeartbeat(fixture.Id));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task Only_current_ping_response_extends_connection(Boolean correctSequence)
        {
            await using Fixture fixture = new();
            fixture.Session.MarkLoginCompleted(fixture.Id);
            fixture.Advance(10);
            fixture.Session.CheckHeartbeat(fixture.Id);
            fixture.Advance(29);
            fixture.Session.ReceiveHeartbeatPong(fixture.Id, correctSequence ? 1 : 999);
            fixture.Advance(1);
            Assert.Equal(correctSequence, fixture.Session.CheckHeartbeat(fixture.Id));
            if (correctSequence)
            {
                fixture.Advance(9);
                fixture.Session.CheckHeartbeat(fixture.Id);
                Assert.Equal(new Int64[] { 1, 2 }, fixture.Session.Pings);
                // 이전 Ping에 대한 중복 응답으로 새 Ping의 대기를 해제하지 않는다.
                fixture.Session.ReceiveHeartbeatPong(fixture.Id, 1);
                fixture.Advance(30);
                Assert.False(fixture.Session.CheckHeartbeat(fixture.Id));
            }
        }

        [Fact]
        public async Task Pong_at_deadline_is_rejected_without_waiting_for_tick()
        {
            await using Fixture fixture = new();
            fixture.Session.MarkLoginCompleted(fixture.Id);
            fixture.Advance(10);
            fixture.Session.CheckHeartbeat(fixture.Id);
            fixture.Advance(30);
            fixture.Session.ReceiveHeartbeatPong(fixture.Id, 1);
            Assert.False(fixture.Session.IsCurrentConnection(fixture.Id));
        }

        [Fact]
        public async Task Reuse_resets_state_and_old_connection_cannot_login_or_answer_ping()
        {
            await using Fixture fixture = new();
            Int64 oldId = fixture.Id;
            fixture.Session.MarkLoginCompleted(oldId);
            fixture.Advance(10);
            fixture.Session.CheckHeartbeat(oldId);
            fixture.Session.Disconnect(oldId);
            fixture.Advance(100);
            fixture.Reuse();
            fixture.Session.MarkLoginCompleted(oldId);
            Assert.False(fixture.Session.CheckHeartbeat(oldId));
            fixture.Advance(29);
            Assert.True(fixture.Session.CheckHeartbeat(fixture.Id));
            fixture.Session.MarkLoginCompleted(fixture.Id);
            fixture.Advance(10);
            fixture.Session.CheckHeartbeat(fixture.Id);
            Assert.Equal(new Int64[] { 1, 1 }, fixture.Session.Pings);
            fixture.Session.ReceiveHeartbeatPong(oldId, 1);
            fixture.Advance(30);
            Assert.False(fixture.Session.CheckHeartbeat(fixture.Id));
        }

        [Fact]
        public async Task Timeout_disconnects_but_waits_for_running_job_before_release()
        {
            await using Fixture fixture = new();
            TaskCompletionSource started = SessionHarness.Signal();
            TaskCompletionSource finish = SessionHarness.Signal();
            try
            {
                Assert.True(fixture.Session.EnqueueAsyncJob(async () =>
                {
                    started.TrySetResult();
                    await finish.Task;
                }));
                await started.Task.WaitAsync(SessionHarness.Timeout);
                fixture.Advance(30);
                Assert.False(fixture.Session.CheckHeartbeat(fixture.Id));
                Assert.False(fixture.Owner.Released.Task.IsCompleted);
            }
            finally
            {
                finish.TrySetResult();
            }

            await fixture.Owner.Released.Task.WaitAsync(SessionHarness.Timeout);
        }

        [Fact]
        public async Task Worker_cancellation_completes_normally()
        {
            using CancellationTokenSource stop = new();
            Task worker = new SocketHeartBeat().RunAsync(stop.Token);
            Assert.False(worker.IsCompleted);
            stop.Cancel();
            await worker.WaitAsync(SessionHarness.Timeout);
        }
    }
}
