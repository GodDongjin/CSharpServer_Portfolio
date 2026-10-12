using System;
using CSharp.Protocol;
using GameServer.DataBase;
using GameServer.Hero;
using GameServer.Matchmaking;

namespace GameServer.Tests
{
    public class HeroAndMatchTests
    {
        private static readonly DBHero Original = new(10, 1, 100, false, false);
        private sealed class Heroes : IHeroDB
        {
            public Func<Task<DBHero?>> Save = () => Task.FromResult<DBHero?>(Original with { IsPinned = true });
            public Int32 Calls;
            public Task<IReadOnlyList<DBHero>> LoadAsync(Int32 userId)
            {
                return Task.FromResult<IReadOnlyList<DBHero>>([Original]);
            }

            public Task<DBHero?> UpdateAsync(Int32 userId, Int64 heroId, HERO_FIELD field, bool value)
            {
                Calls++;
                return Save();
            }
        }

        private sealed class Peer : IMatchPeer
        {
            private readonly Int64 _id;
            public bool Connected = true;
            public List<ACK_MATCH> Notices = [];
            public Int64 ConnectionId { get; set; }

            public Peer(Int64 id)
            {
                _id = id;
                ConnectionId = _id;
            }

            public bool IsCurrent(Int64 id)
            {
                return Connected && id == ConnectionId;
            }

            public void Notify(Int64 id, ACK_MATCH reply)
            {
                if (IsCurrent(id))
                {
                    Notices.Add(reply);
                }
            }
        }

        private sealed class Store : IMatchMakingStore
        {
            public Dictionary<Int32, MatchTicket> Tickets = [];
            public bool FailRemove, FailClaim, FailAdd;
            public Action? OnClaim;
            public Task<bool> AddAsync(MatchTicket t)
            {
                if (FailAdd)
                {
                    throw new IOException();
                }

                return Task.FromResult(Tickets.TryAdd(t.UserId, t));
            }

            public Task RemoveAsync(MatchTicket t)
            {
                if (FailRemove)
                {
                    throw new IOException();
                }

                if (Tickets.GetValueOrDefault(t.UserId) == t)
                {
                    Tickets.Remove(t.UserId);
                }

                return Task.CompletedTask;
            }

            public Task<bool> ClaimAsync(MatchTicket a, MatchTicket b)
            {
                if (FailClaim)
                {
                    throw new IOException();
                }

                OnClaim?.Invoke();
                bool valid = Tickets.GetValueOrDefault(a.UserId) == a && Tickets.GetValueOrDefault(b.UserId) == b;
                if (valid)
                {
                    Tickets.Remove(a.UserId);
                    Tickets.Remove(b.UserId);
                }

                return Task.FromResult(valid);
            }
        }

        private sealed class Clock : TimeProvider
        {
            public DateTimeOffset Now = DateTimeOffset.UnixEpoch;
            public override DateTimeOffset GetUtcNow()
            {
                return Now;
            }
        }

        private static REQ_HERO_UPDATE Request
        {
            get
            {
                return new()
                {
                    HeroId = 10,
                    Field = HERO_FIELD.Pinned,
                    Value = true
                };
            }
        }

        private static HeroManager Collection()
        {
            var result = new HeroManager();
            result.Load([Original]);
            return result;
        }

        [Fact]
        public async Task MemoryChangesOnlyAfterCommit()
        {
            var completion = new TaskCompletionSource<DBHero?>();
            var repo = new Heroes
            {
                Save = () => completion.Task
            };
            var memory = Collection();
            var task = memory.UpdateAsync(repo, 1, Request, () => true);
            Assert.Equal(Original, memory.GetHeroData(10)?._dbHero);
            completion.SetResult(Original with { IsPinned = true });
            Assert.Equal(RESULT_CODE.Ok, (await task).Result);
            Assert.True(memory.GetHeroData(10)?._dbHero!.IsPinned);
        }

        [Fact]
        public async Task DatabaseFailurePreservesMemory()
        {
            var repo = new Heroes
            {
                Save = () => throw new IOException("database unavailable")
            };
            var memory = Collection();
            await Assert.ThrowsAsync<IOException>(() => memory.UpdateAsync(repo, 1, Request, () => true));
            Assert.Equal(Original, memory.GetHeroData(10)?._dbHero);
        }

        [Fact]
        public async Task DisconnectAfterCommitDoesNotMutateReusedSession()
        {
            var memory = Collection();
            Assert.Equal(RESULT_CODE.Disconnected, (await memory.UpdateAsync(new Heroes(), 1, Request, () => false)).Result);
            Assert.Equal(Original, memory.GetHeroData(10)?._dbHero);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(999)]
        public async Task UnknownFieldDoesNotWrite(Int32 field)
        {
            var repo = new Heroes();
            var request = Request;
            request.Field = (HERO_FIELD)field;
            Assert.Equal(RESULT_CODE.InvalidRequest, (await Collection().UpdateAsync(repo, 1, request, () => true)).Result);
            Assert.Equal(0, repo.Calls);
        }

        [Fact]
        public async Task OtherOwnerDoesNotWrite()
        {
            var repo = new Heroes();
            Assert.Equal(RESULT_CODE.NotFound, (await Collection().UpdateAsync(repo, 2, Request, () => true)).Result);
            Assert.Equal(0, repo.Calls);
        }

