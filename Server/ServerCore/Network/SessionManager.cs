using ServerCore.Interface;
using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;

namespace ServerCore.Network
{
    public static class SessionManager
    {
        private static readonly SemaphoreSlim InitializeLock = new(1, 1);
        private static readonly object _lock = new object();
        private static bool IsInitialized;
        private static bool _stopping;
        private static Exception? _releaseError;

        // 새션 풀 List
        private static readonly ConcurrentQueue<Session> _sessionQueue = new ConcurrentQueue<Session>();
        // 사용중인 새션 List
        private static readonly ConcurrentDictionary<Int64, Session> _sessionDic = new ConcurrentDictionary<Int64, Session>();

        // 반환 요청은 Session 자체를 전달한다. 소비자는 하나이며 등록 스레드에서 정리를 즉시 실행하지 않는다.
        private static readonly Channel<Session> _sessionChannel = Channel.CreateUnbounded<Session>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false
        });

        private static Task _releaseTask = Task.CompletedTask;
        private static SemaphoreSlim? _maxSessionsEnforcer;
        // 스레드를 막지 않고 전체 반환 완료를 기다린다. 활성 세션이 처음 생길 때 새 신호를 만든다.
        private static TaskCompletionSource _allSessionsReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private static Task? _stopTask;

        public static async Task Initialize(Int32 maxSessionCount, Func<Session> sessionFactory)
        {
            if (maxSessionCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxSessionCount), "최대 세션 수는 0보다 커야 합니다.");
            }

            ArgumentNullException.ThrowIfNull(sessionFactory);

            await InitializeLock.WaitAsync();
            try
            {
                lock (_lock)
                {
                    if (IsInitialized || _stopping)
                    {
                        throw new InvalidOperationException("SessionManager는 다시 초기화할 수 없습니다.");
                    }

                    List<Session> sessions = new List<Session>();
                    try
                    {
                        for (Int32 i = 0; i < maxSessionCount; i++)
                        {
                            sessions.Add(sessionFactory());
                        }
                    }
                    catch
                    {
                        foreach (Session session in sessions)
                        {
                            session.Dispose();
                        }

                        throw;
                    }

                    foreach (Session session in sessions)
                    {
                        _sessionQueue.Enqueue(session);
                    }

                    _maxSessionsEnforcer = new SemaphoreSlim(maxSessionCount, maxSessionCount);

                    // 요청마다 작업자를 시작하던 경합을 없애고, 초기화 시 하나만 시작해 Task를 보관한다.
                    _releaseTask = StartRelease();
                    IsInitialized = true;
                }
            }
            finally
            {
                InitializeLock.Release();
            }
        }

        // 종료 표시 확인부터 사전 등록까지 잠가서 종료 대상에서 생성 중인 세션이 빠지지 않게 한다.
        public static Session? CreateSession(Socket? socket, SocketAsyncEventArgs readArgs, SocketAsyncEventArgs writeArgs, ISessionOwner sessionOwner, IPacketHandler? packetHandler)
        {
            lock (_lock)
            {
                if (!IsInitialized)
                {
                    throw new InvalidOperationException("SessionManager.Initialize()를 먼저 완료해야 합니다.");
                }

                if (_stopping || socket == null || packetHandler == null || !_maxSessionsEnforcer!.Wait(0))
                {
                    socket?.Close();
                    return null;
                }

                if (!_sessionQueue.TryDequeue(out Session? session))
                {
                    _maxSessionsEnforcer.Release();
                    socket.Close();
                    return null;
                }

                try
                {
                    session.Initialize(sessionOwner, packetHandler, socket, readArgs, writeArgs);
                }
                catch (Exception exception)
                {
                    // 초기화 실패 객체는 재사용하지 않는다. 인수의 소유권은 호출자에게 남는다.
                    _releaseError = exception;
                    _stopping = true;
                    Console.Error.WriteLine($"세션 초기화 실패: {exception}");
                    socket.Close();
                    _maxSessionsEnforcer.Release();
                    return null;
                }

                if (_sessionDic.IsEmpty)
                {
                    _allSessionsReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                _sessionDic[session._sessionID] = session;
                return session;
            }
        }

        public static Task AllReleaseSessionAsync()
        {
            lock (_lock)
            {
                if (_stopTask != null)
                {
                    return _stopTask;
                }

                // 종료 요청은 한 번만 수행한다. 이후 호출은 같은 완료 Task를 받는다.
                _stopping = true;
                KeyValuePair<Int64, Session>[] sessions = _sessionDic.ToArray();
                if (_releaseError != null)
                {
                    _allSessionsReleased.TrySetException(_releaseError);
                }
                else if (_sessionDic.IsEmpty)
                {
                    _allSessionsReleased.TrySetResult();
                }

                Task released = _allSessionsReleased.Task;
                // Disconnect의 동기 처리나 잠금이 지연돼도 호출자는 경고를 기록하며 기다릴 수 있다.
                _stopTask = Task.Run(() => ReleaseAllSessionsAsync(sessions, released));
                return _stopTask;
            }
        }

        private static async Task ReleaseAllSessionsAsync(KeyValuePair<Int64, Session>[] sessions, Task released)
        {
            foreach (KeyValuePair<Int64, Session> entry in sessions)
            {
                try
                {
                    entry.Value.Disconnect(entry.Key);
                }
                catch (Exception exception)
                {
                    // 한 세션의 종료 오류가 다른 세션의 종료 요청을 막지 않도록 한다.
                    lock (_lock)
                    {
                        _releaseError = exception;
                        _allSessionsReleased.TrySetException(exception);
                    }
                }
            }

            // 시간 제한이나 강제 Reset 없이 실제 반환 완료를 기다린다.
            await released;
            lock (_lock)
            {
                if (_releaseError != null)
                {
                    throw new InvalidOperationException("세션 정리 중 오류가 발생했습니다.", _releaseError);
                }
            }

            _sessionChannel.Writer.TryComplete();
            await _releaseTask;
        }

        public static Session? GetSessionById(Int64 sessionId)
        {
            if (_sessionDic.TryGetValue(sessionId, out Session? session))
            {
                return session;
            }

            return null;
        }

        public static Session? GetSessionByUser_Id(Int32 userId)
        {
            foreach (Session session in _sessionDic.Values)
            {
                if (session._userID == userId)
                {
                    return session;
                }
            }

            return null;
        }

        public static IReadOnlyDictionary<Int64, Session> GetSessionAndConnection()
        {
            // 키는 등록 당시 연결 ID다. 세션 객체가 재사용되어도 이 ID로 다시 검사한다.
            return new Dictionary<Int64, Session>(_sessionDic.ToArray());
        }

        internal static void ReleaseRegister(Session session)
        {
            // Session의 종료 경로에서 한 번만 호출한다. 외부에서 중복 등록하지 않는다.
            if (!_sessionChannel.Writer.TryWrite(session))
            {
                throw new InvalidOperationException("세션 반환 채널이 종료되었습니다.");
            }
        }

        private static async Task StartRelease()
        {
            await foreach (Session session in _sessionChannel.Reader.ReadAllAsync())
            {
                try
                {
                    // 순서: Reset → 이벤트 풀 반환·참조 해제 → 세션 큐 공개·사용 슬롯 반환.
                    // 사전에서 먼저 제거하면 서버 종료가 정리 완료로 오인할 수 있으므로 이벤트 반환 뒤에 제거한다.
                    session.Reset();
                    session.RetrunEventArgs();

                    lock (_lock)
                    {
                        _sessionDic.TryRemove(session._sessionID, out _);
                        _sessionQueue.Enqueue(session);
                        _maxSessionsEnforcer!.Release();

                        if (_sessionDic.IsEmpty)
                        {
                            _allSessionsReleased.TrySetResult();
                        }
                    }
                }
                catch (Exception exception)
                {
                    lock (_lock)
                    {
                        // 정리 실패한 세션은 재사용하지 않는다. 완료 Task에 예외를 전달한다.
                        _releaseError = exception;
                        _stopping = true;
                        _allSessionsReleased.TrySetException(exception);
                    }

                    Console.Error.WriteLine($"세션 반환 실패: {exception}");
                }
            }
        }
    }
}
