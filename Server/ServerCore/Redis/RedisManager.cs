using System.Text.Json;
using StackExchange.Redis;

namespace ServerCore.Redis;

public static class RedisManager
{
    private static readonly SemaphoreSlim InitializeLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static ConnectionMultiplexer? _connection;
    private static IDatabase? _database;

    public static bool IsInitialized => Volatile.Read(ref _connection) is not null;

    public static bool IsConnected => Volatile.Read(ref _connection)?.IsConnected ?? false;

    public static IDatabase Database => Volatile.Read(ref _database) 
        ?? throw new InvalidOperationException("RedisManager가 초기화되지 않았습니다.");

    public static async Task InitializeAsync(ConfigurationOptions options, int databaseNumber = -1)
    {
        ArgumentNullException.ThrowIfNull(options);

        await InitializeLock.WaitAsync();

        try
        {
            if (IsInitialized)
            {
                throw new InvalidOperationException("RedisManager는 이미 초기화되었습니다.");
            }

            ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);

            IDatabase database = connection.GetDatabase(databaseNumber);

            Volatile.Write(ref _database, database);
            Volatile.Write(ref _connection, connection);
        }
        finally
        {
            InitializeLock.Release();
        }
    }

    public static Task<TimeSpan> PingAsync()
    {
        return Database.PingAsync();
    }

    public static Task<bool> SetValueAsync(RedisKey key, RedisValue value, TimeSpan? expiration = null)
    {
        Expiration redisExpiration = expiration.HasValue ? new Expiration(expiration.Value)
            : Expiration.Default;

        return Database.StringSetAsync(key, value, redisExpiration);
    }

    public static async Task<string?> GetValueAsync(RedisKey key)
    {
        RedisValue value = await Database.StringGetAsync(key).ConfigureAwait(false);

        return value.HasValue ? value.ToString() : null;
    }

    public static Task<bool> JsonSetAsync<T>(RedisKey key, T value, TimeSpan? expiration = null)
    {
        ArgumentNullException.ThrowIfNull(value);

        string json = JsonSerializer.Serialize(value, JsonOptions);
        Expiration redisExpiration = expiration.HasValue ? new Expiration(expiration.Value)
            : Expiration.Default;

        return Database.StringSetAsync(key, json, redisExpiration);
    }

    public static async Task<T?> JsonGetAsync<T>(RedisKey key)
    {
        RedisValue value = await Database.StringGetAsync(key).ConfigureAwait(false);

        if (!value.HasValue)
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(value.ToString(), JsonOptions);
    }

    public static Task<bool> DeleteAsync(RedisKey key)
    {
        return Database.KeyDeleteAsync(key);
    }

    public static async ValueTask ShutdownAsync()
    {
        await InitializeLock.WaitAsync();

        ConnectionMultiplexer? connection;

        try
        {
            Volatile.Write(ref _database, null);
            connection = Interlocked.Exchange(ref _connection, null);
        }
        finally
        {
            InitializeLock.Release();
        }

        if (connection is null)
        {
            return;
        }

        await connection.CloseAsync().ConfigureAwait(false);
        await connection.DisposeAsync().ConfigureAwait(false);
    }
}
