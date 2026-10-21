using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

using ServerCore.Interface;
using ServerCore.Packet;

namespace ServerCore.Network
{

    public class Service : ISessionOwner
    {
        private readonly PacketHandler? _packetHandler;

        private Listen? _listen;
        private readonly object _stopLock = new object();
        private Task? _stopTask;
        private readonly CancellationTokenSource _heartbeatStop = new CancellationTokenSource();
        public Task HeartbeatTask { get; private set; } = Task.CompletedTask;

        private readonly SocketAsyncEventArgsPool _readPool;
        private readonly SocketAsyncEventArgsPool _writePool;

        private readonly Int32 _maxConnection;
        private readonly SERVICE_TYPE _serviceType;

        public PacketHandler? PacketHandler { get { return _packetHandler; } }
        public SocketAsyncEventArgsPool ReadPool { get { return _readPool; } }
        public SocketAsyncEventArgsPool WritePool { get { return _writePool; } }
        public Listen? Listen { get { return _listen; }  set { _listen = value; } }

        public Service(SERVICE_TYPE serviceType, PacketHandler packetHandler, Int32 maxConnection)
        {
            _readPool = new SocketAsyncEventArgsPool(maxConnection);
            _writePool = new SocketAsyncEventArgsPool(maxConnection);

            for (int i = 0; i < maxConnection; i++)
            {
                _readPool.Push(new SocketAsyncEventArgs());
                _writePool.Push(new SocketAsyncEventArgs());
            }

            _serviceType = serviceType;

            _packetHandler = packetHandler;
            _maxConnection = maxConnection;
        }

        public void StartServer(IPAddress ipAddress, Int16 port)
        {
            IPEndPoint iPEndPoint = new IPEndPoint(ipAddress, port);
            _listen = new Listen(iPEndPoint, this);

            _listen.StartListen();
            HeartbeatTask = new SocketHeartBeat().RunAsync(_heartbeatStop.Token);
        }

        public Task StopServerAsync()
        {
            lock (_stopLock)
            {
                // 여러 번 호출해도 연결 중단과 풀 해제를 중복 실행하지 않는다.
                if (_stopTask == null)
                {
                    _stopTask = Task.Run(StopServerCoreAsync);
                }

                return _stopTask;
            }
        }

        private async Task StopServerCoreAsync()
        {
            Listen?.StopAsyncListen();
            _heartbeatStop.Cancel();
            try
            {
                await HeartbeatTask;
            }
            finally
            {
                // 검사 작업을 끝낸 뒤 세션을 정리한다. DB 작업의 취소 토큰과는 별개다.
                _heartbeatStop.Dispose();
                await SessionManager.AllReleaseSessionAsync();
            }

            // 세션과 반환 작업자의 종료가 확인된 뒤에만 이벤트 풀을 해제한다.
            _readPool.Dispose();
            _writePool.Dispose();
        }

        public void RetrunEventArg(SocketAsyncEventArgs read_args, SocketAsyncEventArgs write_args)
        {
            if(read_args is null || write_args is null)
            {
                return;
            }

            _readPool.Push(read_args);
            _writePool.Push(write_args);

            return;
        }

    }
}
