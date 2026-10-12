using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using ServerCore.Interface;
using ServerCore.Network;
using ServerCore.Tests.Support;

namespace ServerCore.Tests
{
    [Trait("Category", "Loopback")]
    public sealed class SessionManagerTests
    {
        // 프로세스 전체에서 한 번 초기화하는 실제 Manager의 전체 수명을 검사한다.
        [Fact]
        public async Task Event_return_precedes_reuse_and_shutdown_waits_for_jobs_and_return_worker()
        {
            RecordingHandler handler = new RecordingHandler();
            Service service = new Service(SERVICE_TYPE.GAME_SERVER, handler, 1);
            GatedOwner owner = new GatedOwner(service);
            using ManualResetEventSlim closeContinue = new ManualResetEventSlim();
            TaskCompletionSource closeEntered = SessionHarness.Signal();
            GatedSession pooledSession = new GatedSession(closeEntered, closeContinue);
            await SessionManager.Initialize(1, () => pooledSession);
            TaskCompletionSource jobGate = SessionHarness.Signal();

            Session Create(Socket socket)
            {
                SocketAsyncEventArgs readArgs = Assert.IsType<SocketAsyncEventArgs>(service.ReadPool.Pop());
                SocketAsyncEventArgs writeArgs = Assert.IsType<SocketAsyncEventArgs>(service.WritePool.Pop());
                return Assert.IsAssignableFrom<Session>(SessionManager.CreateSession(socket, readArgs, writeArgs, owner, handler));
            }

            void AssertNewConnectionRejected()
            {
                using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using SocketAsyncEventArgs readArgs = new SocketAsyncEventArgs();
                using SocketAsyncEventArgs writeArgs = new SocketAsyncEventArgs();
                Assert.Null(SessionManager.CreateSession(socket, readArgs, writeArgs, owner, handler));
                Assert.Null(readArgs.UserToken);
                Assert.Null(writeArgs.UserToken);
            }

            try
            {
                service.StartServer(IPAddress.Loopback, 0);
                Assert.False(service.HeartbeatTask.IsCompleted);
                owner.BlockReturn = true;
                Session first = Create(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp));
                Int64 firstId = first._sessionID;
                IReadOnlyDictionary<Int64, Session> snapshot = SessionManager.GetSessionAndConnection();
                Assert.Same(first, snapshot[firstId]);
                SocketAsyncEventArgs firstRead = first.ReadArgs;
                SocketAsyncEventArgs firstWrite = first.WriteArgs;
                await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(first.Disconnect)));
                await owner.Entered.Task.WaitAsync(SessionHarness.Timeout);

                Assert.Same(first, SessionManager.GetSessionById(firstId));
                Assert.Equal(0, service.ReadPool.Count);
                Assert.Equal(0, service.WritePool.Count);
                Assert.Null(firstRead.UserToken);
                Assert.Null(firstWrite.UserToken);
                AssertNewConnectionRejected();

                owner.Continue.Set();
                await WaitUntilReturned(firstId);
                Assert.Same(first, snapshot[firstId]);
                Assert.Empty(SessionManager.GetSessionAndConnection());
                Assert.Equal(1, owner.ReturnCount);
                Assert.Equal(1, service.ReadPool.Count);
                Assert.Equal(1, service.WritePool.Count);
                Assert.Throws<InvalidOperationException>(() => first.ReadArgs);
                Assert.Throws<InvalidOperationException>(() => first.WriteArgs);
                owner.BlockReturn = false;

