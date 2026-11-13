using System;
using System.Runtime.CompilerServices;
using System.Threading;

// Which monitor a synchronized method holds, and over what span, observed from one thread through
// Monitor.IsEntered. An instance method holds `this`'s monitor and a static one its type's
// RuntimeType's, for an exact generic instantiation however its code is shared. The monitor is
// taken again by a recursive call and stays held when that call returns; it is still held while
// the method's own finally runs and while a caller's filter runs during the first pass over an
// exception leaving the method, and is released by the time the caller's catch runs, including
// when the runtime wraps that exception as it leaves a constructor. Constructors, delegate,
// virtual and reflection calls hold it as any other call does.
class Holder
{
    public bool HeldInBody;
    public bool OtherHeldInBody;
    public Holder Other;

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Body()
    {
        HeldInBody = Monitor.IsEntered(this);
        OtherHeldInBody = Monitor.IsEntered(Other);
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public int WithResult(int x)
    {
        return Monitor.IsEntered(this) ? x + 1 : -1;
    }

    // Returns how many levels saw the monitor held both on entry and after their inner call returned.
    [MethodImpl(MethodImplOptions.Synchronized)]
    public int Recurse(int depth)
    {
        bool onEntry = Monitor.IsEntered(this);
        int inner = depth == 0 ? 0 : Recurse(depth - 1);
        bool afterInner = Monitor.IsEntered(this);
        return onEntry && afterInner ? inner + 1 : inner;
    }

    public bool HeldInFinally;

    [MethodImpl(MethodImplOptions.Synchronized)]
    public void Throws()
    {
        try
        {
            throw new InvalidOperationException("from a synchronized method");
        }
        finally
        {
            HeldInFinally = Monitor.IsEntered(this);
        }
    }

    [MethodImpl(MethodImplOptions.Synchronized)]
    public bool CatchesOwn()
    {
        try
        {
            throw new InvalidOperationException("caught inside");
        }
        catch (InvalidOperationException)
        {
        }

        return Monitor.IsEntered(this);
    }
}

class Base
{
    public virtual bool Held()
    {
        return Monitor.IsEntered(this);
    }
}

class Derived : Base
{
    [MethodImpl(MethodImplOptions.Synchronized)]
    public override bool Held()
    {
        return Monitor.IsEntered(this);
    }
}

class StaticHolder
{
    [MethodImpl(MethodImplOptions.Synchronized)]
    public static bool Held()
    {
        return Monitor.IsEntered(typeof(StaticHolder));
    }
}

class Generic<T>
{
    [MethodImpl(MethodImplOptions.Synchronized)]
    public static bool HeldOnOwnInstantiationOnly()
    {
        bool own = Monitor.IsEntered(typeof(Generic<T>));
        bool otherString = typeof(T) != typeof(string) && Monitor.IsEntered(typeof(Generic<string>));
        bool otherObject = typeof(T) != typeof(object) && Monitor.IsEntered(typeof(Generic<object>));
        bool otherInt = typeof(T) != typeof(int) && Monitor.IsEntered(typeof(Generic<int>));
        bool open = Monitor.IsEntered(typeof(Generic<>));
        return own && !otherString && !otherObject && !otherInt && !open;
    }
}

class Constructed
{
    public static Constructed Published;
    public bool HeldInConstructor;

    [MethodImpl(MethodImplOptions.Synchronized)]
    public Constructed()
    {
        HeldInConstructor = Monitor.IsEntered(this);
    }
}

class ThrowingConstructed
{
    public static ThrowingConstructed Published;

    [MethodImpl(MethodImplOptions.Synchronized)]
    public ThrowingConstructed()
    {
        Published = this;
        throw new InvalidOperationException("from a synchronized constructor");
    }
}

class Program
{
    static T Create<T>() where T : new()
    {
        return new T();
    }

    static bool HeldWhenFiltering(Holder h, Exception e)
    {
        return e is InvalidOperationException && Monitor.IsEntered(h);
    }

    static int Main(string[] args)
    {
        var h = new Holder();
        h.Other = new Holder();

        h.Body();
        if (!h.HeldInBody)
        {
            return 1;
        }

        if (h.OtherHeldInBody)
        {
            return 2;
        }

        if (Monitor.IsEntered(h))
        {
            return 3;
        }

        if (h.WithResult(41) != 42 || Monitor.IsEntered(h))
        {
            return 4;
        }

        if (h.Recurse(4) != 5)
        {
            return 5;
        }

        if (Monitor.IsEntered(h))
        {
            return 6;
        }

        bool filterSawHeld = false;
        bool catchSawHeld = true;
        try
        {
            h.Throws();
        }
        catch (Exception e) when (filterSawHeld = HeldWhenFiltering(h, e))
        {
            catchSawHeld = Monitor.IsEntered(h);
        }

        if (!filterSawHeld)
        {
            return 7;
        }

        if (!h.HeldInFinally)
        {
            return 8;
        }

        if (catchSawHeld || Monitor.IsEntered(h))
        {
            return 9;
        }

        if (!h.CatchesOwn() || Monitor.IsEntered(h))
        {
            return 10;
        }

        Base b = new Derived();
        if (!b.Held() || Monitor.IsEntered(b))
        {
            return 11;
        }

        if (new Base().Held())
        {
            return 12;
        }

        if (!StaticHolder.Held() || Monitor.IsEntered(typeof(StaticHolder)))
        {
            return 13;
        }

        if (!Generic<int>.HeldOnOwnInstantiationOnly())
        {
            return 14;
        }

        if (!Generic<string>.HeldOnOwnInstantiationOnly())
        {
            return 15;
        }

        if (!Generic<object>.HeldOnOwnInstantiationOnly())
        {
            return 16;
        }

        Func<int, int> viaDelegate = h.WithResult;
        if (viaDelegate(1) != 2 || Monitor.IsEntered(h))
        {
            return 17;
        }

        var constructed = new Constructed();
        if (!constructed.HeldInConstructor || Monitor.IsEntered(constructed))
        {
            return 19;
        }

        // `new T()` is `Activator.CreateInstance<T>()`, which wraps what the constructor throws in
        // a TargetInvocationException as it leaves the constructor.
        bool wrapped = false;
        try
        {
            Create<ThrowingConstructed>();
        }
        catch (System.Reflection.TargetInvocationException e) when (e.InnerException is InvalidOperationException)
        {
            wrapped = true;
        }

        if (!wrapped || ThrowingConstructed.Published == null || Monitor.IsEntered(ThrowingConstructed.Published))
        {
            return 20;
        }

        var viaReflection = (int)typeof(Holder).GetMethod("WithResult").Invoke(h, new object[] { 5 });
        if (viaReflection != 6 || Monitor.IsEntered(h))
        {
            return 21;
        }

        // Another thread can take the monitor once every synchronized call above has returned:
        // each released exactly what it took.
        bool otherThreadTook = false;
        var t = new Thread(() =>
        {
            if (Monitor.TryEnter(h))
            {
                otherThreadTook = true;
                Monitor.Exit(h);
            }
        });
        t.Start();
        t.Join();
        if (!otherThreadTook)
        {
            return 18;
        }

        return 0;
    }
}
