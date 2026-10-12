using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using MySqlConnector;


namespace ServerCore.DataBase
{
    public sealed class MysqlDataBase : IAsyncDisposable
    {
        private readonly MySqlDataSource _dataSource;
        private readonly DbWorkerPool _workerPool;

        private bool _disposed;

        public MysqlDataBase(string connectionString, Int32 workerCount)
        {
            ArgumentNullException.ThrowIfNullOrWhiteSpace(connectionString);

            if(workerCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(workerCount));
            }

            //MySqlDataSourceBuilder를 통해 connectionString으로 설정된 구성으로 생성된 새로운 MySqlDataSource를 반환해준다.
            _dataSource = new MySqlDataSourceBuilder(connectionString).Build();

            _workerPool = new DbWorkerPool(workerCount, _dataSource);
        }
        
        public async Task<bool> CheckConnectAsync(CancellationToken cancelToken = default)
        {
            try
            {
                await using MySqlConnection connection = await _dataSource.OpenConnectionAsync(cancelToken);

                await using MySqlCommand command = connection.CreateCommand();

                command.CommandText = "SELECT 1;";

                object? result = await command.ExecuteScalarAsync(cancelToken);

                return Convert.ToInt32(result) == 1;
            }
            catch(MySqlException exception)
            {
                Console.WriteLine($"MySql 연결 실패 : {exception.Message}");

                return false;
            }
        }


        public Task<T> ExecuteAsync<T>(
            Int64 userID             ,
            Func<MySqlConnection, CancellationToken, Task<T>> operation              ,
            CancellationToken cancellationToken = default         ,
            [CallerMemberName] string jobName = "",
            [CallerFilePath] string sourceFilePath = "")
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            string sourceName = Path.GetFileNameWithoutExtension(sourceFilePath);
            string resolvedJobName = string.IsNullOrWhiteSpace(sourceName)
                ? jobName
                : $"{sourceName}.{jobName}";

            return _workerPool.RegisterUserJobAsync(
                userID,
                operation,
                cancellationToken,
                resolvedJobName);
        }
      
        public async ValueTask DisposeAsync() 
        {
            if (_disposed)
                return;

            _disposed = true;

            await _workerPool.DisposeAsync();
            await _dataSource.DisposeAsync();
        }
    }
}