                // 실제 소켓의 송수신 완료와 동시 종료 후 같은 Session/이벤트를 반복 재사용한다.
                using TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                for (Int32 i = 0; i < 16; i++)
                {
                    using CancellationTokenSource deadline = new CancellationTokenSource(SessionHarness.Timeout);
                    using TcpClient peer = new TcpClient();
                    await peer.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
                    Socket accepted = await listener.AcceptSocketAsync(deadline.Token);
                    accepted.SendBufferSize = 1024;
                    Session current = Create(accepted);
                    Int64 currentId = current._sessionID;
                    Assert.Same(first, current);
                    Assert.Same(firstRead, current.ReadArgs);
                    Assert.Same(firstWrite, current.WriteArgs);
                    Assert.False(current.EnqueueAsyncJob(firstId, () => throw new InvalidOperationException("Stale job executed.")));
                    current.Disconnect(firstId);
                    Assert.True(current.IsCurrentConnection(currentId));
                    current.StartSession();
                    current.StartSession(); // 이중 Receive 등록을 만들지 않는다.

                    await peer.GetStream().WriteAsync(new Byte[] { 4, 0, 42, 0 }, deadline.Token);
                    Assert.Equal(new Byte[] { 4, 0, 42, 0 }, await handler.NextAsync());
                    Task[] sends = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                    {
                        SendBuffer buffer = new SendBuffer(65536);
                        buffer.CopyData(new Byte[65536]);
                        current.Send(buffer);
                    })).ToArray();
                    Task close = Task.Run(() => current.Disconnect(currentId));
                    await Task.WhenAll(sends.Append(close)).WaitAsync(SessionHarness.Timeout);
                    await WaitUntilReturned(currentId);
                    Assert.Equal(i + 2, owner.ReturnCount);
                    Assert.Equal(1, service.ReadPool.Count);
                    Assert.Equal(1, service.WritePool.Count);
                    Assert.Null(firstRead.UserToken);
                    Assert.Null(firstWrite.UserToken);
                }

                owner.Entered = SessionHarness.Signal();
                owner.Continue.Reset();
                owner.BlockReturn = true;
                Session last = Create(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp));
                TaskCompletionSource jobStarted = SessionHarness.Signal();
                Assert.True(last.EnqueueAsyncJob(async () =>
                {
                    jobStarted.TrySetResult();
                    await jobGate.Task;
                }));
                await jobStarted.Task.WaitAsync(SessionHarness.Timeout);
                pooledSession.BlockClose = true;
                Task stopTask = service.StopServerAsync();
                await closeEntered.Task.WaitAsync(SessionHarness.Timeout);
                // 세션 정리 대기 전에 Heartbeat 작업부터 종료되어야 한다.
                Assert.True(service.HeartbeatTask.IsCompletedSuccessfully);
                Assert.Same(stopTask, service.StopServerAsync());
                await Task.Delay(30);
                Assert.False(stopTask.IsCompleted);
                AssertNewConnectionRejected();
                closeContinue.Set();

                jobGate.TrySetResult();
                await owner.Entered.Task.WaitAsync(SessionHarness.Timeout);
                Assert.False(stopTask.IsCompleted);
                Assert.Equal(0, service.ReadPool.Count);
                owner.Continue.Set();
                await stopTask.WaitAsync(SessionHarness.Timeout);
                Assert.Null(SessionManager.GetSessionById(last._sessionID));
                Assert.Equal(18, owner.ReturnCount);
                Assert.Equal(0, service.ReadPool.Count);
                Assert.Equal(0, service.WritePool.Count);
                Assert.Same(stopTask, service.StopServerAsync());
                Assert.True(stopTask.IsCompletedSuccessfully);
                AssertNewConnectionRejected();
                last.Dispose();
            }
            finally
            {
                jobGate.TrySetResult();
                owner.Continue.Set();
                closeContinue.Set();
                await service.StopServerAsync().WaitAsync(SessionHarness.Timeout);
                owner.Continue.Dispose();
            }
        }

        private static async Task WaitUntilReturned(Int64 connectionId)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            while (SessionManager.GetSessionById(connectionId) != null)
            {
                if (stopwatch.Elapsed > SessionHarness.Timeout)
                {
                    throw new TimeoutException("Session was not returned.");
                }

                await Task.Delay(1);
            }
        }

        private sealed class GatedSession : Session
        {
            private readonly TaskCompletionSource _entered;
            private readonly ManualResetEventSlim _continue;
            public bool BlockClose;

            public GatedSession(TaskCompletionSource entered, ManualResetEventSlim continueClose)
            {
                _entered = entered;
                _continue = continueClose;
            }

            protected override void OnDisconnected(Int64 connectionId)
            {
                if (BlockClose)
                {
                    _entered.TrySetResult();
                    if (!_continue.Wait(SessionHarness.Timeout))
                    {
                        throw new TimeoutException("Disconnect gate timed out.");
                    }
                }
            }
        }

        private sealed class GatedOwner : ISessionOwner
        {
            private readonly Service _service;
            private Int32 _returnCount;
            public bool BlockReturn;
            public TaskCompletionSource Entered = SessionHarness.Signal();
            public ManualResetEventSlim Continue = new ManualResetEventSlim();
            public Int32 ReturnCount { get { return Volatile.Read(ref _returnCount); } }

            public GatedOwner(Service service)
            {
                _service = service;
            }

            public void RetrunEventArg(SocketAsyncEventArgs read_args, SocketAsyncEventArgs write_args)
            {
                if (BlockReturn)
                {
                    Entered.TrySetResult();
                    if (!Continue.Wait(SessionHarness.Timeout))
                    {
                        throw new TimeoutException("Event return gate timed out.");
                    }
                }

                _service.RetrunEventArg(read_args, write_args);
                Interlocked.Increment(ref _returnCount);
            }
        }
    }
}
