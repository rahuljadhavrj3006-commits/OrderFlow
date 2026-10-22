namespace ConcurrencyLab;

/// <summary>Console helper that stamps every line with the managed thread id, so you can SEE which thread runs what.</summary>
public static class Out
{
    public static void Line(string message)
    {
        var kind = Thread.CurrentThread.IsThreadPoolThread ? "pool" : "own ";
        Console.WriteLine($"  [T{Environment.CurrentManagedThreadId,3} {kind}] {message}");
    }
}
