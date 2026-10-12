using System;
using System.Collections.Concurrent;

namespace GameServer.Tests
{
    public class ShutdownTests
    {
        [Fact]
        public async Task Warnings_do_not_finish_or_cancel_the_shutdown_task()
        {
            TaskCompletionSource shutdown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource warned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ConcurrentQueue<string> warnings = new ConcurrentQueue<string>();
            Task waiting = Program.WaitForShutdownAsync(shutdown.Task, "DB", TimeSpan.FromMilliseconds(10), message =>
            {
                warnings.Enqueue(message);
                if (warnings.Count >= 2)
                {
                    warned.TrySetResult();
                }
            });

            try
            {
                await warned.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(waiting.IsCompleted);
                Assert.False(shutdown.Task.IsCompleted);
                Assert.All(warnings, message => Assert.Contains("DB 종료 대기 중", message));
            }
            finally
            {
                shutdown.TrySetResult();
                await waiting.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [Fact]
        public async Task Shutdown_error_is_propagated_without_running_later_cleanup()
        {
            InvalidOperationException failure = new InvalidOperationException("Reset failed.");
            bool databaseDisposed = false;
            async Task Shutdown()
            {
                await Program.WaitForShutdownAsync(Task.FromException(failure), "세션", TimeSpan.FromSeconds(10), _ => { });
                databaseDisposed = true;
            }

            Exception actual = await Assert.ThrowsAsync<InvalidOperationException>(Shutdown);
            Assert.Same(failure, actual);
            Assert.False(databaseDisposed);
        }

        [Fact]
        public async Task Completed_shutdown_does_not_wait_for_warning_interval()
        {
            Int32 warnings = 0;
            await Program.WaitForShutdownAsync(Task.CompletedTask, "세션", TimeSpan.FromSeconds(10), _ => warnings++)
                .WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(0, warnings);
        }
    }
}
