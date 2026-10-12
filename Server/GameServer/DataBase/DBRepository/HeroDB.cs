using System;

using CSharp.Protocol;
using MySqlConnector;
using ServerCore.DataBase;
using ServerCore.Diagnostics;

namespace GameServer.DataBase
{
    public interface IHeroDB
    {
        Task<IReadOnlyList<DBHero>> LoadAsync(Int32 userId);
        Task<DBHero?> UpdateAsync(Int32 userId, Int64 heroId, HERO_FIELD field, bool value);
    }

    public sealed class HeroDB : IHeroDB
    {
        private readonly MysqlDataBase _database;
        public HeroDB(MysqlDataBase database)
        {
            _database = database;
        }

        private static DBHero Read(MySqlDataReader reader)
        {
            return new(reader.GetInt64(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetBoolean(3), reader.GetBoolean(4));
        }

        public Task<IReadOnlyList<DBHero>> LoadAsync(Int32 userId)
        {
            return _database.ExecuteAsync<IReadOnlyList<DBHero>>(userId, async (connection, token) =>
            {
                await using MySqlCommand command = connection.CreateCommand();
                
                command.CommandText = "SELECT id,user_id,template_id,is_pinned,is_locked FROM heroes WHERE user_id=@user ORDER BY id";
                command.Parameters.AddWithValue("@user", userId);
                
                await using MySqlDataReader reader = await ServerPerformanceMetrics.ExecuteReaderAsync(command, token);
                
                List<DBHero> rows = new List<DBHero>();
                while (await reader.ReadAsync(token))
                {
                    rows.Add(Read(reader));
                }

                return rows;
            });
        }

        public Task<DBHero?> UpdateAsync(Int32 userId, Int64 heroId, HERO_FIELD field, bool value)
        {
            string column = field switch
            {
                HERO_FIELD.Pinned => "is_pinned",
                HERO_FIELD.Locked => "is_locked",
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            };
            return _database.ExecuteAsync<DBHero?>(userId, async (connection, token) =>
            {
                await using MySqlTransaction transaction = await connection.BeginTransactionAsync(token);
                await using MySqlCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.Parameters.AddWithValue("@user", userId);
                command.Parameters.AddWithValue("@hero", heroId);
                command.Parameters.AddWithValue("@value", value);

                // 컬럼 이름은 위에서 허용한 enum 값으로만 결정한다.
                command.CommandText = $"UPDATE heroes SET {column}=@value WHERE id=@hero AND user_id=@user";

                await command.ExecuteNonQueryAsync(token);
                command.CommandText = "SELECT id,user_id,template_id,is_pinned,is_locked FROM heroes WHERE id=@hero AND user_id=@user";
                
                DBHero? saved;
                await using (MySqlDataReader reader = await ServerPerformanceMetrics.ExecuteReaderAsync(command, token))
                    saved = await reader.ReadAsync(token) ? Read(reader) : null;
               
                await transaction.CommitAsync(token);
                return saved;
            });
        }
    }
}


