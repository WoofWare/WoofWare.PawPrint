using System.Threading;

// The entry type's static constructor makes the entry thread a background thread and then starts
// a foreground worker that never finishes. The runtime decides whether a process waits for its
// foreground threads from the moment it is about to call Main, which is before the static
// constructor runs: when the entry thread became background no foreground thread was alive, so
// the process is already free to end, and it exits with Main's return value rather than waiting
// for the worker.
class Program
{
    static Program()
    {
        Thread.CurrentThread.IsBackground = true;
        var worker = new Thread(() => Thread.Sleep(Timeout.Infinite));
        worker.Start();
    }

    static int Main(string[] args)
    {
        return 3;
    }
}
