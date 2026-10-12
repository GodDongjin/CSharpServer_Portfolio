using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using CSharp.Protocol;
using GameServer.DataBase;
using GameServer.Matchmaking;
using GameServer.Packet;
using Google.Protobuf;
using ServerCore.Interface;
using ServerCore.Network;

namespace GameServer.Tests
{
    public class ProtocolTests
    {
        private static void AdvanceHeartbeat(Session session, Int32 seconds)
        {
            // 테스트에서만 기록 시점을 변경한다. 실제 서버는 Stopwatch를 직접 사용한다.
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Object lifetimeLock = typeof(Session).GetField("_lifetimeLock", flags)!.GetValue(session)!;
            lock (lifetimeLock)
            {
                foreach (String name in new[] { "_connectedTimestamp", "_lastHeartbeatTimestamp", "_pingTimestamp" })
                {
                    FieldInfo field = typeof(Session).GetField(name, flags)!;
                    field.SetValue(session, (Int64)field.GetValue(session)! - seconds * Stopwatch.Frequency);
                }
            }
        }

        [Fact]
        public async Task Pong_is_dispatched_while_game_job_is_blocked_and_queue_is_full()
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            using TcpClient client = new();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, deadline.Token);
            using Socket accepted = await listener.AcceptSocketAsync(deadline.Token);
            Repository repo = new();
            await using MatchManager matches = new(new Store());
            GSPacketHandler handler = new(new GSPacket(new DataBaseRepository(repo, repo), matches));
            handler.Initialize();
            Owner owner = new();
            using GameSession session = new(matches, new UserSessions());
            session.Initialize(owner, handler, accepted, new SocketAsyncEventArgs(), new SocketAsyncEventArgs(), owner.ReleaseSession);
            Int64 id = session.ConnectionId;
            TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                session.MarkLoginCompleted(id);
                Assert.True(session.EnqueueAsyncJob(async () =>
                {
                    started.TrySetResult();
                    await finish.Task;
                }));
                await started.Task.WaitAsync(deadline.Token);
                for (Int32 i = 0; i < 30; i++)
                {
                    Assert.True(session.EnqueueAsyncJob(() => ValueTask.CompletedTask));
                }

