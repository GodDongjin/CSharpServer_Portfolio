using ServerCore.Interface;
using System;
using System.Net;
using System.Net.Sockets;

namespace ServerCore.Network
{
    public sealed class Listen
    {
        private Socket? _listenSocket;
        private readonly IPEndPoint _iPEndPoint;
        private readonly Service _serivce;
        private readonly SocketUtile _socketUtile = new SocketUtile();
        private readonly object _lock = new object();
        private bool _isStop;

        public Listen(IPEndPoint iPEndPoint, Service service)
        {
            _iPEndPoint = iPEndPoint;
            _serivce = service;
        }

        public void StartListen()
        {
            lock (_lock)
            {
                _isStop = false;
                _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                _listenSocket.Bind(_iPEndPoint);
                _listenSocket.Listen(backlog: 100);
            }

            StartAsyncListen(null);
            Console.WriteLine($"{_serivce.ToString()} 서버가 {_iPEndPoint.ToString()}에서 시작됨");
        }

        public void StartAsyncListen(SocketAsyncEventArgs? acceptEventArg)
        {
            // [세션 수명 수정] 동기 Accept 완료 시 반복문으로 재등록한다. 종료 후에는 재등록하지 않고 Accept 이벤트를 정리한다.
            while (true)
            {
                bool pending;
                lock (_lock)
                {
                    if (_isStop || _listenSocket == null)
                    {
                        acceptEventArg?.Dispose();
                        return;
                    }

                    if (acceptEventArg == null)
                    {
                        acceptEventArg = new SocketAsyncEventArgs();
                        acceptEventArg.Completed += OnAcceptCompleted;
                    }

                    acceptEventArg.AcceptSocket = null;
                    try
                    {
                        pending = _listenSocket.AcceptAsync(acceptEventArg);
                    }
                    catch
                    {
                        acceptEventArg.Dispose();
                        throw;
                    }
                }

                if (pending)
                {
                    return;
                }

                ProcessAccept(acceptEventArg);
            }
        }

        private void OnAcceptCompleted(object? sender, SocketAsyncEventArgs e)
        {
            ProcessAccept(e);
            StartAsyncListen(e);
        }

        // [세션 수명 수정] 종료 여부 확인뿐 아니라 세션 등록·시작까지 StopAsyncListen과 같은 잠금으로 보호한다.
        private void ProcessAccept(SocketAsyncEventArgs e)
        {
            lock (_lock)
            {
                if (_isStop || e.SocketError != SocketError.Success)
                {
                    e.AcceptSocket?.Close();
                    e.AcceptSocket = null;
                    return;
                }

                SocketAsyncEventArgs? readEventArgs = null;
                SocketAsyncEventArgs? writeEventArgs = null;
                Session? session = null;
                try
                {
                    _socketUtile.SocketNagle(e.AcceptSocket, true);
                    _socketUtile.SocketKeepAlive(e.AcceptSocket, true);
                    readEventArgs = _serivce.ReadPool.Pop();
                    writeEventArgs = _serivce.WritePool.Pop();

                    if (readEventArgs == null || writeEventArgs == null)
                    {
                        Console.WriteLine("서버가 최대 용량에 도달했습니다. 연결 거부됨.");
                        return;
                    }

                    // StopAsyncListen이 반환된 뒤에는 생성 중인 세션도 남아 있지 않는다.
                    session = SessionManager.CreateSession(e.AcceptSocket, readEventArgs, writeEventArgs, _serivce, _serivce.PacketHandler);
                    if (session == null)
                    {
                        Console.WriteLine("Session 생성 실패. 연결 거부됨");
                        return;
                    }

                    Console.WriteLine($"{session._sessionID} 유저 생성 완료!");
                    session.StartSession();
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"연결 수락 실패: {exception}");
                    session?.Disconnect();
                }
                finally
                {
                    // [세션 수명 수정] 세션 생성 전 실패한 자원만 여기서 반환한다. 생성된 세션의 자원은 Manager가 반환한다.
                    if (session == null)
                    {
                        if (readEventArgs != null)
                        {
                            _serivce.ReadPool.Push(readEventArgs);
                        }

                        if (writeEventArgs != null)
                        {
                            _serivce.WritePool.Push(writeEventArgs);
                        }

                        e.AcceptSocket?.Close();
                    }

                    e.AcceptSocket = null;
                }
            }
        }

        public void StopAsyncListen()
        {
            Socket? socketToClose;
            lock (_lock)
            {
                if (_isStop)
                {
                    return;
                }

                _isStop = true;
                socketToClose = _listenSocket;
                _listenSocket = null;
            }

            socketToClose?.Close();
            Console.WriteLine("서버 연결 수락 종료");
        }
    }
}
