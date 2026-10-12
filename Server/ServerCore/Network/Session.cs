using Google.Protobuf;
using ServerCore.Diagnostics;
using ServerCore.Interface;
using ServerCore.Packet;
using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace ServerCore.Network
{

    public class Session : IDisposable
    {
        private static long _nextSessionId;

        // [세션 수명 수정] pending만 검사하면 송신 준비나 콜백의 다음 I/O 등록 사이에 Reset이 실행될 수 있다.
        // 초기화, 송수신 준비/콜백, 종료, 반환은 같은 잠금 안에서 처리한다.
        // 잠금 밖에서 대기하는 I/O와 비동기 작업은 pending 값으로 수명을 유지한다.
        private readonly object _lifetimeLock = new object();
        private Action<Session> _releaseSession = SessionManager.ReleaseRegister;
        private bool _started;
        private bool _disconnecting;
        private bool _resetCompleted;
        private bool _disposed;
        private static readonly TimeSpan LoginTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(30);
        private Int64 _connectedTimestamp;
        private Int64 _lastHeartbeatTimestamp;
        private Int64 _pingTimestamp;
        private Int64 _pingSequence;
        private bool _waitingPong;
        private bool _loginCompleted;

        private ISessionOwner? _owner;
        protected IPacketHandler? _packetHandler;

        private Socket? _sessionSocket;
        private SocketAsyncEventArgs? _readArgs;
        private SocketAsyncEventArgs? _writeArgs;

        private readonly RecvBuffer _recvBuffer;
        private readonly Queue<SendBuffer> _sendBuffer;
        private readonly List<SendBuffer> _pendingSendBuffers = new();

        private readonly Queue<Func<ValueTask>> _recvQueue = new();
        private bool _recvQueueRunning;
        private readonly object _recvQueueLock = new object();
        private const int _maxRecvJobCount = 30;
        private const Int64 MaxSendBufferBytes = 1024 * 1024;
        private Int64 _sendBufferBytes;
        private bool _sendRegistered = false;

        private int _receivePending;
        private int _sendPending;
        private int _asyncJobPending;
        private int _disconnectRequested;
        private int _released;

        private readonly object _sendLock = new object();

        public Int64 _sessionID { get; private set; }
        public Int64 _accountIDX { get; protected set; }
        public Int32 _userID { get; protected set; }

        public SocketAsyncEventArgs ReadArgs
        {
            get { return _readArgs ?? throw new InvalidOperationException("수신 이벤트가 초기화되지 않았습니다."); }
        }
        public SocketAsyncEventArgs WriteArgs
        {
            get { return _writeArgs ?? throw new InvalidOperationException("송신 이벤트가 초기화되지 않았습니다."); }
        }


        public Session()
        {
            _recvBuffer = new RecvBuffer();
            _sendBuffer = new Queue<SendBuffer>();
            _disconnectRequested = 1;
            _released = 1;
        }

        public void Initialize(ISessionOwner owner, IPacketHandler packetHandler, Socket socket, SocketAsyncEventArgs readArgs, SocketAsyncEventArgs writeArgs)
        {
            Initialize(owner, packetHandler, socket, readArgs, writeArgs, SessionManager.ReleaseRegister);
        }

        // [세션 수명 수정] 테스트에서만 반환 작업자를 대체한다. 실제 서버는 SessionManager의 Channel에 등록한다.
        internal void Initialize(ISessionOwner owner, IPacketHandler packetHandler, Socket socket, SocketAsyncEventArgs readArgs, SocketAsyncEventArgs writeArgs, Action<Session> releaseSession)
        {
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(packetHandler);
            ArgumentNullException.ThrowIfNull(socket);
            ArgumentNullException.ThrowIfNull(readArgs);
            ArgumentNullException.ThrowIfNull(writeArgs);
            ArgumentNullException.ThrowIfNull(releaseSession);

            lock (_lifetimeLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                // [세션 수명 수정] 이벤트 풀 반환과 owner 해제까지 끝난 객체만 새 연결에 사용할 수 있다.
                if (_owner != null || _released == 0)
                {
                    throw new InvalidOperationException("반환이 끝나지 않은 세션은 초기화할 수 없습니다.");
                }

                // 이벤트 준비가 실패하면 세션에 소유권을 넘기지 않는다.
                readArgs.SetBuffer(_recvBuffer.WriteMemory);
                writeArgs.BufferList = null;
                readArgs.Completed += IO_Completed;
                writeArgs.Completed += IO_Completed;
                readArgs.UserToken = this;
                writeArgs.UserToken = this;

                _owner = owner;
                _packetHandler = packetHandler;
                _sessionSocket = socket;
                _readArgs = readArgs;
                _writeArgs = writeArgs;
                _releaseSession = releaseSession;
                _started = false;
                _resetCompleted = false;
                _receivePending = 0;
                _sendPending = 0;
                _asyncJobPending = 0;
                _released = 0;
                _disconnectRequested = 0;
                _sendBufferBytes = 0;
                _connectedTimestamp = Stopwatch.GetTimestamp();
                _lastHeartbeatTimestamp = _connectedTimestamp;
                _pingTimestamp = 0;
                _pingSequence = 0;
                _waitingPong = false;
                _loginCompleted = false;
                _sessionID = Interlocked.Increment(ref _nextSessionId);
            }
        }

        public void StartSession()
        {
            lock (_lifetimeLock)
            {
                // [세션 수명 수정] StartSession을 다시 호출해도 Receive를 중복 등록하지 않는다.
                if (_disconnectRequested != 0 || _started)
                {
                    return;
                }

                _started = true;
                RegisterReceive();
            }
        }

        public bool IsCurrentConnection(long connectionId)
        {
            lock (_lifetimeLock)
            {
                return _sessionID == connectionId &&
                       Volatile.Read(ref _disconnectRequested) == 0;
            }
        }

        private void IO_Completed(object? sender, SocketAsyncEventArgs e)
        {
            lock (_lifetimeLock)
            {
                switch (e.LastOperation)
                {
                    case SocketAsyncOperation.Receive:
                        ProcessReceive(e);
                        break;
                    case SocketAsyncOperation.Send:
                        ProcessSend(e);
                        break;
                    default:
                        throw new ArgumentException("지원되지 않는 작업 유형");
                }
            }
        }

        private void RegisterReceive()
        {
            if (Volatile.Read(ref _disconnectRequested) != 0)
            {
                TryRelease();
                return;
            }

            if (Interlocked.Exchange(ref _receivePending, 1) != 0)
            {
                throw new InvalidOperationException("Receive 작업이 이미 등록되어 있습니다.");
            }

            // 종료 상태에서는 등록한 pending을 해제한다. 현재 호출 경로는 _lifetimeLock 안에서 실행된다.
            if (Volatile.Read(ref _disconnectRequested) != 0)
            {
                Volatile.Write(ref _receivePending, 0);

                TryRelease();
                return;
            }

            try
            {
                Socket socket = _sessionSocket ?? throw new InvalidOperationException("소켓이 초기화되지 않았습니다.");

                SocketAsyncEventArgs readArgs = ReadArgs;

                if (!socket.ReceiveAsync(readArgs))
                {
                    ProcessReceive(readArgs);
                }
            }
            catch
            {
                Volatile.Write(ref _receivePending, 0);
                Disconnect();
                // [세션 수명 수정] 이미 종료된 경우 Disconnect가 바로 반환하므로 반환 조건을 별도로 재검사한다.
                TryRelease();
            }
        }

        private void ProcessReceive(SocketAsyncEventArgs e)
        {
            bool receiveAgain = false;

            try
            {
                if (e.BytesTransferred == 0 || e.SocketError != SocketError.Success)
                {
                    Disconnect();
                    return;
                }

                if (_recvBuffer.OnWrite(e.BytesTransferred) == false)
                {
                    Disconnect();
                    Console.WriteLine("Recv : 0 이여서 disconnect 함");
                    return;
                }

                int processLen = OnRecv(_recvBuffer.ReadMemory);

                if (processLen < 0)
                {
                    Disconnect();
                    Console.WriteLine("processLen is 0");
                    return;
                }

                if (!_recvBuffer.OnRead(processLen))
                {
                    Disconnect();
                    Console.WriteLine("RecvBuffer read failed.");
                    return;
                }

                _recvBuffer.Clear();

                e.SetBuffer(_recvBuffer.WriteMemory);

                receiveAgain = true;
            }
            catch
            {
                Disconnect();
            }
            finally
            {
                // [세션 수명 수정] pending 해제부터 다음 Receive 등록까지 같은 수명 잠금 안에서 처리한다.
                Volatile.Write(ref _receivePending, 0);

                if (receiveAgain && Volatile.Read(ref _disconnectRequested) == 0)
                {
                    RegisterReceive();
                }
                else
                {
                    TryRelease();
                }
            }

        }

        // [세션 수명 수정] 큐 이동과 BufferList 준비도 보호한다. SendAsync의 pending 설정 전에도 Reset과 겹치면 안 된다.
        public void Send(SendBuffer data)
        {
            lock (_lifetimeLock)
            {
                bool registerSend = false;

                lock (_sendLock)
                {
                    if (Volatile.Read(ref _disconnectRequested) != 0)
                    {
                        data.Dispose();
                        return;
                    }

                    if (_sendBufferBytes + data.Buffer.Length > MaxSendBufferBytes)
                    {
                        data.Dispose();
                        Disconnect();
                        return;
                    }

                    _sendBuffer.Enqueue(data);

                    _sendBufferBytes += data.Buffer.Length;

                    if (_sendRegistered == false)
                    {
                        _sendRegistered = true;
                        registerSend = true;
                    }
                }

                if (registerSend)
                {
                    RegisterSend();
                }
            }
        }

        private void RegisterSend()
        {
            if(_writeArgs == null)
            {
                lock(_sendLock)
                {
                    _sendRegistered = false;
                }

                Console.WriteLine("송신 SocketAsyncEventArgs객체가 없습니다. 연결해제");
                Disconnect();
                return;
            }

            _pendingSendBuffers.Clear();


            lock(_sendLock)
            {
                while(_sendBuffer.Count > 0)
                {
                    SendBuffer sendBuffer = _sendBuffer.Dequeue();

                    _pendingSendBuffers.Add(sendBuffer);
                }
            }

            List<ArraySegment<byte>> bufferList = new List<ArraySegment<byte>>();

            foreach (SendBuffer sendBuffer in _pendingSendBuffers)
            {
                bufferList.Add(sendBuffer.ToArraySegment());
            }

            _writeArgs.BufferList = bufferList;

            RegisterSocketSend();
        }

        private void RegisterSocketSend()
        {
            if (Volatile.Read(ref _disconnectRequested) != 0)
            {
                TryRelease();
                return;
            }

            if (Interlocked.Exchange(ref _sendPending, 1) != 0)
            {
                throw new InvalidOperationException("Send 작업이 이미 등록되어 있습니다.");
            }

            try
            {
                Socket socket = _sessionSocket ?? throw new InvalidOperationException("소켓이 초기화되지 않았습니다.");

                SocketAsyncEventArgs writeArgs = WriteArgs;

                if (!socket.SendAsync(writeArgs))
                {
                    ProcessSend(writeArgs);
                }
            }
            catch
            {
                Volatile.Write(ref _sendPending, 0);
                Disconnect();
                // [세션 수명 수정] 이미 종료된 경우 Disconnect가 바로 반환하므로 반환 조건을 별도로 재검사한다.
                TryRelease();
            }
        }

        private void ProcessSend(SocketAsyncEventArgs e)
        {
            bool sendRemaining = false;
            bool sendNextBatch = false;

            try
            {
                if (e.SocketError != SocketError.Success || e.BytesTransferred <= 0)
                {
                    Disconnect();
                    return;
                }

                // 실제 전송된 바이트 수.
                int transferred = e.BytesTransferred;

                while (transferred > 0 && e.BufferList!.Count > 0)
                {
                    ArraySegment<byte> segment = e.BufferList[0];

                    if (transferred >= segment.Count)
                    {
                        transferred -= segment.Count;
                        e.BufferList.RemoveAt(0);

                        SendBuffer completed = _pendingSendBuffers[0];
                        _pendingSendBuffers.RemoveAt(0);

                        _sendBufferBytes -= completed.Buffer.Length;
                        completed.Dispose();
                    }
                    else
                    {
                        e.BufferList[0] = new ArraySegment<byte>(
                            segment.Array!,
                            segment.Offset + transferred,
                            segment.Count - transferred
                        );
                        transferred = 0;
                    }
                }

                if (e.BufferList!.Count > 0)
                {
                    // 변경된 버퍼 목록을 SocketAsyncEventArgs의 내부 송신 설정에 다시 반영한다.
                    e.BufferList = e.BufferList;
                    sendRemaining = true;
                    return;
                }

                e.BufferList = null;

                lock (_sendLock)
                {
                    sendNextBatch = _sendBuffer.Count > 0;

                    if (!sendNextBatch)
                    {
                        _sendRegistered = false;
                    }
                }
            }
            catch
            {
                Disconnect();
            }
            finally
            {
                // [세션 수명 수정] 남은 데이터나 다음 묶음을 등록할 때까지 수명 잠금을 유지한다.
                Volatile.Write(ref _sendPending, 0);

                if (Volatile.Read(ref _disconnectRequested) != 0)
                {
                    TryRelease();
                }
                else if (sendRemaining)
                {
                    RegisterSocketSend();
                }
                else if (sendNextBatch)
                {
                    RegisterSend();
                }
            }
        }

        private Int32 OnRecv(ReadOnlyMemory<byte> buffer)
        {
            Int32 porcessLen = 0;

            while (true)
            {
                Int32 dataSize = buffer.Length - porcessLen;

                if (dataSize < PacketHeader.HeaderSize)
                {
                    break;
                }

                ReadOnlySpan<byte> span = buffer.Span;

                ushort size = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Span.Slice(porcessLen, 2));
                ushort id = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Span.Slice(porcessLen + 2, 2));

                PacketHeader header = new PacketHeader(size, id);

                if (header.Size < PacketHeader.HeaderSize || header.Size > PacketHeader.MaxPacketSize)
                    return -1;

                if (dataSize < header.Size)
                    break;

                ReadOnlySpan<byte> packet = span.Slice(porcessLen, header.Size);

                if (!OnRecvPack(packet))
                {
                    Console.WriteLine($"HandlePacket ERROR - ID : {header.Id}");
                    return -1;
                }

                porcessLen += header.Size;
            }

            return porcessLen;
        }

        public void SendPacket<T>(ushort packetId, T message) where T : IMessage<T>
        {
            lock (_lifetimeLock)
            {
                if (_disconnectRequested != 0 || _packetHandler == null)
                {
                    return;
                }

                long sendStartTimestamp = Stopwatch.GetTimestamp();
                SendBuffer sendBuffer = _packetHandler.MakeSendBuffer(packetId, message);
                Send(sendBuffer);
                ServerPerformanceMetrics.MarkAckSendEnqueued(sendStartTimestamp);
            }
        }

        protected virtual bool OnRecvPack(ReadOnlySpan<byte> packet)
        {
            return _packetHandler?.HandlePacket(this, packet) ?? false;
        }

        public bool EnqueueAsyncJob(Func<ValueTask> job)
        {
            return EnqueueAsyncJob(_sessionID, job);
        }

        // [세션 수명 수정] 외부에서 보관한 Session 참조는 재사용될 수 있으므로 연결 ID 확인과 작업 등록을 함께 보호한다.
        public bool EnqueueAsyncJob(Int64 connectionId, Func<ValueTask> job)
        {
            lock (_lifetimeLock)
            {
                ArgumentNullException.ThrowIfNull(job);

                if (!IsCurrentConnection(connectionId))
                {
                    return false;
                }

                bool startProcess = false;

                // Race condition을 방지 하기 위해 Lock을 걸어둔다.
                lock (_recvQueueLock)
                {
                    if (Volatile.Read(ref _disconnectRequested) != 0)
                    {
                        return false;
                    }

                    if (_recvQueue.Count >= _maxRecvJobCount)
                    {
                        return false;
                    }

                    // 아직 실행하지 않은 비동기 작업 함수를 Queue에 등록한다.
                    _recvQueue.Enqueue(job);

                    // 현재 Queue를 처리하는 Drain 작업이 실행 중인지 확인한다.
                    if (!_recvQueueRunning)
                    {
                        // 실행 중인 Process 작업이 없다면
                        // 현재 호출자가 새로운 Process 작업을 시작하도록 표시한다.
                        _recvQueueRunning = true;

                        // Process를 실제 호출하기 전에 Pending 표시
                        Volatile.Write(ref _asyncJobPending, 1);

                        startProcess = true;
                    }
                }

                // 실행 중인 Process 작업이 없다면 Queue 처리를 시작한다.
                if (startProcess)
                {
                    // [세션 수명 수정] 사용자 작업은 I/O 콜백의 잠금 밖에서 실행한다.
                    // 작업자가 시작되기 전부터 _asyncJobPending이 1이므로 그 사이에도 세션은 반환되지 않는다.
                    _ = Task.Run(ProcessRecvQueueAsync);
                }

                return true;
            }
        }

        private async Task ProcessRecvQueueAsync()
        {
            while (true)
            {
                Func<ValueTask> job;

                lock (_lifetimeLock)
                {
                    lock (_recvQueueLock)
                    {
                        if (_recvQueue.Count == 0)
                        {
                            // [세션 수명 수정] 큐 종료 표시와 반환 판단 사이에 새 작업 등록이나 Reset이 끼어들지 않도록 한다.
                            _recvQueueRunning = false;
                            _asyncJobPending = 0;
                            TryRelease();
                            return;
                        }

                        job = _recvQueue.Dequeue();
                    }
                }

                try
                {
                    await job();
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"세션 비동기 작업 처리 실패: {exception}");
                }
            }
        }

        public virtual void Disconnect()
        {
            Disconnect(_sessionID);
        }

        // [세션 수명 수정] 이전 연결의 늦은 종료 요청이 같은 객체를 쓰는 새 연결을 닫지 않도록 한다.
        public void Disconnect(Int64 connectionId)
        {
            if (Volatile.Read(ref _disconnectRequested) != 0)
            {
                return;
            }

            lock (_lifetimeLock)
            {
                if (_sessionID != connectionId || _disconnectRequested != 0)
                {
                    return;
                }

                // [세션 수명 수정] 소켓 닫기와 종료가 끝날 때까지 반환을 막고, 같은 소켓에 Shutdown/Close를 수행한다.
                _disconnectRequested = 1;
                _disconnecting = true;
                Socket? socket = _sessionSocket;

                try
                {
                    try
                    {
                        socket?.Shutdown(SocketShutdown.Both);
                    }
                    catch (SocketException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                    }

                    socket?.Close();
                    OnDisconnected(connectionId);
                }
                finally
                {
                    _disconnecting = false;
                    TryRelease();
                }
            }
        }

        // 연결 종료 후 처리는 세션이 반환되기 전에 실행한다.
        protected virtual void OnDisconnected(Int64 connectionId)
        {
        }

        // [세션 수명 수정] _lifetimeLock 안에서 호출한다. 종료 처리와 모든 I/O·비동기 작업이 끝나야 반환을 예약한다.
        private void TryRelease()
        {
            if (_disconnecting || Volatile.Read(ref _disconnectRequested) == 0 ||
                Volatile.Read(ref _receivePending) != 0 ||
                Volatile.Read(ref _sendPending) != 0 ||
                Volatile.Read(ref _asyncJobPending) != 0)
            {
                return;
            }

            // [세션 수명 수정] 여러 완료 경로가 도착해도 Channel에는 한 번만 등록한다. 실제 Reset은 반환 작업자가 수행한다.
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;

            _releaseSession(this);
        }

        protected virtual void OnReset()
        {
        }

        // [세션 수명 수정] 이벤트 구독·버퍼·게임 상태만 정리한다. 이벤트 풀 반환에 필요한 owner는 여기서 지우지 않는다.
        public void Reset()
        {
            lock (_lifetimeLock)
            {
                if (_released == 0 || _disconnectRequested == 0 || _disconnecting ||
                    _receivePending != 0 || _sendPending != 0 || _asyncJobPending != 0)
                {
                    throw new InvalidOperationException("실행 중인 세션은 초기화할 수 없습니다.");
                }

                _sessionSocket = null;
                _recvBuffer.Reset();

                lock (_recvQueueLock)
                {
                    _recvQueue.Clear();
                    _recvQueueRunning = false;
                }

                lock (_sendLock)
                {
                    while (_sendBuffer.Count > 0)
                    {
                        _sendBuffer.Dequeue().Dispose();
                    }

                    foreach (SendBuffer sendBuffer in _pendingSendBuffers)
                    {
                        sendBuffer.Dispose();
                    }

                    _pendingSendBuffers.Clear();
                    _sendBufferBytes = 0;
                    _sendRegistered = false;
                }

                if(_readArgs is not null)
                {
                    _readArgs.Completed -= IO_Completed;
                    _readArgs.UserToken = null;
                    _readArgs.SetBuffer(Memory<byte>.Empty);
                }

                if (_writeArgs is not null)
                {
                    _writeArgs.Completed -= IO_Completed;
                    _writeArgs.UserToken = null;
                    _writeArgs.BufferList = null;
                }

                OnReset();
                _connectedTimestamp = 0;
                _lastHeartbeatTimestamp = 0;
                _pingTimestamp = 0;
                _pingSequence = 0;
                _waitingPong = false;
                _loginCompleted = false;
                _resetCompleted = true;
            }
        }

        public void RetrunEventArgs()
        {
            lock (_lifetimeLock)
            {
                if (!_resetCompleted || _owner == null || _readArgs == null || _writeArgs == null)
                {
                    throw new InvalidOperationException("Reset 이후에만 이벤트 객체를 반환할 수 있습니다.");
                }

                // [세션 수명 수정] 풀 반환이 성공한 다음 참조를 지운다. 이 메서드가 끝난 뒤 Manager가 세션을 공개한다.
                _owner.RetrunEventArg(_readArgs, _writeArgs);
                _readArgs = null;
                _writeArgs = null;
                _packetHandler = null;
                _owner = null;
            }
        }

        public void Dispose()
        {
            lock (_lifetimeLock)
            {
                if (_disposed)
                {
                    return;
                }

                // [세션 수명 수정] 이벤트 풀 반환과 owner 해제가 끝나기 전에는 버퍼를 폐기하지 않는다.
                if (_owner != null || _released == 0)
                {
                    throw new InvalidOperationException("Disconnect 후 세션 반환이 완료되어야 Dispose할 수 있습니다.");
                }

                _disposed = true;
                // [세션 수명 수정] 반환한 이벤트 객체는 풀의 소유이므로 Session.Dispose에서 다시 Dispose하지 않는다.
                _recvBuffer.Dispose();
            }
        }

        public bool CheckHeartbeat(Int64 currentSessionId)
        {
            lock (_lifetimeLock)
            {
                if (!IsCurrentConnection(currentSessionId))
                {
                    return false;
                }

                Int64 now = Stopwatch.GetTimestamp();
                if (!_loginCompleted)
                {
                    // 로그인 전에는 패킷을 받아도 접속 제한 시간을 연장하지 않는다.
                    if (Stopwatch.GetElapsedTime(_connectedTimestamp, now) >= LoginTimeout)
                    {
                        Disconnect(currentSessionId);
                        return false;
                    }

                    return true;
                }

                if (_waitingPong)
                {
                    if (Stopwatch.GetElapsedTime(_pingTimestamp, now) >= PongTimeout)
                    {
                        Disconnect(currentSessionId);
                        return false;
                    }

                    return true;
                }

                if (Stopwatch.GetElapsedTime(_lastHeartbeatTimestamp, now) >= PingInterval)
                {
                    _pingSequence++;
                    _pingTimestamp = now;
                    _waitingPong = true;
                    // 상태 설정과 송신을 같은 수명 잠금 안에서 수행한다.
                    SendHeartbeatPing(currentSessionId, _pingSequence);
                }

                return IsCurrentConnection(currentSessionId);
            }
        }

        public void MarkLoginCompleted(Int64 connectionId)
        {
            lock (_lifetimeLock)
            {
                if (!IsCurrentConnection(connectionId) || _loginCompleted)
                {
                    return;
                }

                // 검사 tick 사이에 늦게 끝난 로그인도 동일한 제한을 적용한다.
                if (Stopwatch.GetElapsedTime(_connectedTimestamp) >= LoginTimeout)
                {
                    Disconnect(connectionId);
                    return;
                }

                _loginCompleted = true;
                _lastHeartbeatTimestamp = Stopwatch.GetTimestamp();
            }
        }

        public void ReceiveHeartbeatPong(Int64 connectionId, Int64 sequence)
        {
            lock (_lifetimeLock)
            {
                if (!IsCurrentConnection(connectionId) || !_waitingPong || sequence != _pingSequence)
                {
                    return;
                }

                if (Stopwatch.GetElapsedTime(_pingTimestamp) >= PongTimeout)
                {
                    Disconnect(connectionId);
                    return;
                }

                _waitingPong = false;
                _lastHeartbeatTimestamp = Stopwatch.GetTimestamp();
            }
        }

        // 패킷 형식은 GameSession에서 정의한다. 라이브러리는 게임 프로토콜에 의존하지 않는다.
        protected virtual void SendHeartbeatPing(Int64 connectionId, Int64 sequence)
        {
        }
    }
}