                AdvanceHeartbeat(session, 10);
                Assert.True(session.CheckHeartbeat(id));
                Byte[] header = new Byte[4];
                await client.GetStream().ReadExactlyAsync(header, deadline.Token);
                Assert.Equal(PacketIds.HeartbeatPing, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)));
                Byte[] payload = new Byte[BinaryPrimitives.ReadUInt16LittleEndian(header) - 4];
                await client.GetStream().ReadExactlyAsync(payload, deadline.Token);
                SEND_HEARTBEAT_PING ping = SEND_HEARTBEAT_PING.Parser.ParseFrom(payload);
                Assert.Equal(1L, ping.Sequence);

                REQ_HEARTBEAT_PONG pong = new() { Sequence = ping.Sequence };
                Byte[] frame = new Byte[pong.CalculateSize() + 4];
                BinaryPrimitives.WriteUInt16LittleEndian(frame, (UInt16)frame.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), PacketIds.HeartbeatPong);
                pong.WriteTo(frame.AsSpan(4));
                AdvanceHeartbeat(session, 29);
                Assert.True(handler.HandlePacket(session, frame));
                AdvanceHeartbeat(session, 1);
                Assert.True(session.CheckHeartbeat(id));
                Assert.False(finish.Task.IsCompleted);
                Assert.True(session.IsCurrentConnection(id));
            }
            finally
            {
                session.Disconnect(id);
                finish.TrySetResult();
                await owner.Released.Task.WaitAsync(deadline.Token);
            }
        }

        [Fact]
        public async Task Malformed_pong_is_rejected()
        {
            Repository repo = new();
            await using MatchManager matches = new(new Store());
            GSPacketHandler handler = new(new GSPacket(new DataBaseRepository(repo, repo), matches));
            handler.Initialize();
            using GameSession session = new(matches, new UserSessions());
            Byte[] frame = { 5, 0, 0, 0, 0x80 };
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), PacketIds.HeartbeatPong);
            Assert.False(handler.HandlePacket(session, frame));
        }

        private sealed class Repository : ILoginDB, IHeroDB
        {
            public bool Fail;
            public DBHero Row = new(101, 1, 1, false, false);
            public Task<DBUser?> FindAsync(string id)
            {
                return Task.FromResult<DBUser?>(id == "demo-a" ? new(1, id, "Demo", 1000) : null);
            }

            public Task<IReadOnlyList<DBHero>> LoadAsync(Int32 id)
            {
                return Task.FromResult<IReadOnlyList<DBHero>>([Row]);
            }

            public Task<DBHero?> UpdateAsync(Int32 user, Int64 hero, HERO_FIELD field, bool value)
            {
                if (Fail)
                {
                    throw new IOException("Simulated lost DB acknowledgement");
                }

                Row = field == HERO_FIELD.Pinned ? Row with
                {
                    IsPinned = value
                }

                : Row with
                {
                    IsLocked = value
                };
                return Task.FromResult<DBHero?>(Row);
            }
        }

        private sealed class Store : IMatchMakingStore
        {
            public Task<bool> AddAsync(MatchTicket ticket)
            {
                return Task.FromResult(true);
            }

            public Task RemoveAsync(MatchTicket ticket)
            {
                return Task.CompletedTask;
            }

            public Task<bool> ClaimAsync(MatchTicket first, MatchTicket second)
            {
                return Task.FromResult(true);
            }
        }

        private sealed class Owner : ISessionOwner
        {
            public TaskCompletionSource Released = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public void ReleaseSession(Session session)
            {
                session.Reset();
                session.RetrunEventArgs();
                Released.TrySetResult();
            }

            public void RetrunEventArg(SocketAsyncEventArgs read_args, SocketAsyncEventArgs write_args)
            {
                read_args.Dispose();
                write_args.Dispose();
            }
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task RealSocketLoginHeroUpdateAndReset(bool databaseFails)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var token = deadline.Token;
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, token);
            using var accepted = await listener.AcceptSocketAsync(token);
            var repo = new Repository();
            var registry = new UserSessions();
            await using var matches = new MatchManager(new Store());
            GSPacket packet = new GSPacket(new DataBaseRepository(repo, repo), matches);
            GSPacketHandler handler = new GSPacketHandler(packet);
            handler.Initialize();
            var owner = new Owner();
            using var session = new GameSession(matches, registry);
            session.Initialize(owner, handler, accepted, new SocketAsyncEventArgs(), new SocketAsyncEventArgs(), owner.ReleaseSession);
            session.StartSession();
            var stream = client.GetStream();
            async Task Send(UInt16 id, IMessage message)
            {
                var data = new Byte[message.CalculateSize() + 4];
                BinaryPrimitives.WriteUInt16LittleEndian(data, (UInt16)data.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), id);
                message.WriteTo(data.AsSpan(4));
                await stream.WriteAsync(data, token);
            }

            async Task<T> Read<T>(UInt16 id, MessageParser<T> parser)
                where T : IMessage<T>
            {
                var header = new Byte[4];
                await stream.ReadExactlyAsync(header, token);
                Assert.Equal(id, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)));
                var payload = new Byte[BinaryPrimitives.ReadUInt16LittleEndian(header) - 4];
                await stream.ReadExactlyAsync(payload, token);
                return parser.ParseFrom(payload);
            }

            try
            {
                await Send(PacketIds.HeroList, new REQ_HERO_LIST());
                Assert.Equal(RESULT_CODE.NotLoggedIn, (await Read(PacketIds.HeroListReply, ACK_HERO_LIST.Parser)).Result);
                await Send(PacketIds.Login, new REQ_LOGIN { Id = "unknown-user" });
                Assert.Equal(LOGIN_ERROR.LoginFail, (await Read(PacketIds.LoginReply, ACK_LOGIN.Parser)).Result);
                await Send(PacketIds.Login, new REQ_LOGIN { Id = "demo-a" });
                Assert.Equal(LOGIN_ERROR.LoginSuccess, (await Read(PacketIds.LoginReply, ACK_LOGIN.Parser)).Result);
                await Send(PacketIds.HeroList, new REQ_HERO_LIST());
                Assert.Equal(101, Assert.Single((await Read(PacketIds.HeroListReply, ACK_HERO_LIST.Parser)).Heroes).Id);
                // 오류 ACK도 처리 완료(true)이므로 동일 연결에서 다음 요청을 처리할 수 있어야 한다.
                await Send(PacketIds.HeroUpdate, new REQ_HERO_UPDATE { HeroId = 999, Field = HERO_FIELD.Locked, Value = true });
                Assert.Equal(RESULT_CODE.NotFound, (await Read(PacketIds.HeroUpdateReply, ACK_HERO_UPDATE.Parser)).Result);
                repo.Fail = databaseFails;
                await Send(PacketIds.HeroUpdate, new REQ_HERO_UPDATE { HeroId = 101, Field = HERO_FIELD.Locked, Value = true });
                if (databaseFails)
                {
                    await owner.Released.Task.WaitAsync(token);
                }
                else
                {
                    var update = await Read(PacketIds.HeroUpdateReply, ACK_HERO_UPDATE.Parser);
                    Assert.Equal(RESULT_CODE.Ok, update.Result);
                    Assert.True(update.Hero.IsLocked);
                    await Send(PacketIds.HeroList, new REQ_HERO_LIST());
                    Assert.True(Assert.Single((await Read(PacketIds.HeroListReply, ACK_HERO_LIST.Parser)).Heroes).IsLocked);

                    await Send(PacketIds.MatchJoin, new REQ_MATCH_JOIN());
                    Assert.Equal(MATCH_STATE.Pending, (await Read(PacketIds.MatchJoinReply, ACK_MATCH.Parser)).State);
                    await Send(PacketIds.MatchCancel, new REQ_MATCH_CANCEL());
                    Assert.Equal(RESULT_CODE.Ok, (await Read(PacketIds.MatchCancelReply, ACK_MATCH.Parser)).Result);
                    await Send(PacketIds.MatchLeave, new REQ_MATCH_LEAVE());
                    Assert.Equal(MATCH_STATE.Closed, (await Read(PacketIds.MatchLeaveReply, ACK_MATCH.Parser)).State);
                }
            }
            finally
            {
                session.Disconnect();
                await owner.Released.Task.WaitAsync(token);
            }

            Assert.Null(session.User);
            Assert.Empty(session.Heroes.Values);
            Assert.True(registry.TryLogin(1, 999));
        }

        [Fact]
        public async Task Hero_failure_returns_false_without_disconnecting_inside_game_logic()
        {
            Repository repo = new Repository { Fail = true };
            await using MatchManager matches = new MatchManager(new Store());
            GSPacket packet = new GSPacket(new DataBaseRepository(repo, repo), matches);
            GSPacketHandler handler = new GSPacketHandler(packet);
            handler.Initialize();
            Owner owner = new Owner();
            using GameSession session = new GameSession(matches, new UserSessions());
            session.Initialize(owner, handler, new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp),
                new SocketAsyncEventArgs(), new SocketAsyncEventArgs(), owner.ReleaseSession);
            Int64 connectionId = session.ConnectionId;
            try
            {
                Assert.True(session.Login(new DBUser(1, "demo-a", "Demo", 1000)));
                session.Heroes.Load(new[] { repo.Row });

                Boolean result = await packet.HandleReqHeroUpdate(session,
                    new REQ_HERO_UPDATE { HeroId = 101, Field = HERO_FIELD.Locked, Value = true });

                Assert.False(result);
                Assert.True(session.IsCurrentConnection(connectionId));
                Assert.False(owner.Released.Task.IsCompleted);
            }
            finally
            {
                session.Disconnect(connectionId);
                await owner.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(65535)]
        public async Task UnsupportedPacketRejected(Int32 id)
        {
            var repo = new Repository();
            await using var matches = new MatchManager(new Store());
            GSPacket packet = new GSPacket(new DataBaseRepository(repo, repo), matches);
            GSPacketHandler handler = new GSPacketHandler(packet);
            handler.Initialize();
            using var session = new GameSession(matches, new UserSessions());
            var frame = new Byte[4];
            BinaryPrimitives.WriteUInt16LittleEndian(frame, 4);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), (UInt16)id);
            Assert.False(handler.HandlePacket(session, frame));
        }

        [Fact]
        public async Task Old_match_notification_cannot_disconnect_reused_session_with_full_queue()
        {
            Repository repo = new Repository();
            UserSessions users = new UserSessions();
            await using MatchManager matches = new MatchManager(new Store());
            GSPacketHandler handler = new GSPacketHandler(new GSPacket(new DataBaseRepository(repo, repo), matches));
            handler.Initialize();
            using GameSession session = new GameSession(matches, users);
            Owner oldOwner = new Owner();
            session.Initialize(oldOwner, handler, new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp),
                new SocketAsyncEventArgs(), new SocketAsyncEventArgs(), oldOwner.ReleaseSession);
            Assert.True(session.Login(new DBUser(1, "demo-a", "Demo", 1000)));
            Int64 oldId = session.ConnectionId;
            session.Disconnect();
            await oldOwner.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Owner owner = new Owner();
            session.Initialize(owner, handler, new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp),
                new SocketAsyncEventArgs(), new SocketAsyncEventArgs(), owner.ReleaseSession);
            Assert.True(session.Login(new DBUser(1, "demo-a", "Demo", 1000)));
            Int64 currentId = session.ConnectionId;
            TaskCompletionSource started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                Assert.True(session.EnqueueAsyncJob(async () =>
                {
                    started.TrySetResult();
                    await gate.Task;
                }));
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                for (Int32 i = 0; i < 30; i++)
                {
                    Assert.True(session.EnqueueAsyncJob(() => ValueTask.CompletedTask));
                }

                session.Notify(oldId, new ACK_MATCH { State = MATCH_STATE.Closed });
                Assert.True(session.IsCurrent(currentId));
                Assert.False(owner.Released.Task.IsCompleted);
                Assert.NotNull(session.User);
            }
            finally
            {
                session.Disconnect();
                gate.TrySetResult();
                await owner.Released.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [Fact]
        public async Task RemovedRequestsAreNotRegistered()
        {
            Repository repo = new Repository();
            await using MatchManager matches = new MatchManager(new Store());
            GSPacketHandler handler = new GSPacketHandler(new GSPacket(new DataBaseRepository(repo, repo), matches));
            handler.Initialize();
            using GameSession session = new GameSession(matches, new UserSessions());

            UInt16[] removedIds = { 1000, 1004, 1008, 1012, 1014, 1016, 1025, 1028, 1031, 1034, 1036, 1038, 1040 };
            foreach (UInt16 id in removedIds)
            {
                Byte[] frame = new Byte[4];
                BinaryPrimitives.WriteUInt16LittleEndian(frame, 4);
                BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), id);
                Assert.False(handler.HandlePacket(session, frame));
            }
        }

        [Fact]
        public async Task DuplicateInitializationIsRejected()
        {
            Repository repo = new Repository();
            await using MatchManager matches = new MatchManager(new Store());
            GSPacketHandler handler = new GSPacketHandler(new GSPacket(new DataBaseRepository(repo, repo), matches));
            handler.Initialize();

            Assert.Throws<InvalidOperationException>(() => handler.Initialize());
        }

        [Fact]
        public async Task MalformedProtobufIsRejectedBeforeQueueing()
        {
            Repository repo = new Repository();
            await using MatchManager matches = new MatchManager(new Store());
            GSPacketHandler handler = new GSPacketHandler(new GSPacket(new DataBaseRepository(repo, repo), matches));
            handler.Initialize();
            using GameSession session = new GameSession(matches, new UserSessions());

            Byte[] frame = { 5, 0, 0, 0, 0x80 };
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), PacketIds.Login);
            Assert.False(handler.HandlePacket(session, frame));
        }
    }
}
