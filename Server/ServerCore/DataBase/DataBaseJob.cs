using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using MySqlConnector;
using ServerCore.Diagnostics;

namespace ServerCore.DataBase
{


    internal interface IDbJob
    {
        Task ExecuteAsync(
            MySqlDataSource dataSource,
            CancellationToken workerCancelToken,
            int workerId
            );
    }

    internal sealed class DbJob<T> : IDbJob
    {
        private readonly Func<MySqlConnection, CancellationToken, Task<T>> _operation;

        private readonly CancellationToken _cancelToken;
        private readonly string _jobName;
        private readonly long _enqueuedTimestamp;

        private readonly TaskCompletionSource<T> _completionSource =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Completion => _completionSource.Task;

        public DbJob(Func<MySqlConnection, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default, string jobName = "")
        {
            ArgumentNullException.ThrowIfNull(operation);

            _operation = operation;
            _cancelToken = cancellationToken;
            _jobName = string.IsNullOrWhiteSpace(jobName) ? "unknown" : jobName;
            _enqueuedTimestamp = Stopwatch.GetTimestamp();
        }

        public async Task ExecuteAsync(MySqlDataSource dataSource,
            CancellationToken workerCancelToken, int workerId)
        {
            ArgumentNullException.ThrowIfNull(dataSource);

            using CancellationTokenSource linkedCancellationSource =
                CancellationTokenSource.CreateLinkedTokenSource(_cancelToken, workerCancelToken);

            CancellationToken cancellationToken = linkedCancellationSource.Token;
            double queueWaitMs = Stopwatch.GetElapsedTime(_enqueuedTimestamp).TotalMilliseconds;
            double connectionAcquireMs = 0;
            double operationMs = 0;
            string outcome = "success";

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                long connectionStartTimestamp = Stopwatch.GetTimestamp();
                MySqlConnection connection;
                try
                {
                    connection = await dataSource.OpenConnectionAsync(cancellationToken);
                }
                finally
                {
                    connectionAcquireMs = Stopwatch.GetElapsedTime(connectionStartTimestamp).TotalMilliseconds;
                }

                T result;
                // connection을 진행할 작업에 넘겨줘 DB작업 진행.
                await using (connection)
                {
                    long operationStartTimestamp = Stopwatch.GetTimestamp();

                    using (ServerPerformanceMetrics.BeginDbOperation(workerId, _jobName))
                    {
                        try
                        {
                            result = await _operation(connection, cancellationToken);
                        }
                        finally
                        {
                            operationMs = Stopwatch.GetElapsedTime(operationStartTimestamp).TotalMilliseconds;
                        }
                    }
                }

                // Completion Task를 성공 상태로 완료하고 결과를 전달한다.
                _completionSource.TrySetResult(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcome = "canceled";
                _completionSource.TrySetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                outcome = "error";
                _completionSource.TrySetException(ex);
            }
            finally
            {
                ServerPerformanceMetrics.RecordDbJob(
                    workerId,
                    _jobName,
                    outcome,
                    queueWaitMs,
                    connectionAcquireMs,
                    operationMs);

                double totalMs = queueWaitMs + connectionAcquireMs + operationMs;
                if (totalMs >= 50 || outcome != "success")
                {
                    Console.WriteLine(
                        $"[DB-TIMING] workerId={workerId}, job={_jobName}, " +
                        $"queueWaitMs={queueWaitMs:F3}, " +
                        $"connectionAcquireMs={connectionAcquireMs:F3}, " +
                        $"operationMs={operationMs:F3}, totalMs={totalMs:F3}, " +
                        $"outcome={outcome}");
                }
            }
        }

    }
}
