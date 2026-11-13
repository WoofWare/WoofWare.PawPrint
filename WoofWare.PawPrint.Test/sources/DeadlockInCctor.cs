using System.Threading;

// The same wedge as `DeadlockBeforeFork.cs`, but in the entry type's static constructor, which
// `Main`'s prologue runs, rather than in `Main`'s body.
public class DeadlockInCctor
{
    private static int flag;

    static DeadlockInCctor()
    {
        Thread.Sleep(Timeout.Infinite);
        flag = 1;
    }

    private static int Main(string[] args)
    {
        return flag;
    }
}
