using System.Threading;

// A guest that forks in `Main`'s prologue: the entry type's static constructor, which the prologue
// runs, starts a thread, so the first contended decision comes before any of `Main`'s body.
public class ForkInCctor
{
    private static int flag;

    static ForkInCctor()
    {
        Thread t = new Thread(() => { flag = 1; });
        t.Start();
    }

    private static int Main(string[] args)
    {
        return flag;
    }
}
