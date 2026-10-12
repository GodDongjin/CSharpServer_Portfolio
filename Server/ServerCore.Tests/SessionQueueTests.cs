using System;
using ServerCore.Tests.Support;

namespace ServerCore.Tests
{
    [Trait("Category", "Unit")]
    public sealed class SessionQueueTests
    {
        [Fact]
        public async Task Jobs_in_one_session_wait_for_previous_completion()
        {
            await using var harness = SessionHarness.ForQueue();
            var gate = SessionHarness.Signal();
            var firstStarted = SessionHarness.Signal();
            var secondFinished = SessionHarness.Signal();
            var order = new List<Int32>();
            try
            {
                Assert.True(harness.Session.EnqueueAsyncJob(async () =>
                {
                    firstStarted.SetResult();
                    await gate.Task;
                    order.Add(1);
                }));
                await firstStarted.Task.WaitAsync(SessionHarness.Timeout);
                Assert.True(harness.Session.EnqueueAsyncJob(() =>
                {
                    order.Add(2);
                    secondFinished.SetResult();
                    return ValueTask.CompletedTask;
                }));
                Assert.False(secondFinished.Task.IsCompleted);
                gate.SetResult();
                await secondFinished.Task.WaitAsync(SessionHarness.Timeout);
                Assert.Equal(new[] { 1, 2 }, order);
            }
            finally
            {
                gate.TrySetResult();
            }
        }

        [Fact]
        public async Task Blocked_session_does_not_block_another_session()
        {
            await using var first = SessionHarness.ForQueue();
            await using var second = SessionHarness.ForQueue();
            var gate = SessionHarness.Signal();
            var started = SessionHarness.Signal();
            var completed = SessionHarness.Signal();
            try
            {
                Assert.True(first.Session.EnqueueAsyncJob(async () =>
                {
                    started.SetResult();
                    await gate.Task;
                }));
                await started.Task.WaitAsync(SessionHarness.Timeout);
                Assert.True(second.Session.EnqueueAsyncJob(() =>
                {
                    completed.SetResult();
                    return ValueTask.CompletedTask;
                }));
                await completed.Task.WaitAsync(SessionHarness.Timeout);
                Assert.False(gate.Task.IsCompleted);
            }
            finally
            {
                gate.TrySetResult();
            }
        }

        [Fact]
        public async Task Queue_accepts_30_waiting_jobs_rejects_next_then_recovers()
        {
            await using var harness = SessionHarness.ForQueue();
            var gate = SessionHarness.Signal();
            var started = SessionHarness.Signal();
            var drained = SessionHarness.Signal();
            Int32 executed = 0;
            Int32 rejectedExecuted = 0;
            try
            {
                Assert.True(harness.Session.EnqueueAsyncJob(async () =>
                {
                    started.SetResult();
                    await gate.Task;
                }));
                await started.Task.WaitAsync(SessionHarness.Timeout);
                for (Int32 i = 0; i < 30; i++)
                    Assert.True(harness.Session.EnqueueAsyncJob(() =>
                    {
                        if (Interlocked.Increment(ref executed) == 30)
                        {
                            drained.SetResult();
                        }

                        return ValueTask.CompletedTask;
                    }));
                Assert.False(harness.Session.EnqueueAsyncJob(() =>
                {
                    Interlocked.Increment(ref rejectedExecuted);
                    return ValueTask.CompletedTask;
                }));
                gate.SetResult();
                await drained.Task.WaitAsync(SessionHarness.Timeout);
                var recovered = SessionHarness.Signal();
                Assert.True(harness.Session.EnqueueAsyncJob(() =>
                {
                    recovered.SetResult();
                    return ValueTask.CompletedTask;
                }));
                await recovered.Task.WaitAsync(SessionHarness.Timeout);
                Assert.Equal(30, Volatile.Read(ref executed));
                Assert.Equal(0, Volatile.Read(ref rejectedExecuted));
            }
            finally
            {
                gate.TrySetResult();
            }
        }

        [Fact]
        public async Task Throwing_job_does_not_prevent_later_jobs_or_release()
        {
            await using var harness = SessionHarness.ForQueue();
            var gate = SessionHarness.Signal();
            var started = SessionHarness.Signal();
            var completed = SessionHarness.Signal();
            try
            {
                Assert.True(harness.Session.EnqueueAsyncJob(async () =>
                {
                    started.SetResult();
                    await gate.Task;
                    throw new InvalidOperationException("Intentional test failure inside job.");
                }));
                await started.Task.WaitAsync(SessionHarness.Timeout);
                Assert.True(harness.Session.EnqueueAsyncJob(() =>
                {
                    completed.SetResult();
                    return ValueTask.CompletedTask;
                }));
                gate.SetResult();
                await completed.Task.WaitAsync(SessionHarness.Timeout);
                harness.Session.Disconnect();
                await harness.Owner.Released.Task.WaitAsync(SessionHarness.Timeout);
                Assert.Equal(1, harness.Owner.ReleaseCount);
            }
            finally
            {
                gate.TrySetResult();
            }
        }
    }
}
