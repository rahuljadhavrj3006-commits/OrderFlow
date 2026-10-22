using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace ConcurrencyLab;

/// <summary>
/// Each method is a self-contained, runnable answer to a classic interview question.
/// Read the code, run it (dotnet run -- &lt;name&gt;), then explain the output in your own words.
/// </summary>
public static class Demos
{
    // ------------------------------------------------------------------------------------------
    // 1. Thread vs ThreadPool vs Task
    //    Q: "What is the difference between a Thread and a Task?"
    // ------------------------------------------------------------------------------------------
    public static async Task ThreadsVsTasks()
    {
        Out.Line("Main flow starts");

        var thread = new Thread(() => Out.Line("Dedicated Thread: I created it, I own it, ~1MB stack, I must manage it")) { IsBackground = true };
        thread.Start();
        thread.Join();

        var poolDone = new TaskCompletionSource();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Out.Line("ThreadPool work item: a reused, pooled thread - cheap, but no result/exception/continuation support");
            poolDone.SetResult();
        });
        await poolDone.Task;

        await Task.Run(() => Out.Line("Task.Run: runs on the pool AND gives me an awaitable handle, result and exception flow"));

        var answer = await Task.Run(() =>
        {
            Thread.Sleep(100);
            return 42;
        });
        Out.Line($"Task<int> result = {answer}. A Task is a PROMISE of a future result - not a thread.");

        var delay = Task.Delay(300);
        Out.Line($"Task.Delay(300) is just a timer: IsCompleted={delay.IsCompleted}, and NO thread is blocked while it waits.");
        await delay;

        Out.Line("After await we may be on a different thread (console apps have no SynchronizationContext).");
    }

    // ------------------------------------------------------------------------------------------
    // 2. Synchronous vs asynchronous vs concurrent
    //    Q: "Does async make my code faster?"  A: Not one call - but it frees threads and enables overlap.
    // ------------------------------------------------------------------------------------------
    public static async Task SyncVsAsync()
    {
        const int calls = 5;

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < calls; i++) FakeIoSync();
        Out.Line($"Sync sequential      : {sw.ElapsedMilliseconds,5} ms  (thread BLOCKED for every call)");

        sw.Restart();
        for (var i = 0; i < calls; i++) await FakeIoAsync();
        Out.Line($"Async sequential     : {sw.ElapsedMilliseconds,5} ms  (same time, but the thread is FREE while waiting)");

        sw.Restart();
        await Task.WhenAll(Enumerable.Range(0, calls).Select(_ => FakeIoAsync()));
        Out.Line($"Async concurrent     : {sw.ElapsedMilliseconds,5} ms  (calls overlap: ~ the slowest one, not the sum)");

        sw.Restart();
        Parallel.For(0, calls, _ => FakeIoSync());
        Out.Line($"Parallel.For + sleep : {sw.ElapsedMilliseconds,5} ms  (also overlaps, but burns {calls} threads doing nothing)");

        Out.Line("Takeaway: async = scalability for I/O-bound work. Parallel = speed for CPU-bound work. Don't mix them up.");
    }

    private static void FakeIoSync() => Thread.Sleep(500);
    private static Task FakeIoAsync() => Task.Delay(500);

    // ------------------------------------------------------------------------------------------
    // 3. Race conditions and thread-safe state
    //    Q: "How do you make a counter / cache thread-safe?"
    // ------------------------------------------------------------------------------------------
    public static async Task RaceConditions()
    {
        const int workers = 8;
        const int perWorker = 100_000;
        const int expected = workers * perWorker;

        var unsafeCounter = 0;
        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < perWorker; i++) unsafeCounter++; // read-modify-write is NOT atomic
        })));
        Out.Line($"counter++            : {unsafeCounter,8} (expected {expected}) -> lost updates: {expected - unsafeCounter}");

        var lockCounter = 0;
        var gate = new object();
        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < perWorker; i++)
            {
                lock (gate) { lockCounter++; }
            }
        })));
        Out.Line($"lock                 : {lockCounter,8} (correct, but threads queue up on the lock)");

        var interlockedCounter = 0;
        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < perWorker; i++) Interlocked.Increment(ref interlockedCounter);
        })));
        Out.Line($"Interlocked.Increment: {interlockedCounter,8} (correct, lock-free, fastest for a single variable)");

        // The subtle one: ConcurrentDictionary.GetOrAdd is thread-safe, but its FACTORY can run several times.
        var plain = new ConcurrentDictionary<string, int>();
        var factoryRuns = 0;
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            plain.GetOrAdd("key", _key =>
            {
                Interlocked.Increment(ref factoryRuns);
                Thread.Sleep(50);
                return 1;
            }))));
        Out.Line($"GetOrAdd(factory)    : factory ran {factoryRuns} time(s) for ONE key (only one result is kept)");

        var lazyMap = new ConcurrentDictionary<string, Lazy<int>>();
        var lazyRuns = 0;
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            lazyMap.GetOrAdd("key", _key => new Lazy<int>(() =>
            {
                Interlocked.Increment(ref lazyRuns);
                Thread.Sleep(50);
                return 1;
            })).Value)));
        Out.Line($"GetOrAdd + Lazy      : factory ran {lazyRuns} time(s)  <- the pattern used in Orders.Api IdempotencyStore");
    }

    // ------------------------------------------------------------------------------------------
    // 4. Throttling concurrency
    //    Q: "You must call an API for 10,000 items but it allows only 5 parallel calls. How?"
    // ------------------------------------------------------------------------------------------
    public static async Task Throttling()
    {
        using var gate = new SemaphoreSlim(3);
        var current = 0;
        var max = 0;

        var jobs = Enumerable.Range(1, 10).Select(async id =>
        {
            await gate.WaitAsync();                       // async-friendly (lock() cannot be used across await)
            try
            {
                var now = Interlocked.Increment(ref current);
                InterlockedMax(ref max, now);
                Out.Line($"job {id,2} started   (running now: {now})");
                await Task.Delay(300);
            }
            finally
            {
                Interlocked.Decrement(ref current);
                gate.Release();                           // ALWAYS release in finally
            }
        });

        await Task.WhenAll(jobs);
        Out.Line($"SemaphoreSlim(3): max concurrent = {max}");

        var seen = 0;
        var peak = 0;
        await Parallel.ForEachAsync(Enumerable.Range(1, 10), new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (_, ct) =>
        {
            var now = Interlocked.Increment(ref seen);
            InterlockedMax(ref peak, now);
            await Task.Delay(200, ct);
            Interlocked.Decrement(ref seen);
        });
        Out.Line($"Parallel.ForEachAsync(MaxDegreeOfParallelism=4): peak concurrent = {peak}  <- shortest way to throttle async work");
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while ((current = Volatile.Read(ref target)) < value &&
               Interlocked.CompareExchange(ref target, value, current) != current)
        {
        }
    }

    // ------------------------------------------------------------------------------------------
    // 5. Cancellation and timeouts
    //    Q: "How do you cancel a long-running operation? What happens to the work after a timeout?"
    // ------------------------------------------------------------------------------------------
    public static async Task Cancellation()
    {
        using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(700)))
        {
            try
            {
                await LongOperationAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                Out.Line("1) Cancelled by timeout - cooperative: the operation checked the token and stopped itself.");
            }
        }

        using var userCts = new CancellationTokenSource();
        using var safetyCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(userCts.Token, safetyCts.Token);

        var running = LongOperationAsync(linked.Token);
        await Task.Delay(300);
        userCts.Cancel();
        try
        {
            await running;
        }
        catch (OperationCanceledException)
        {
            Out.Line($"2) Linked token (user OR timeout): task.IsCanceled = {running.IsCanceled}");
        }

        try
        {
            await LongOperationAsync(CancellationToken.None).WaitAsync(TimeSpan.FromMilliseconds(200));
        }
        catch (TimeoutException)
        {
            Out.Line("3) WaitAsync(timeout) stopped WAITING - but the underlying work keeps running. Pass a token to actually stop it.");
        }
    }

    private static async Task LongOperationAsync(CancellationToken ct)
    {
        for (var step = 0; step < 20; step++)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(100, ct);
        }
    }

    // ------------------------------------------------------------------------------------------
    // 6. Producer / consumer with backpressure
    //    Q: "How do you decouple a fast producer from a slow consumer without running out of memory?"
    // ------------------------------------------------------------------------------------------
    public static async Task Channels()
    {
        var channel = Channel.CreateBounded<int>(new BoundedChannelOptions(3) { FullMode = BoundedChannelFullMode.Wait });

        var producer = Task.Run(async () =>
        {
            for (var i = 1; i <= 10; i++)
            {
                await channel.Writer.WriteAsync(i);       // WAITS when the channel is full = backpressure
                Out.Line($"produced {i}");
            }
            channel.Writer.Complete();                    // tells consumers "no more items"
        });

        var consumers = Enumerable.Range(1, 2).Select(id => Task.Run(async () =>
        {
            await foreach (var item in channel.Reader.ReadAllAsync())
            {
                Out.Line($"    consumer {id} handled {item}");
                await Task.Delay(150);                    // slow consumer
            }
        })).ToList();                                      // ToList: start the consumers exactly once

        await Task.WhenAll(consumers.Append(producer));
        Out.Line("Producer was throttled by the bounded capacity (3) - watch 'produced' lines pace themselves.");
    }

    // ------------------------------------------------------------------------------------------
    // 7. The classic sync-over-async deadlock
    //    Q: "Why does .Result / .Wait() deadlock?"
    // ------------------------------------------------------------------------------------------
    public static async Task Deadlock()
    {
        using var context = new SingleThreadSyncContext();

        // "Button click" on the UI-like thread: block on an async method with .Wait().
        var badFinished = await context.RunAsync(() => WorkAsync().Wait(TimeSpan.FromSeconds(2)));
        Out.Line(badFinished
            ? "BAD   : finished (unexpected)"
            : "BAD   : DEADLOCK. Wait() blocks the only thread; the continuation needs that same thread to resume.");

        var fixedFinished = await context.RunAsync(() => WorkNoCaptureAsync().Wait(TimeSpan.FromSeconds(2)));
        Out.Line(fixedFinished
            ? "FIX 1 : ConfigureAwait(false) - continuation doesn't need the blocked thread, so it completes."
            : "FIX 1 : still blocked (unexpected)");

        Out.Line("FIX 2 (best): never block - go async all the way: 'await WorkAsync()'. ASP.NET Core has no sync context, but thread-pool starvation is its cousin.");
    }

    private static async Task WorkAsync() => await Task.Delay(100);                          // captures the context
    private static async Task WorkNoCaptureAsync() => await Task.Delay(100).ConfigureAwait(false);

    // ------------------------------------------------------------------------------------------
    // 8. Exceptions with Task.WhenAll
    //    Q: "Three tasks fail. How many exceptions do you see after 'await Task.WhenAll'?"
    // ------------------------------------------------------------------------------------------
    public static async Task Exceptions()
    {
        var all = Task.WhenAll(FailAfter(100, "A"), FailAfter(200, "B"), Task.Delay(50));
        try
        {
            await all;
        }
        catch (Exception ex)
        {
            Out.Line($"'await' rethrows only the FIRST exception: {ex.Message}");
            var names = string.Join(", ", all.Exception!.InnerExceptions.Select(e => e.Message));
            Out.Line($"all.Exception.InnerExceptions holds every one: [{names}]  <- log these, not just the first");
        }

        Out.Line("Also remember: 'async void' exceptions can't be caught by the caller and can crash the process - use it only for event handlers.");
    }

    private static async Task FailAfter(int milliseconds, string name)
    {
        await Task.Delay(milliseconds);
        throw new InvalidOperationException($"boom {name}");
    }

    // ------------------------------------------------------------------------------------------
    // 9. Thread-pool starvation
    //    Q: "The API is slow under load but CPU is low. What is going on?"
    // ------------------------------------------------------------------------------------------
    public static Task ThreadPoolStarvation() =>
        Task.Factory.StartNew(StarvationBody, TaskCreationOptions.LongRunning); // run on our own thread so we can measure from outside the pool

    private static void StarvationBody()
    {
        ThreadPool.GetMinThreads(out var minWorkers, out _);
        var blockers = Math.Min(minWorkers * 3, 96);

        Out.Line($"Pool starts with ~{minWorkers} threads. Queuing {blockers} tasks that BLOCK a thread for 3s (Thread.Sleep)...");
        var hogs = Enumerable.Range(0, blockers).Select(_ => Task.Run(() => Thread.Sleep(3000))).ToArray();
        Thread.Sleep(200);

        var sw = Stopwatch.StartNew();
        Task.Run(() => { }).Wait();
        Out.Line($"A trivial Task.Run waited {sw.ElapsedMilliseconds} ms for a free thread  <- STARVATION (pool injects only ~1-2 threads/second)");
        Task.WaitAll(hogs);

        Out.Line($"Now the same load written with ASYNC waits (await Task.Delay) instead of blocking...");
        var polite = Enumerable.Range(0, blockers).Select(_ => Task.Run(async () => await Task.Delay(3000))).ToArray();
        Thread.Sleep(200);

        sw.Restart();
        Task.Run(() => { }).Wait();
        Out.Line($"A trivial Task.Run waited {sw.ElapsedMilliseconds} ms  <- threads are released while waiting, so the pool stays healthy");
        Task.WaitAll(polite);

        Out.Line("Lesson: blocking calls (.Result, .Wait(), Thread.Sleep, sync I/O) inside request handlers starve the pool. Go async end-to-end.");
    }
}