        [Fact]
        public async Task MissingDatabaseRowPreservesMemory()
        {
            var repo = new Heroes
            {
                Save = () => Task.FromResult<DBHero?>(null)
            };
            var memory = Collection();
            Assert.Equal(RESULT_CODE.NotFound, (await memory.UpdateAsync(repo, 1, Request, () => true)).Result);
            Assert.Equal(Original, memory.GetHeroData(10)?._dbHero);
        }

        [Fact]
        public async Task WrongDatabaseOwnerRejected()
        {
            var repo = new Heroes
            {
                Save = () => Task.FromResult<DBHero?>(Original with { UserId = 2 })
            };
            await Assert.ThrowsAsync<InvalidDataException>(() => Collection().UpdateAsync(repo, 1, Request, () => true));
        }

        private static DBUser User(Int32 id, Int32 score = 1000)
        {
            return new(id, $"demo-{id}", $"User {id}", score);
        }

        [Fact]
        public async Task DuplicateJoinAndCancelAreSafe()
        {
            var store = new Store();
            await using var manager = new MatchManager(store);
            var peer = new Peer(1);
            Assert.Equal(MATCH_STATE.Pending, (await manager.JoinAsync(User(1), peer)).State);
            Assert.Equal(RESULT_CODE.Busy, (await manager.JoinAsync(User(1), peer)).Result);
            await manager.CancelAsync(1, 1);
            await manager.CancelAsync(1, 1);
            Assert.Empty(store.Tickets);
        }

        [Fact]
        public async Task MatchAndDisconnectNotifyOpponent()
        {
            var store = new Store();
            await using var manager = new MatchManager(store);
            var a = new Peer(1);
            var b = new Peer(2);
            await manager.JoinAsync(User(1), a);
            await manager.JoinAsync(User(2), b);
            await manager.TickAsync();
            Assert.Equal(MATCH_STATE.Matched, Assert.Single(a.Notices).State);
            Assert.Equal(a.Notices[0].MatchId, Assert.Single(b.Notices).MatchId);
            Assert.Empty(store.Tickets);
            a.Connected = false;
            await manager.DisconnectAsync(1, 1);
            Assert.Equal(MATCH_STATE.Closed, b.Notices[^1].State);
            Assert.Equal(RESULT_CODE.Ok, (await manager.JoinAsync(User(2), b)).Result);
        }

        [Fact]
        public async Task OldDisconnectCannotRemoveNewConnection()
        {
            var store = new Store();
            await using var manager = new MatchManager(store);
            await manager.JoinAsync(User(1), new Peer(20));
            await manager.DisconnectAsync(1, 10);
            Assert.Single(store.Tickets);
        }

        [Fact]
        public async Task TimeoutRemovesPendingTicket()
        {
            var store = new Store();
            var clock = new Clock();
            await using var manager = new MatchManager(store, clock);
            var peer = new Peer(1);
            await manager.JoinAsync(User(1), peer);
            clock.Now += TimeSpan.FromSeconds(60);
            await manager.TickAsync();
            Assert.Empty(store.Tickets);
            Assert.Equal("expired", Assert.Single(peer.Notices).Reason);
        }

        [Fact]
        public async Task DistantScoresDoNotMatch()
        {
            await using var manager = new MatchManager(new Store());
            var a = new Peer(1);
            var b = new Peer(2);
            await manager.JoinAsync(User(1, 0), a);
            await manager.JoinAsync(User(2, 1001), b);
            await manager.TickAsync();
            Assert.Empty(a.Notices);
        }

        [Fact]
        public async Task RedisCleanupRetries()
        {
            var store = new Store();
            await using var manager = new MatchManager(store);
            await manager.JoinAsync(User(1), new Peer(1));
            store.FailRemove = true;
            await manager.CancelAsync(1, 1);
            Assert.Single(store.Tickets);
            store.FailRemove = false;
            await manager.TickAsync();
            Assert.Empty(store.Tickets);
        }

        [Fact]
        public async Task ClaimFailureClosesBothAndCleansTickets()
        {
            var store = new Store
            {
                FailClaim = true
            };
            await using var manager = new MatchManager(store);
            var a = new Peer(1);
            var b = new Peer(2);
            await manager.JoinAsync(User(1), a);
            await manager.JoinAsync(User(2), b);
            await manager.TickAsync();
            Assert.Equal(RESULT_CODE.StorageError, Assert.Single(a.Notices).Result);
            Assert.Equal(MATCH_STATE.Closed, Assert.Single(b.Notices).State);
            Assert.Empty(store.Tickets);
        }

        [Fact]
        public async Task DisconnectDuringClaimCannotCreateRoom()
        {
            var a = new Peer(1);
            var b = new Peer(2);
            var store = new Store
            {
                OnClaim = () => a.Connected = false
            };
            await using var manager = new MatchManager(store);
            await manager.JoinAsync(User(1), a);
            await manager.JoinAsync(User(2), b);
            await manager.TickAsync();
            Assert.Empty(a.Notices);
            Assert.Equal(MATCH_STATE.Closed, Assert.Single(b.Notices).State);
        }

        [Fact]
        public async Task JoinStorageFailureReported()
        {
            await using var manager = new MatchManager(new Store { FailAdd = true });
            Assert.Equal(RESULT_CODE.StorageError, (await manager.JoinAsync(User(1), new Peer(1))).Result);
        }
    }
}
