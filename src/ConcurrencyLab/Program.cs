using ConcurrencyLab;

var demos = new List<Demo>
{
    new("threads",    "Thread vs ThreadPool vs Task",                    Demos.ThreadsVsTasks),
    new("async",      "Sync vs async vs concurrent vs parallel",         Demos.SyncVsAsync),
    new("races",      "Race conditions, lock, Interlocked, GetOrAdd",    Demos.RaceConditions),
    new("throttle",   "SemaphoreSlim and Parallel.ForEachAsync",         Demos.Throttling),
    new("cancel",     "CancellationToken, linked tokens, WaitAsync",     Demos.Cancellation),
    new("channels",   "Bounded Channel: producer/consumer + backpressure", Demos.Channels),
    new("deadlock",   "Sync-over-async deadlock and the fixes",          Demos.Deadlock),
    new("exceptions", "Task.WhenAll and exceptions",                     Demos.Exceptions),
    new("starvation", "Thread-pool starvation (blocking vs async)",     Demos.ThreadPoolStarvation),
};

var requested = args.Length == 0 || args[0].Equals("all", StringComparison.OrdinalIgnoreCase)
    ? demos
    : demos.Where(d => args.Contains(d.Name, StringComparer.OrdinalIgnoreCase)).ToList();

if (requested.Count == 0)
{
    Console.WriteLine("Usage: dotnet run --project src/ConcurrencyLab -- [all | <demo> ...]");
    Console.WriteLine();
    foreach (var d in demos)
    {
        Console.WriteLine($"  {d.Name,-11} {d.Description}");
    }
    return 1;
}

Console.WriteLine($"Processors: {Environment.ProcessorCount}, runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");

foreach (var demo in requested)
{
    Console.WriteLine();
    Console.WriteLine($"=== {demo.Name}: {demo.Description} ===");
    await demo.Run();
}

return 0;

internal sealed record Demo(string Name, string Description, Func<Task> Run);
