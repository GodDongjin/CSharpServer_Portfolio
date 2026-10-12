using System;

using CSharp.Protocol;
using MySqlConnector;
using ServerCore.DataBase;
using ServerCore.Diagnostics;

namespace GameServer.DataBase
{
    public interface ILoginDB
    {
        Task<DBUser?> FindAsync(string loginId);
    }

    public sealed class LoginDB : ILoginDB
    {
        private readonly MysqlDataBase _database;
        public LoginDB(MysqlDataBase database)
        {
            _database = database;
        }

        public Task<DBUser?> FindAsync(string loginId)
        {
            return _database.ExecuteAsync<DBUser?>(0, async (connection, token) =>
            {
                await using MySqlCommand command = connection.CreateCommand();
                command.CommandText = "SELECT id, login_id, name, score FROM users WHERE login_id=@login";
               
                command.Parameters.AddWithValue("@login", loginId);
                await using MySqlDataReader reader = await ServerPerformanceMetrics.ExecuteReaderAsync(command, token);
                
                return await reader.ReadAsync(token) ? new DBUser(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)) : null;
            });
        }
    }
}
