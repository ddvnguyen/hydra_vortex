namespace Tests.Core.Scheduling;

/// <summary>
/// Pins the contract of the serial async mailbox used by the worker scheduler
/// rewrite (epic #591): one handler at a time, in order, graceful drain on stop,
/// and handler exceptions that never kill the loop. Everything is asserted via
/// awaited tasks and completion state — no sleeps.
/// </summary>
public sealed class MailboxExecutorTests
{
    [Fact]
    public async Task Post_Before_Start_Throws()
    {
        var executor = new MailboxExecutor();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.PostAsync(() => Task.CompletedTask));
        Assert.Contains("not started", ex.Message);
        await executor.DisposeAsync(); // unstarted executor disposes as a no-op
    }

    [Fact]
    public void Start_Twice_Throws()
    {
        var executor = new MailboxExecutor();
        executor.Start();
        try
        {
            Assert.Throws<InvalidOperationException>(() => executor.Start());
        }
        finally
        {
            _ = executor.StopAsync();
        }
    }

    [Fact]
    public async Task Post_Runs_Handlers_Serially_In_Order()
    {
        var executor = new MailboxExecutor();
        executor.Start();
        var ran = new List<string>();

        var tasks = new[]
        {
            executor.PostAsync(async () => { ran.Add("1"); await Task.Yield(); ran.Add("1-done"); }),
            executor.PostAsync(async () => { ran.Add("2"); await Task.Yield(); ran.Add("2-done"); }),
            executor.PostAsync(async () => { ran.Add("3"); await Task.Yield(); ran.Add("3-done"); }),
        };

        await Task.WhenAll(tasks);
        await executor.StopAsync();

        // FIFO order, and each handler ran to completion before the next began.
        Assert.Equal(new[] { "1", "1-done", "2", "2-done", "3", "3-done" }, ran);
    }

    [Fact]
    public async Task Handlers_Never_Run_Concurrently()
    {
        var executor = new MailboxExecutor();
        executor.Start();
        var guard = 0;

        var tasks = Enumerable.Range(0, 20).Select(_ => executor.PostAsync(async () =>
        {
            // If two handlers ever ran at once, the second would find the guard taken.
            Assert.Equal(0, Interlocked.Exchange(ref guard, 1));
            await Task.Yield();
            Interlocked.Exchange(ref guard, 0);
        }));

        await Task.WhenAll(tasks);
        await executor.StopAsync();
        Assert.Equal(0, guard);
    }

    [Fact]
    public async Task Post_Returns_Task_Completing_When_Handler_Completes()
    {
        var executor = new MailboxExecutor();
        executor.Start();
        var flag = false;

        var posted = executor.PostAsync(async () =>
        {
            await Task.Yield();
            flag = true;
        });

        Assert.False(flag);           // handler has not run yet at post time
        await posted;
        Assert.True(flag);            // completing the post means the handler finished
        await executor.StopAsync();
    }

    [Fact]
    public async Task Handler_Exception_Faults_Posted_Task_And_Loop_Continues()
    {
        Exception? captured = null;
        var executor = new MailboxExecutor(onHandlerError: ex => captured = ex);
        executor.Start();

        var failing = executor.PostAsync(() => throw new InvalidOperationException("boom"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing);
        Assert.Equal(1, executor.HandlerErrorCount);
        Assert.IsType<InvalidOperationException>(captured);

        // The loop survived the exception and still executes subsequent handlers.
        var ok = executor.PostAsync(() => Task.CompletedTask);
        await ok;
        Assert.Equal(1, executor.HandlerErrorCount); // unchanged — only the failure counted

        await executor.StopAsync();
    }

    [Fact]
    public async Task Stop_Drains_Pending_Handlers()
    {
        var executor = new MailboxExecutor();
        executor.Start();
        var ran = 0;

        var posts = Enumerable.Range(0, 5)
            .Select(_ => executor.PostAsync(async () => { await Task.Yield(); Interlocked.Increment(ref ran); }))
            .ToArray();

        await executor.StopAsync(); // returns only after every posted handler ran
        Assert.Equal(5, ran);
        Assert.Equal(0, executor.PendingCount);
        Assert.False(executor.IsRunning);
        await Task.WhenAll(posts); // all posts completed during the drain
    }

    [Fact]
    public async Task PendingCount_Reflects_Queued_Handlers()
    {
        var executor = new MailboxExecutor();
        executor.Start();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var blocker = executor.PostAsync(async () =>
        {
            started.TrySetResult();
            await release.Task;
        });
        await started.Task; // the consumer loop is now executing the blocker

        var queued = Enumerable.Range(0, 3)
            .Select(_ => executor.PostAsync(() => Task.CompletedTask))
            .ToArray();
        Assert.Equal(3, executor.PendingCount);

        release.TrySetResult();
        await blocker;
        await Task.WhenAll(queued);
        await executor.StopAsync();
        Assert.Equal(0, executor.PendingCount);
    }

    [Fact]
    public async Task Post_After_Stop_Throws()
    {
        var executor = new MailboxExecutor();
        executor.Start();
        await executor.PostAsync(() => Task.CompletedTask);
        await executor.StopAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.PostAsync(() => Task.CompletedTask));
        Assert.Contains("stopped", ex.Message);
    }

    [Fact]
    public async Task Stop_On_Unstarted_Executor_Throws()
    {
        var executor = new MailboxExecutor();
        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.StopAsync());
        await executor.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_Drains_Like_Stop()
    {
        var executor = new MailboxExecutor();
        executor.Start();
        var ran = 0;

        _ = executor.PostAsync(async () => { await Task.Yield(); Interlocked.Increment(ref ran); });
        _ = executor.PostAsync(async () => { await Task.Yield(); Interlocked.Increment(ref ran); });
        await executor.DisposeAsync();

        Assert.Equal(2, ran);
        Assert.False(executor.IsRunning);
    }

    [Fact]
    public async Task Concurrent_Posters_All_Handlers_Run_Exactly_Once()
    {
        var executor = new MailboxExecutor();
        executor.Start();
        var ran = 0;

        var postTasks = Enumerable.Range(0, 8).Select(worker => Task.Run(async () =>
        {
            for (var i = 0; i < 25; i++)
                await executor.PostAsync(() => { Interlocked.Increment(ref ran); return Task.CompletedTask; });
        }));

        await Task.WhenAll(postTasks);
        await executor.StopAsync();
        Assert.Equal(200, ran);
    }
}
