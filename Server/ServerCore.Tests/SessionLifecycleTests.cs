using System;
using System.Net.Sockets;
using ServerCore.Network;
using ServerCore.Tests.Support;

namespace ServerCore.Tests
{
    [Trait("Category", "Unit")]
    public sealed class SessionLifecycleTests
    {
        [Fact]
        public async Task Disconnect_defers_release_until_running_job_completes_and_rejects_new_jobs()
        {
            await using var harness = SessionHarness.ForQueue();
            var gate = SessionHarness.Signal();
            var started = SessionHarness.Signal();
            Int64 connectionId = harness.Session._sessionID;
            try
            {
                Assert.True(harness.Session.EnqueueAsyncJob(async () =>
                {
                    started.SetResult();
                    await gate.Task;
                }));
                await started.Task.WaitAsync(SessionHarness.Timeout);
                harness.Session.Disconnect();
                Assert.False(harness.Session.IsCurrentConnection(connectionId));
                Assert.False(harness.Session.EnqueueAsyncJob(() => ValueTask.CompletedTask));
                Assert.False(harness.Owner.Released.Task.IsCompleted);
                Assert.Equal(0, harness.Owner.ReleaseCount);
                gate.SetResult();
                await harness.Owner.Released.Task.WaitAsync(SessionHarness.Timeout);
                Assert.Equal(1, harness.Owner.ReleaseCount);
            }
            finally
            {
                gate.TrySetResult();
            }
        }

        [Fact]
        public async Task Concurrent_disconnect_calls_release_session_once()
        {
            await using var harness = SessionHarness.ForQueue();
            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(harness.Session.Disconnect))).WaitAsync(SessionHarness.Timeout);
            await harness.Owner.Released.Task.WaitAsync(SessionHarness.Timeout);
            Assert.Equal(1, harness.Owner.ReleaseCount);
        }

        [Fact]
        public async Task Reused_session_rejects_old_connection_id_and_accepts_new_jobs()
        {
            await using var harness = SessionHarness.ForQueue();
            Int64 previousId = harness.Session._sessionID;
            Assert.True(harness.Session.IsCurrentConnection(previousId));
            harness.Session.Disconnect();
            await harness.Owner.Released.Task.WaitAsync(SessionHarness.Timeout);
            harness.ReinitializeQueueSession();
            Assert.NotEqual(previousId, harness.Session._sessionID);
            Assert.False(harness.Session.IsCurrentConnection(previousId));
            Assert.True(harness.Session.IsCurrentConnection(harness.Session._sessionID));
            Assert.False(harness.Session.EnqueueAsyncJob(previousId, () => throw new InvalidOperationException("Old job executed.")));
            harness.Session.Disconnect(previousId);
            Assert.True(harness.Session.IsCurrentConnection(harness.Session._sessionID));
            var completed = SessionHarness.Signal();
            Assert.True(harness.Session.EnqueueAsyncJob(() =>
            {
                completed.SetResult();
                return ValueTask.CompletedTask;
            }));
            await completed.Task.WaitAsync(SessionHarness.Timeout);
        }

        [Fact]
        public async Task Active_session_cannot_be_reset_disposed_or_initialized_again()
        {
            await using var harness = SessionHarness.ForQueue();
            using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using SocketAsyncEventArgs readArgs = new SocketAsyncEventArgs();
            using SocketAsyncEventArgs writeArgs = new SocketAsyncEventArgs();

            Assert.Throws<InvalidOperationException>(harness.Session.Reset);
            Assert.Throws<InvalidOperationException>(harness.Session.Dispose);
            Assert.Throws<InvalidOperationException>(harness.Session.RetrunEventArgs);
            Assert.Throws<InvalidOperationException>(() => harness.Session.Initialize(harness.Owner, harness.Handler, socket, readArgs, writeArgs));
            Assert.True(harness.Session.IsCurrentConnection(harness.Session._sessionID));
        }

        [Fact]
        public async Task Receive_registration_failure_still_returns_session_once()
        {
            await using var harness = SessionHarness.ForQueue();
            // 연결하지 않은 소켓의 ReceiveAsync는 동기적으로 실패한다.
            harness.Session.StartSession();
            await harness.Owner.Released.Task.WaitAsync(SessionHarness.Timeout);
            Assert.Equal(1, harness.Owner.ReleaseCount);
        }

        [Fact]
        public async Task Disconnect_hook_finishes_before_reset_and_event_return()
        {
            using HookSession session = new HookSession();
            RecordingOwner owner = new RecordingOwner();
            using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using SocketAsyncEventArgs readArgs = new SocketAsyncEventArgs();
            using SocketAsyncEventArgs writeArgs = new SocketAsyncEventArgs();
            session.Initialize(owner, new RecordingHandler(), socket, readArgs, writeArgs, owner.ReleaseSession);
            Task disconnect = Task.Run(session.Disconnect);
            try
            {
                await session.Entered.Task.WaitAsync(SessionHarness.Timeout);
                session.Disconnect();
                Assert.False(owner.Released.Task.IsCompleted);
                Assert.Equal(0, owner.ReleaseCount);
            }
            finally
            {
                session.Continue.Set();
                await disconnect.WaitAsync(SessionHarness.Timeout);
            }

            Assert.Equal(1, owner.ReleaseCount);
            Assert.True(session.ResetAfterHook);
        }

        private sealed class HookSession : Session
        {
            public TaskCompletionSource Entered = SessionHarness.Signal();
            public ManualResetEventSlim Continue = new ManualResetEventSlim();
            private bool _hookFinished;
            public bool ResetAfterHook;

            protected override void OnDisconnected(Int64 connectionId)
            {
                Entered.TrySetResult();
                if (!Continue.Wait(SessionHarness.Timeout))
                {
                    throw new TimeoutException("Disconnect hook gate timed out.");
                }

                _hookFinished = true;
            }

            protected override void OnReset()
            {
                ResetAfterHook = _hookFinished;
            }
        }
    }
}
