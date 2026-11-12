using System;

// A pointer to a stack local, stored through a `ref T*` (which C# compiles to `stind.i`) into a
// pointer-typed location, then dereferenced from that location. CoreLib does this with
// `AsyncDispatcherInfo.t_current`, a `[ThreadStatic]` pointer to a stack local.
unsafe class Program
{
    struct S
    {
        public int X;
        public long Y;
    }

    class Holder
    {
        public S* P;
    }

    static S* s_p;

    [ThreadStatic]
    static S* t_p;

    static void Store(ref S* destination, S* value)
    {
        destination = value;
    }

    static int Main(string[] args)
    {
        S local = new S { X = 5, Y = 7 };

        // A static, through a `ref` taken inline.
        ref S* r = ref s_p;
        r = &local;
        if (s_p->X != 5) return 1;
        if (s_p->Y != 7) return 2;
        if (s_p != &local) return 3;

        // A write through the stored pointer reaches the local.
        s_p->X = 6;
        if (local.X != 6) return 4;

        // A thread-static, through a `ref` passed to a callee.
        Store(ref t_p, &local);
        if (t_p->X != 6) return 5;
        t_p = null;
        if (t_p != null) return 6;

        // A local of pointer type.
        S* p = null;
        Store(ref p, &local);
        if (p->Y != 7) return 7;

        // An instance field of pointer type.
        var holder = new Holder();
        Store(ref holder.P, &local);
        if (holder.P->X != 6) return 8;

        // An element of a pointer array.
        S*[] array = new S*[2];
        Store(ref array[1], &local);
        if (array[1]->Y != 7) return 9;
        if (array[0] != null) return 10;

        // `ldind.i` back through the same `ref` sees what was stored.
        S* readBack = r;
        if (readBack != &local) return 11;
        if (readBack->X != 6) return 12;

        return 0;
    }
}
