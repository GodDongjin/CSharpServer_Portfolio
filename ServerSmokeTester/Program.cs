using System;
using System.Buffers.Binary;
using System.Net.Sockets;
using CSharp.Protocol;
using Google.Protobuf;

namespace ServerSmokeTester
{
    internal static class Program
    {
        private static async Task Main(string[] args)
        {
            if (!args.Contains("--smoke"))
            {
                Console.WriteLine("Start local services and GameServer, then use --smoke. Target: 127.0.0.1:7777");
                return;
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var first = await Client.Connect(timeout.Token);
                using var second = await Client.Connect(timeout.Token);
                await first.Login("demo-a");
                await second.Login("demo-b");
                await first.Send(PacketIds.HeroList, new REQ_HERO_LIST());
                var heroes = await first.Receive(PacketIds.HeroListReply, ACK_HERO_LIST.Parser);
                Check(heroes.Result == RESULT_CODE.Ok && heroes.Heroes.Count > 0, "Hero list");
                var hero = heroes.Heroes[0];
                try
                {
                    await first.Send(PacketIds.HeroUpdate, new REQ_HERO_UPDATE { HeroId = hero.Id, Field = HERO_FIELD.Pinned, Value = !hero.IsPinned });
                    var updated = await first.Receive(PacketIds.HeroUpdateReply, ACK_HERO_UPDATE.Parser);
                    Check(updated.Result == RESULT_CODE.Ok && updated.Hero.IsPinned == !hero.IsPinned, "Hero update");
                }
                finally
                {
                    await first.Send(PacketIds.HeroUpdate, new REQ_HERO_UPDATE { HeroId = hero.Id, Field = HERO_FIELD.Pinned, Value = hero.IsPinned });
                    Check((await first.Receive(PacketIds.HeroUpdateReply, ACK_HERO_UPDATE.Parser)).Result == RESULT_CODE.Ok, "Hero restore");
                }

                await first.Send(PacketIds.MatchJoin, new REQ_MATCH_JOIN());
                Check((await first.Receive(PacketIds.MatchJoinReply, ACK_MATCH.Parser)).State == MATCH_STATE.Pending, "Join");
                await first.Send(PacketIds.MatchCancel, new REQ_MATCH_CANCEL());
                Check((await first.Receive(PacketIds.MatchCancelReply, ACK_MATCH.Parser)).Result == RESULT_CODE.Ok, "Cancel");
                await first.Send(PacketIds.MatchJoin, new REQ_MATCH_JOIN());
                Check((await first.Receive(PacketIds.MatchJoinReply, ACK_MATCH.Parser)).Result == RESULT_CODE.Ok, "Rejoin");
                await second.Send(PacketIds.MatchJoin, new REQ_MATCH_JOIN());
                Check((await second.Receive(PacketIds.MatchJoinReply, ACK_MATCH.Parser)).Result == RESULT_CODE.Ok, "Second join");
                var a = await first.Receive(PacketIds.MatchNotice, ACK_MATCH.Parser);
                var b = await second.Receive(PacketIds.MatchNotice, ACK_MATCH.Parser);
                Check(a.State == MATCH_STATE.Matched && b.State == MATCH_STATE.Matched && a.MatchId == b.MatchId, "Match pair");
                first.Dispose();
                Check((await second.Receive(PacketIds.MatchNotice, ACK_MATCH.Parser)).State == MATCH_STATE.Closed, "Opponent disconnect");
                Console.WriteLine("PASS: login, Hero update/restore, match cancel/pair/disconnect.");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL: {exception.Message}");
                Environment.ExitCode = 1;
            }
        }

        private static void Check(bool condition, string step)
        {
            if (!condition)
            {
                throw new InvalidDataException(step);
            }
        }
    }

    sealed class Client : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly CancellationToken _token;
        private readonly List<(UInt16 Id, Byte[] Payload)> _inbox = [];
        public Client(TcpClient tcp, CancellationToken token)
        {
            _tcp = tcp;
            _token = token;
        }

        public static async Task<Client> Connect(CancellationToken token)
        {
            var tcp = new TcpClient();
            try
            {
                await tcp.ConnectAsync("127.0.0.1", 7777, token);
                return new(tcp, token);
            }
            catch
            {
                tcp.Dispose();
                throw;
            }
        }

        public async Task Login(string id)
        {
            await Send(PacketIds.Login, new REQ_LOGIN { Id = id });
            if ((await Receive(PacketIds.LoginReply, ACK_LOGIN.Parser)).Result != LOGIN_ERROR.LoginSuccess)
            {
                throw new InvalidDataException($"Login failed: {id}");
            }
        }

        public async Task Send(UInt16 id, IMessage message)
        {
            var frame = new Byte[checked(message.CalculateSize() + 4)];
            BinaryPrimitives.WriteUInt16LittleEndian(frame, checked((UInt16)frame.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), id);
            message.WriteTo(frame.AsSpan(4));
            await _tcp.GetStream().WriteAsync(frame, _token);
        }

        public async Task<T> Receive<T>(UInt16 id, MessageParser<T> parser)
            where T : IMessage<T>
        {
            while (true)
            {
                Int32 index = _inbox.FindIndex(packet => packet.Id == id);
                if (index >= 0)
                {
                    var packet = _inbox[index];
                    _inbox.RemoveAt(index);
                    return parser.ParseFrom(packet.Payload);
                }

                var header = new Byte[4];
                await _tcp.GetStream().ReadExactlyAsync(header, _token);
                Int32 size = BinaryPrimitives.ReadUInt16LittleEndian(header);
                if (size < 4)
                {
                    throw new InvalidDataException("Invalid frame size");
                }

                var payload = new Byte[size - 4];
                await _tcp.GetStream().ReadExactlyAsync(payload, _token);
                UInt16 receivedId = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
                if (receivedId == PacketIds.HeartbeatPing)
                {
                    SEND_HEARTBEAT_PING ping = SEND_HEARTBEAT_PING.Parser.ParseFrom(payload);
                    await Send(PacketIds.HeartbeatPong, new REQ_HEARTBEAT_PONG { Sequence = ping.Sequence });
                    continue;
                }

                _inbox.Add((receivedId, payload));
                if (_inbox.Count > 100)
                {
                    throw new InvalidDataException("Too many unexpected packets");
                }
            }
        }

        public void Dispose()
        {
            _tcp.Dispose();
        }
    }
}
