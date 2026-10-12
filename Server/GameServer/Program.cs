using GameServer.Config;
using GameServer.DataBase;
using GameServer.Matchmaking;
using GameServer.Packet;
using Microsoft.Extensions.Configuration;
using ServerCore.DataBase;
using ServerCore.Network;
using ServerCore.Redis;
using System;
using System.Diagnostics;
using System.Net;

namespace GameServer
{
    internal static class Program
    {
        private static async Task Main()
        {
            IConfiguration config = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("Config/config.json").AddJsonFile("Config/config.local.json", optional: true).Build();
            DatabaseSettings db = config.GetRequiredSection("Database").Get<DatabaseSettings>()!;
            RedisSettings redis = config.GetRequiredSection("Redis").Get<RedisSettings>()!;
            ServerSettings settings = config.GetRequiredSection("Server").Get<ServerSettings>()!;
            MysqlDataBase database = new MysqlDataBase(db.CreateConnectionString(), db.WorkerCount);
            MatchManager? matches = null;
            Service? service = null;
            ConsoleCancelEventHandler? onCancel = null;

            try
            {
                if (!await database.CheckConnectAsync())
                {
                    throw new InvalidOperationException("Local demo database is not ready. See README.");
                }

                await RedisManager.InitializeAsync(redis.CreateConnectionString(), redis.DatabaseNumber);
                await RedisManager.PingAsync();

                matches = new MatchManager(new RedisMatchmakingStore(RedisManager.Database));
                UserSessions users = new UserSessions();
                DataBaseRepository repository = new DataBaseRepository(database);
                GSPacket packet = new GSPacket(repository, matches);
                GSPacketHandler handler = new GSPacketHandler(packet);

                handler.Initialize();
                await SessionManager.Initialize(settings.MaxConnections, () => new GameSession(matches, users));

                service = new Service(SERVICE_TYPE.GAME_SERVER, handler, settings.MaxConnections);
                TaskCompletionSource shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                onCancel = (_, e) =>
                {
                    e.Cancel = true;
                    shutdown.TrySetResult();
                };
                Console.CancelKeyPress += onCancel;
                service.StartServer(IPAddress.Parse(settings.IpAddress), settings.Port);
                matches.Start();
                Console.WriteLine($"Portfolio server: {settings.IpAddress}:{settings.Port}. Demo seed login only. Ctrl+C to stop.");
                await Task.WhenAny(shutdown.Task, service.HeartbeatTask);
                if (!shutdown.Task.IsCompleted)
                {
                    // 검사 작업이 예기치 않게 멈춘 상태로 서버를 계속 운영하지 않는다.
                    await service.HeartbeatTask;
                    throw new InvalidOperationException("Heartbeat 작업이 예기치 않게 종료되었습니다.");
                }
            }
            finally
            {
                try
                {
                    // 정상 종료는 순서대로 기다린다. await using의 자동 해제로 이 순서를 우회하지 않는다.
                    if (service != null)
                    {
                        await WaitForShutdownAsync(service.StopServerAsync(), "세션", TimeSpan.FromSeconds(10), Console.Error.WriteLine);
                    }

                    if (matches != null)
                    {
                        await WaitForShutdownAsync(matches.DisposeAsync().AsTask(), "매칭", TimeSpan.FromSeconds(10), Console.Error.WriteLine);
                    }

                    await WaitForShutdownAsync(database.DisposeAsync().AsTask(), "DB Worker 및 연결", TimeSpan.FromSeconds(10), Console.Error.WriteLine);
                    await WaitForShutdownAsync(RedisManager.ShutdownAsync().AsTask(), "Redis", TimeSpan.FromSeconds(10), Console.Error.WriteLine);
                    Console.WriteLine("서버 정상 종료 완료.");
                }
                catch (Exception exception)
                {
                    // 실제 정리 오류는 숨기지 않는다. 이후 의존 자원의 정상 해제는 진행하지 않는다.
                    Console.Error.WriteLine($"서버 종료 중 오류: {exception}");
                    throw;
                }
                finally
                {
                    // 종료 대기 중 추가 Ctrl+C도 기존 처리기로 받아 저장 작업을 중단하지 않는다.
                    if (onCancel != null)
                    {
                        Console.CancelKeyPress -= onCancel;
                    }
                }
            }
        }

        internal static async Task WaitForShutdownAsync(Task shutdownTask, string name, TimeSpan warningInterval, Action<string> writeWarning)
        {
            if (warningInterval <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(warningInterval));
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            using CancellationTokenSource warningStop = new CancellationTokenSource();
            try
            {
                while (!shutdownTask.IsCompleted)
                {
                    Task warningDelay = Task.Delay(warningInterval, warningStop.Token);
                    await Task.WhenAny(shutdownTask, warningDelay);
                    if (shutdownTask.IsCompleted)
                    {
                        break;
                    }

                    // 10초는 실패 기준이 아닌 경고 주기다. 같은 종료 Task를 계속 기다린다.
                    writeWarning($"{name} 종료 대기 중: {stopwatch.Elapsed.TotalSeconds:F0}초 경과. 작업 완료를 계속 기다립니다.");
                }

                // 실제 종료 오류는 호출자에게 전달한다.
                await shutdownTask;
            }
            finally
            {
                warningStop.Cancel();
            }
        }
    }
}
