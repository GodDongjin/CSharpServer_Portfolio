using System;

using StackExchange.Redis;

namespace GameServer.Matchmaking
{
    // 연결 식별자가 포함된 토큰으로 이전 연결의 정리가 새 예약을 삭제하지 않도록 한다.
    public sealed class RedisMatchmakingStore : IMatchMakingStore
    {
        private readonly IDatabase _database;
        private readonly string _prefix;
        public RedisMatchmakingStore(IDatabase database, string prefix = "csharp:{portfolio}:pending:")
        {
            _database = database;
            _prefix = prefix;
        }

        private RedisKey Key(MatchTicket ticket)
        {
            return $"{_prefix}{ticket.UserId}";
        }

        private static string Value(MatchTicket ticket)
        {
            return $"{ticket.ConnectionId}:{ticket.Token}";
        }

        public Task<bool> AddAsync(MatchTicket ticket)
        {
            return _database.StringSetAsync(Key(ticket), Value(ticket), TimeSpan.FromSeconds(120), When.NotExists);
        }

        public async Task RemoveAsync(MatchTicket ticket)
        {
            await _database.ScriptEvaluateAsync("if redis.call('GET',KEYS[1]) == ARGV[1] then return redis.call('DEL',KEYS[1]) end return 0", [Key(ticket)], [Value(ticket)]);
        }

        public async Task<bool> ClaimAsync(MatchTicket first, MatchTicket second)
        {
            if (first.UserId == second.UserId)
            {
                return false;
            }

            RedisResult result = await _database.ScriptEvaluateAsync("""
            if redis.call('GET',KEYS[1]) ~= ARGV[1] or redis.call('GET',KEYS[2]) ~= ARGV[2] then return 0 end
            redis.call('DEL',KEYS[1],KEYS[2])
            return 1
            """, [Key(first), Key(second)], [Value(first), Value(second)]);
            return (Int64)result == 1;
        }
    }
}
