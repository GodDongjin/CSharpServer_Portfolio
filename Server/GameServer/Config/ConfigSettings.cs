using System;
using System.Collections.Generic;
using System.Text;

using StackExchange.Redis;

namespace GameServer.Config
{
    public sealed class DatabaseSettings
    {
        public string Server { get; init; } = string.Empty;
        public Int32 Port { get; init; } = 3306;
        public string Database { get; init; } = string.Empty;
        public string UserId { get; init; } = string.Empty;
        public string Password { get; init; } = string.Empty;
        public UInt32 MinimumPoolSize { get; init; }
        public UInt32 MaximumPoolSize { get; init; } = 50;
        public Int32 WorkerCount { get; init; } = 4;

        public string CreateConnectionString()
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Server);
            ArgumentException.ThrowIfNullOrWhiteSpace(Database);
            ArgumentException.ThrowIfNullOrWhiteSpace(UserId);
            ArgumentException.ThrowIfNullOrWhiteSpace(Password);
            MySqlConnector.MySqlConnectionStringBuilder builder = new MySqlConnector.MySqlConnectionStringBuilder
            {
                Server = Server,
                Port = (UInt32)Port,
                Database = Database,
                UserID = UserId,
                Password = Password,
                Pooling = true,
                MinimumPoolSize = MinimumPoolSize,
                MaximumPoolSize = MaximumPoolSize
            };
            return builder.ConnectionString;
        }
    }

    public sealed class RedisSettings
    {
        public string Ip { get; init; } = string.Empty;
        public Int32 Port { get; init; } = 6379;
        public string? Username { get; init; }
        public string? Password { get; init; }
        public Int32 DatabaseNumber { get; init; }

        public ConfigurationOptions CreateConnectionString()
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(Ip);
            if (Port is <= 0 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(Port));
            }

            if (DatabaseNumber < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(DatabaseNumber));
            }

            ConfigurationOptions options = new()
            {
                AbortOnConnectFail = false,
                ConnectTimeout = 3000,
                AsyncTimeout = 3000,
                SyncTimeout = 3000,
                DefaultDatabase = DatabaseNumber,
                ClientName = "CSharp.GameServer"
            };
            options.EndPoints.Add(Ip, Port);
            if (!string.IsNullOrWhiteSpace(Username))
            {
                options.User = Username;
            }

            if (!string.IsNullOrWhiteSpace(Password))
            {
                options.Password = Password;
            }

            return options;
        }
    }

    public sealed class ServerSettings
    {
        public string IpAddress { get; init; } = "127.0.0.1";
        public Int16 Port { get; init; } = 7777;
        public Int32 MaxConnections { get; init; } = 1000;
    }
}
