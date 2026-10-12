using System;
using System.Collections.Concurrent;

using CSharp.Protocol;
using GameServer.DataBase;

namespace GameServer.Matchmaking
{
    public interface IMatchPeer
    {
        Int64 ConnectionId { get; }
        bool IsCurrent(Int64 connectionId);
        void Notify(Int64 connectionId, ACK_MATCH reply);
    }

    public sealed record MatchTicket
    {
        public Int32 UserId { get; init; }
        public Int64 ConnectionId { get; init; }
        public string Token { get; init; }
        public Int32 Score { get; init; }
        public Int64 CreatedAt { get; init; }

        public MatchTicket(Int32 userId, Int64 connectionId, string token, Int32 score, Int64 createdAt)
        {
            UserId = userId;
            ConnectionId = connectionId;
            Token = token;
            Score = score;
            CreatedAt = createdAt;
        }
    }

    public interface IMatchMakingStore
    {
        Task<bool> AddAsync(MatchTicket ticket);
        Task RemoveAsync(MatchTicket ticket);
        Task<bool> ClaimAsync(MatchTicket first, MatchTicket second);
    }

    // 매칭 방은 현재 서버가 관리한다. Redis는 대기 예약의 중복 처리만 방지한다.
    public sealed class MatchManager : IAsyncDisposable
    {
        private readonly IMatchMakingStore _store;
        private readonly TimeProvider _clock;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<Int32, Player> _pending = new();
        private readonly Dictionary<Int64, Room> _rooms = new();
        private readonly Dictionary<Int32, Int64> _roomByUser = new();
        private readonly Queue<MatchTicket> _cleanup = new();
        private readonly ConcurrentQueue<(Int32 User, Int64 Connection)> _disconnects = new();
        private readonly CancellationTokenSource _stop = new();
        private Task? _worker;
        private Int64 _nextRoom;
        private bool _stopping;
        private sealed record Player
        {
            public DBUser User { get; init; }
            public IMatchPeer Peer { get; init; }
            public MatchTicket Ticket { get; init; }

            public Player(DBUser user, IMatchPeer peer, MatchTicket ticket)
            {
                User = user;
                Peer = peer;
                Ticket = ticket;
            }
        }

        private sealed record Room
        {
            public Int64 Id { get; init; }
            public Player First { get; init; }
            public Player Second { get; init; }
            public Int64 CreatedAt { get; init; }

            public Room(Int64 id, Player first, Player second, Int64 createdAt)
            {
                Id = id;
                First = first;
                Second = second;
                CreatedAt = createdAt;
            }
        }

        private Int64 Now
        {
            get
            {
                return _clock.GetUtcNow().ToUnixTimeMilliseconds();
            }
        }

        public MatchManager(IMatchMakingStore store, TimeProvider? clock = null)
        {
            _store = store;
            _clock = clock ?? TimeProvider.System;
        }

        public void Start()
        {
            _worker ??= RunAsync();
        }

        public void ScheduleDisconnect(Int32 userId, Int64 connectionId)
        {
            _disconnects.Enqueue((userId, connectionId));
        }

        private static bool Valid(Player player)
        {
            return player.Peer.IsCurrent(player.Ticket.ConnectionId);
        }

        private static ACK_MATCH Reply(RESULT_CODE result, MATCH_STATE state = MATCH_STATE.Idle, string reason = "")
        {
            return new()
            {
                Result = result,
                State = state,
                Reason = reason
            };
        }

