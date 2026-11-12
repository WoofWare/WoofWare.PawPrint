using System.Runtime.CompilerServices;
using System.Threading;

// Two threads call one synchronized method, instance and then static. The first caller waits
// inside the body until the second is about to call, then yields for a while: had the second
// entered the body meanwhile, the two would have run it at once, which the monitor the runtime
// takes around a synchronized method's body forbids.
class Instance
{
    public volatile bool FirstEntered;
    public volatile bool SecondReady;
    public volatile bool SecondEntered;
    public volatile bool OverlapSeen;

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Body(bool first)
    {
        if (first)
        {
            FirstEntered = true;
            while (!SecondReady)
            {
                Thread.Yield();
            }

            for (int i = 0; i < 200 && !SecondEntered; i++)
            {
                Thread.Yield();
            }

            if (SecondEntered)
            {
                OverlapSeen = true;
            }
        }
        else
        {
            SecondEntered = true;
        }
    }
}

class Static
{
    public static volatile bool FirstEntered;
    public static volatile bool SecondReady;
    public static volatile bool SecondEntered;
    public static volatile bool OverlapSeen;

    [MethodImpl(MethodImplOptions.Synchronized)]
    public static void Body(bool first)
    {
        if (first)
        {
            FirstEntered = true;
            while (!SecondReady)
            {
                Thread.Yield();
            }

            for (int i = 0; i < 200 && !SecondEntered; i++)
            {
                Thread.Yield();
            }

            if (SecondEntered)
            {
                OverlapSeen = true;
            }
        }
        else
        {
            SecondEntered = true;
        }
    }
}

class Program
{
    static int Main(string[] args)
    {
        int result = 0;

        var instance = new Instance();
        var firstInstance = new Thread(() => instance.Body(true));
        var secondInstance = new Thread(() =>
        {
            while (!instance.FirstEntered)
            {
                Thread.Yield();
            }

            instance.SecondReady = true;
            instance.Body(false);
        });
        firstInstance.Start();
        secondInstance.Start();
        firstInstance.Join();
        secondInstance.Join();
        if (instance.OverlapSeen || !instance.SecondEntered)
        {
            result |= 1;
        }

        var firstStatic = new Thread(() => Static.Body(true));
        var secondStatic = new Thread(() =>
        {
            while (!Static.FirstEntered)
            {
                Thread.Yield();
            }

            Static.SecondReady = true;
            Static.Body(false);
        });
        firstStatic.Start();
        secondStatic.Start();
        firstStatic.Join();
        secondStatic.Join();
        if (Static.OverlapSeen || !Static.SecondEntered)
        {
            result |= 2;
        }

        return result;
    }
}
