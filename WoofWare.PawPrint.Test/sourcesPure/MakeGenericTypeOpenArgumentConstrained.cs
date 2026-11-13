using System;

public class NeedsStruct<T> where T : struct
{
}

public class Holder<U>
{
}

public class StructHolder<S> where S : struct
{
}

public static class Program
{
    // `MakeGenericType` over a type parameter, against a parameter that declares a constraint.
    // CoreCLR decides it from the argument's own constraints (`SatisfiesSpecialConstraintRecursive`
    // and `GatherConstraintsRecursive`): an unconstrained variable is refused, and a variable
    // itself constrained to `struct` is admitted.
    //
    // Exit code is the index of the first failing check, so a failure names itself.
    public static int Main()
    {
        Type u = typeof(Holder<>).GetGenericArguments()[0];
        try
        {
            typeof(NeedsStruct<>).MakeGenericType(u);
            return 1;
        }
        catch (ArgumentException e)
        {
            if (e.InnerException is not TypeLoadException) return 2;
        }

        Type s = typeof(StructHolder<>).GetGenericArguments()[0];
        Type admitted = typeof(NeedsStruct<>).MakeGenericType(s);
        if (!ReferenceEquals(admitted.GetGenericArguments()[0], s)) return 3;

        return 0;
    }
}