        public async Task<ACK_MATCH> JoinAsync(DBUser user, IMatchPeer peer)
        {
            MatchTicket ticket = new MatchTicket(user.Id, peer.ConnectionId, Guid.NewGuid().ToString("N"), user.Score, Now);
            await _gate.WaitAsync();
            try
            {
                if (_stopping || !peer.IsCurrent(ticket.ConnectionId))
                {
                    return Reply(RESULT_CODE.Disconnected);
                }

                if (_pending.ContainsKey(user.Id) || _roomByUser.ContainsKey(user.Id))
                {
                    return Reply(RESULT_CODE.Busy);
                }

                try
                {
                    if (!await _store.AddAsync(ticket))
                    {
                        return Reply(RESULT_CODE.Busy);
                    }
                }
                catch
                {
                    // 응답이 유실되어도 Redis에는 등록되었을 수 있으므로 정리 대상으로 남긴다.
                    _cleanup.Enqueue(ticket);
                    return Reply(RESULT_CODE.StorageError);
                }

                if (!peer.IsCurrent(ticket.ConnectionId))
                {
                    _cleanup.Enqueue(ticket);
                    return Reply(RESULT_CODE.Disconnected);
                }

                _pending.Add(user.Id, new Player(user, peer, ticket));
                return Reply(RESULT_CODE.Ok, MATCH_STATE.Pending);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<ACK_MATCH> CancelAsync(Int32 userId, Int64 connectionId)
        {
            await _gate.WaitAsync();
            try
            {
                if (_roomByUser.ContainsKey(userId))
                {
                    return Reply(RESULT_CODE.Busy, MATCH_STATE.Matched);
                }

                if (_pending.TryGetValue(userId, out Player? player))
                {
                    if (player.Ticket.ConnectionId != connectionId)
                    {
                        return Reply(RESULT_CODE.Disconnected);
                    }

                    RemovePending(player);
                }

                await FlushCleanupAsync();
                return Reply(RESULT_CODE.Ok);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<ACK_MATCH> LeaveAsync(Int32 userId, Int64 connectionId, string reason = "left")
        {
            await _gate.WaitAsync();
            try
            {
                if (_roomByUser.TryGetValue(userId, out Int64 roomId))
                {
                    Room room = _rooms[roomId];
                    Player player = room.First.User.Id == userId ? room.First : room.Second;
                    if (player.Ticket.ConnectionId != connectionId)
                    {
                        return Reply(RESULT_CODE.Disconnected);
                    }

                    Close(room, reason);
                }

                return Reply(RESULT_CODE.Ok, MATCH_STATE.Closed, reason);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task DisconnectAsync(Int32 userId, Int64 connectionId)
        {
            await _gate.WaitAsync();
            try
            {
                if (_pending.TryGetValue(userId, out Player? player) && player.Ticket.ConnectionId == connectionId)
                {
                    RemovePending(player);
                }

                if (_roomByUser.TryGetValue(userId, out Int64 roomId))
                {
                    Room room = _rooms[roomId];
                    Player member = room.First.User.Id == userId ? room.First : room.Second;
                    if (member.Ticket.ConnectionId == connectionId)
                    {
                        Close(room, "disconnected");
                    }
                }

                await FlushCleanupAsync();
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task TickAsync()
        {
            while (_disconnects.TryDequeue(out var disconnected))
            {
                await DisconnectAsync(disconnected.User, disconnected.Connection);
            }

            await _gate.WaitAsync();
            try
            {
                foreach (Player player in _pending.Values.ToArray())
                {
                    if (!Valid(player) || Now - player.Ticket.CreatedAt >= 60_000)
                    {
                        RemovePending(player);
                        Notify(player, Reply(RESULT_CODE.Ok, MATCH_STATE.Closed, "expired"));
                    }
                }

                foreach (Room room in _rooms.Values.ToArray())
                {
                    if (!Valid(room.First) || !Valid(room.Second) || Now - room.CreatedAt >= 300_000)
                    {
                        Close(room, "expired_or_disconnected");
                    }
                }

                await FlushCleanupAsync();
                if (_stopping)
                {
                    return;
                }

                foreach (Player first in _pending.Values.OrderBy(p => p.Ticket.CreatedAt).ToArray())
                {
                    if (!_pending.ContainsKey(first.User.Id))
                    {
                        continue;
                    }

                    Player? second = _pending.Values
                        .Where(p => p.User.Id != first.User.Id && Math.Abs((Int64)p.User.Score - first.User.Score) <= 1000)
                        .OrderBy(p => Math.Abs((Int64)p.User.Score - first.User.Score))
                        .ThenBy(p => p.Ticket.CreatedAt)
                        .FirstOrDefault();

                    if (second is null)
                    {
                        continue;
                    }

                    bool claimed;
                    try
                    {
                        claimed = await _store.ClaimAsync(first.Ticket, second.Ticket);
                    }
                    catch
                    {
                        FailPair(first, second, "storage_error");
                        continue;
                    }

                    if (!claimed || !Valid(first) || !Valid(second))
                    {
                        FailPair(first, second, "claim_invalidated");
                        continue;
                    }

                    _pending.Remove(first.User.Id);
                    _pending.Remove(second.User.Id);
                    Room room = new Room(++_nextRoom, first, second, Now);
                    _rooms.Add(room.Id, room);
                    _roomByUser.Add(first.User.Id, room.Id);
                    _roomByUser.Add(second.User.Id, room.Id);
                    Notify(first, new() { Result = RESULT_CODE.Ok, State = MATCH_STATE.Matched, MatchId = room.Id, Opponent = second.User.ToPacket() });
                    Notify(second, new() { Result = RESULT_CODE.Ok, State = MATCH_STATE.Matched, MatchId = room.Id, Opponent = first.User.ToPacket() });
                }

                await FlushCleanupAsync();
            }
            finally
            {
                _gate.Release();
            }
        }

        private static void Notify(Player player, ACK_MATCH reply)
        {
            if (Valid(player))
            {
                player.Peer.Notify(player.Ticket.ConnectionId, reply);
            }
        }

        private void RemovePending(Player player)
        {
            _pending.Remove(player.User.Id);
            _cleanup.Enqueue(player.Ticket);
        }

        private void FailPair(Player first, Player second, string reason)
        {
            RemovePending(first);
            RemovePending(second);
            Notify(first, Reply(RESULT_CODE.StorageError, MATCH_STATE.Closed, reason));
            Notify(second, Reply(RESULT_CODE.StorageError, MATCH_STATE.Closed, reason));
        }

        private void Close(Room room, string reason)
        {
            if (!_rooms.Remove(room.Id))
            {
                return;
            }

            _roomByUser.Remove(room.First.User.Id);
            _roomByUser.Remove(room.Second.User.Id);
            ACK_MATCH reply = new ACK_MATCH
            {
                Result = RESULT_CODE.Ok,
                State = MATCH_STATE.Closed,
                MatchId = room.Id,
                Reason = reason
            };
            Notify(room.First, reply);
            Notify(room.Second, reply);
        }

        private async Task FlushCleanupAsync()
        {
            Int32 attempts = _cleanup.Count;
            while (attempts-- > 0)
            {
                MatchTicket ticket = _cleanup.Dequeue();
                try
                {
                    await _store.RemoveAsync(ticket);
                }
                catch (Exception exception)
                {
                    _cleanup.Enqueue(ticket);
                    Console.Error.WriteLine($"Ticket cleanup will retry: {exception.Message}");
                    break; // Redis 장애 시 전체 대기열만큼 타임아웃을 반복하지 않는다.
                }
            }
        }

        private async Task RunAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await TickAsync();
                    await Task.Delay(TimeSpan.FromMilliseconds(100), _clock, _stop.Token);
                }
            }
            catch (OperationCanceledException)when (_stop.IsCancellationRequested)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            if (_worker is not null)
            {
                await _worker;
            }

            await _gate.WaitAsync();
            try
            {
                _stopping = true;
                foreach (Player player in _pending.Values.ToArray())
                {
                    RemovePending(player);
                }

                foreach (Room room in _rooms.Values.ToArray())
                {
                    Close(room, "server_stopped");
                }

                await FlushCleanupAsync(); // 종료 시 정리에 실패한 Redis 티켓은 TTL로 만료된다.
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
