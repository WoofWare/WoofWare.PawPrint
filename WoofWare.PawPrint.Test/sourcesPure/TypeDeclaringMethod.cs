using System;
using System.Collections.Generic;
using System.Reflection;

// `Type.DeclaringMethod`, which reaches `RuntimeTypeHandle.GetDeclaringMethodForGenericParameter` for a
// generic parameter and turns the method it answers into a `MethodBase` on that method's declaring type.
// A method's type variable is declared by the generic method definition, so that is the answer however
// the variable was reached: through the definition, through an instantiation of the declaring type, or
// through the definition's signature. `SignatureHelper` asks this of each generic parameter it writes, to
// choose between `ELEMENT_TYPE_MVAR` and `ELEMENT_TYPE_VAR`.
//
// Reference equality throughout: RuntimeType's member cache hands back one `MethodInfo` per method, and
// `DeclaringMethod` finds it there.
//
// Exit code is the index of the first failing check.

public class Holder
{
    public static void M<A, B> () { }
}

public class Box<T>
{
    public static void N<U> () { }
}

public static class Program
{
    public static int Main ()
    {
        // A method of a non-generic type of another assembly.
        MethodInfo empty = typeof (Array).GetMethod ("Empty")!;
        Type emptyT = empty.GetGenericArguments ()[0];
        MethodBase? declaring = emptyT.DeclaringMethod;
        if (declaring == null) return 1;
        if (!ReferenceEquals (declaring, empty)) return 2;
        if (!ReferenceEquals (emptyT.DeclaringMethod, declaring)) return 3;
        if (declaring.DeclaringType != typeof (Array)) return 4;
        if (!declaring.IsGenericMethodDefinition) return 5;

        // The same variable, reached through the definition's return type `T[]`.
        if (!ReferenceEquals (empty.ReturnType.GetElementType ()!.DeclaringMethod, empty)) return 6;

        // A method of this assembly, and its second variable.
        MethodInfo m = typeof (Holder).GetMethod ("M")!;
        Type b = m.GetGenericArguments ()[1];
        if (!ReferenceEquals (b.DeclaringMethod, m)) return 7;
        if (b.GenericParameterPosition != 1) return 8;

        // A generic method of a generic type definition: declared on the definition.
        MethodInfo n = typeof (Box<>).GetMethod ("N")!;
        Type u = n.GetGenericArguments ()[0];
        if (!ReferenceEquals (u.DeclaringMethod, n)) return 9;
        if (u.DeclaringMethod!.DeclaringType != typeof (Box<>)) return 10;

        // Reached through an instantiation of the declaring type: still the definition's method.
        MethodInfo nInt = typeof (Box<int>).GetMethod ("N")!;
        Type uThroughInt = nInt.GetGenericArguments ()[0];
        if (!ReferenceEquals (uThroughInt, u)) return 11;
        if (!ReferenceEquals (uThroughInt.DeclaringMethod, n)) return 12;
        if (ReferenceEquals (uThroughInt.DeclaringMethod, nInt)) return 13;

        // The same, for a method of a generic type of another assembly.
        MethodInfo convertAll = typeof (List<>).GetMethod ("ConvertAll")!;
        Type output = typeof (List<int>).GetMethod ("ConvertAll")!.GetGenericArguments ()[0];
        if (!ReferenceEquals (output.DeclaringMethod, convertAll)) return 14;
        if (output.DeclaringMethod!.DeclaringType != typeof (List<>)) return 15;

        // A type's variable has no declaring method.
        if (typeof (List<>).GetGenericArguments ()[0].DeclaringMethod != null) return 16;
        if (typeof (Box<>).GetGenericArguments ()[0].DeclaringMethod != null) return 17;

        return 0;
    }
}
