using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;

using MySqlConnector;

namespace ServerCore.DataBase
{
    internal sealed class DbWorker : IAsyncDisposable
    {
        private readonly Int32 _workerID;
        
        private readonly MySqlDataSource _dataSource;

        private readonly ConcurrentQueue<IDbJob> _jobQueue = new();

        private readonly SemaphoreSlim _jobSignal = new(0);
        private readonly SemaphoreSlim _jobMaxSignal;

        private readonly object _stateLock = new object();

        private Task? _workerTask;

        private bool _isStart;

        private bool _acceptingJobs;
        private bool _disposed;

        public int WorkerID => _workerID;
        public int PendingJobCount => _jobQueue.Count;

        public DbWorker(int workerId, MySqlDataSource dataSource, Int32 maxJobCount)
        {
            ArgumentNullException.ThrowIfNull(dataSource);

            if (maxJobCount <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maxJobCount));
            }

            _workerID = workerId;
            _dataSource = dataSource;

            _jobMaxSignal = new SemaphoreSlim(maxJobCount, maxJobCount);
        }


        public void Start()
        {
            lock(_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);

                if (_isStart)
                    return;

                _isStart = true;
                _acceptingJobs = true;

                _workerTask = RunAsync();

            }

            Console.WriteLine($"DB Worker {_workerID} 시작");
        }

        public Task<T> EnqueueAsync<T>(
            Func<MySqlConnection, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default,
            string jobName = "")
        {
            ArgumentNullException.ThrowIfNull(operation);

            if(!_jobMaxSignal.Wait(0))
            {
                return Task.FromException<T>(new InvalidOperationException($"DB Worker {_workerID}의 작업 큐가 가득 찼습니다."));
            }

            try
            {
                var job = new DbJob<T>(operation, cancellationToken, jobName);


                lock (_stateLock)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);

                    if (!_isStart)
                    {
                        throw new InvalidOperationException($"DB Worker {_workerID}가 시작되지 않았습니다.");
                    }

                    if (!_acceptingJobs)
                    {
                        throw new InvalidOperationException($"DB Worker {_workerID}가 종료 중입니다.");
                    }
					
                    _jobQueue.Enqueue(job);


                    _jobSignal.Release();
                }

                return job.Completion;
            }
            catch
            {
                _jobMaxSignal.Release();
                throw;
            }           
        }


        public async Task RunAsync()
        {
            while(true)
            {
                await _jobSignal.WaitAsync();

                if(_jobQueue.TryDequeue(out IDbJob? job))
                {
                    try
                    {
                        await job.ExecuteAsync(
                            _dataSource,
                            CancellationToken.None,
                            _workerID);
                    }
                    finally
                    {
                        _jobMaxSignal.Release();
                    }
                }

                lock(_stateLock)
                {
                    if(!_acceptingJobs && _jobQueue.IsEmpty)
                    {
                        break;
                    }
                }
            }

            Console.WriteLine($"DB Worker {_workerID} 종료");
        }

        public async Task StopAsync()
        {
            Task? workerTask;

            lock(_stateLock)
            {
                if (!_isStart)
                    return;

                if(_acceptingJobs)
                {
                    _acceptingJobs = false;

                    _jobSignal.Release();
                }

                workerTask = _workerTask;
            }

            if(workerTask is not null)
                await workerTask;

            lock(_stateLock)
            {
                _isStart = false;
                _workerTask = null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            await StopAsync();

            _disposed = true;
            _jobSignal.Dispose();
            _jobMaxSignal.Dispose();
        }
    }


    public sealed class DbWorkerPool : IAsyncDisposable
    {
        private readonly DbWorker[] _workers;
        private bool _disposed;

        public int WorkerCount => _workers.Length;

        public DbWorkerPool(Int32 workerCount, MySqlDataSource dataSource)
        {            
            if(workerCount <= 0) {
                throw new ArgumentOutOfRangeException(nameof(workerCount), "Worker 개수는 1개 이상이어야 합니다.");
            }

            ArgumentNullException.ThrowIfNull(dataSource);

            _workers = new DbWorker[workerCount];

            for (int workerID = 0; workerID < workerCount; workerID++)
            {
                DbWorker _dbWorker = new DbWorker(workerID, dataSource, maxJobCount: 10_000);

                _workers[workerID] = _dbWorker;

                _dbWorker.Start();
            }
        }

        public Task<T> RegisterJob<T>(
            Int32 workerID,
            Func<MySqlConnection, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default,
            string jobName = "")
        {
            ArgumentNullException.ThrowIfNull(operation);
            ObjectDisposedException.ThrowIf(_disposed, this);

            if((UInt32)workerID >= (UInt32)_workers.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(workerID), workerID, "존재하지 않는 DB Worker ID입니다.");
            }

            return _workers[workerID].EnqueueAsync(operation, cancellationToken, jobName);
        }

        public Task<T> RegisterUserJobAsync<T>(
            Int64 userID,
            Func<MySqlConnection, CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default,
            string jobName = "")
        {
            Int32 workerID = GetWorkerID(userID);
			
            return RegisterJob(workerID, operation, cancellationToken, jobName);
        }

        public Int32 GetWorkerID(Int64 userID)
        {
            return (Int32)((Int64)userID % (Int64)_workers.Length);
        }

        public async Task StopAsync()
        {
            Task[] stopTasks = _workers.Select(worker => worker.StopAsync()).ToArray();

            await Task.WhenAll(stopTasks);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;

            await Task.WhenAll(_workers.Select(worker => worker.StopAsync()));

            foreach(DbWorker worker in _workers)
            {
                await worker.DisposeAsync();
            }
        }

    }
}
