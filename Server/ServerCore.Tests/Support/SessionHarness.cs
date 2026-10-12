using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using ServerCore.Interface;
using ServerCore.Network;
using ServerCore.Packet;

namespace ServerCore.Tests.Support
{
    // Owns a real Session, but avoids process-wide SessionManager state and external services.
    internal sealed class SessionHarness : IAsyncDisposable
    {
        internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
        private readonly SocketAsyncEventArgs _readArgs = new();
        private readonly SocketAsyncEventArgs _writeArgs = new();
        public Session Session { get; } = new();
        public RecordingHandler Handler { get; } = new();
        public RecordingOwner Owner { get; private set; } = new();
        public Socket? Peer { get; private set; }

        private SessionHarness(Socket socket, Socket? peer = null)
        {
            Peer = peer;
            Session.Initialize(Owner, Handler, socket, _readArgs, _writeArgs, Owner.ReleaseSession);
        }

        internal static TaskCompletionSource Signal()
        {
            return new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        // Queue/lifecycle tests don't start I/O and therefore don't need a listening socket.
        public static SessionHarness ForQueue()
        {
            return new(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp));
        }

        public static async Task<SessionHarness> ConnectAsync()
        {
            using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(1);
            var peer = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            Socket? accepted = null;
            try
            {
                using var deadline = new CancellationTokenSource(Timeout);
                await peer.ConnectAsync(listener.LocalEndPoint!, deadline.Token);
                accepted = await listener.AcceptAsync(deadline.Token);
                var harness = new SessionHarness(accepted, peer);
                harness.Session.StartSession();
                return harness;
            }
            catch
            {
                accepted?.Dispose();
                peer.Dispose();
                throw;
            }
        }

        public void ReinitializeQueueSession()
        {
            if (!Owner.Released.Task.IsCompletedSuccessfully || Peer is not null)
            {
                throw new InvalidOperationException("Reuse requires a fully released queue-only session.");
            }

            Owner = new RecordingOwner();
            Session.Initialize(Owner, Handler, new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp), _readArgs, _writeArgs, Owner.ReleaseSession);
        }

        public async Task SendFromPeerAsync(ReadOnlyMemory<Byte> bytes)
        {
            using var deadline = new CancellationTokenSource(Timeout);
            while (!bytes.IsEmpty)
            {
                Int32 sent = await Peer!.SendAsync(bytes, SocketFlags.None, deadline.Token);
                if (sent == 0)
                {
                    throw new IOException("Peer stopped sending.");
                }

                bytes = bytes[sent..];
            }
        }

        public async Task<Byte[]> ReceiveAtPeerAsync(Int32 length)
        {
            var bytes = new Byte[length];
            Int32 received = 0;
            using var deadline = new CancellationTokenSource(Timeout);
            while (received < length)
            {
                Int32 count = await Peer!.ReceiveAsync(bytes.AsMemory(received), SocketFlags.None, deadline.Token);
                if (count == 0)
                {
                    throw new EndOfStreamException("Session closed before all bytes arrived.");
                }

                received += count;
            }

            return bytes;
        }

        public async ValueTask DisposeAsync()
        {
            Session.Disconnect();
            try
            {
                await Owner.Released.Task.WaitAsync(Timeout);
            }
            finally
            {
                Peer?.Dispose();
                Session.Dispose();
                _readArgs.Dispose();
                _writeArgs.Dispose();
            }
        }
    }

    internal sealed class RecordingOwner : ISessionOwner
    {
        private Int32 _releaseCount;
        public Int32 ReleaseCount
        {
            get
            {
                return Volatile.Read(ref _releaseCount);
            }
        }

        public TaskCompletionSource Released { get; } = SessionHarness.Signal();

        public void ReleaseSession(Session session)
        {
            session.Reset();
            session.RetrunEventArgs();
            Released.TrySetResult();
        }

        public void RetrunEventArg(SocketAsyncEventArgs read_args, SocketAsyncEventArgs write_args)
        {
            Interlocked.Increment(ref _releaseCount);
        }
    }

    internal sealed class RecordingHandler : PacketHandler
    {
        private readonly Channel<Byte[]> _packets = Channel.CreateUnbounded<Byte[]>();
        public bool AcceptPackets { get; set; } = true;

        public bool HasPacket
        {
            get
            {
                return _packets.Reader.TryPeek(out _);
            }
        }

        public override void Initialize()
        {
        }

        public override bool HandlePacket(Session session, ReadOnlySpan<Byte> data)
        {
            _packets.Writer.TryWrite(data.ToArray());
            return AcceptPackets;
        }

        public async Task<Byte[]> NextAsync()
        {
            return await _packets.Reader.ReadAsync().AsTask().WaitAsync(SessionHarness.Timeout);
        }
    }
}
