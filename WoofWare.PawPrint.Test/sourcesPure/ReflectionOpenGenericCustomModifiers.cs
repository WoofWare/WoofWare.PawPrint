using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// `ParameterInfo.GetRequiredCustomModifiers` and `GetOptionalCustomModifiers` on members of open
// generic definitions: methods of `G<>` and `List<>`, and the generic method definition
// `H.GenericIn<>`. `Signature.GetCustomModifiersAtOffset` resolves each modifier it finds against the
// signature's generic context, which here supplies the definitions' own type variables rather than
// runtime types. None of these modifiers mentions a variable, so none needs one: `InAttribute` and
// `IsExternalInit` are named by TypeRef, even where the parameter they modify is a `T`.
//
// `ModuleBuilder` asks exactly this of `List<T>.Add` before emitting a call to `List<int>.Add`: it
// builds the call's signature from the generic definition.
//
// Roslyn emits `modreq(InAttribute)` for an `in` parameter and a `ref readonly` return only on a
// virtual method, so these are virtual. An `init` accessor carries `modreq(IsExternalInit)` on its
// return type whether virtual or not.

public class G<T>
{
    public int Init { get; init; }

    public virtual void InInt(in int a) { }

    public virtual void InT(int a, in T b) { }

    public virtual ref readonly T ReadonlyReturn() => ref _field;

    private T _field = default!;
}

public class H
{
    public virtual void GenericIn<U>(int a, in U b) { }
}

public class Program
{
    static int failures = 0;

    static void ExpectRequired(Type[] actual, Type expected, string what)
    {
        if (actual.Length != 1 || actual[0] != expected)
        {
            Console.WriteLine($"{what}: expected [{expected}], got {actual.Length} modifiers");
            failures++;
        }
    }

    static void ExpectNone(Type[] actual, string what)
    {
        if (actual.Length != 0)
        {
            Console.WriteLine($"{what}: expected no modifiers, got {actual.Length}");
            failures++;
        }
    }

    public static int Main(string[] argv)
    {
        var g = typeof(G<>);

        ExpectNone(typeof(List<>).GetMethod("Add").GetParameters()[0].GetRequiredCustomModifiers(), "List<>.Add item");
        ExpectNone(typeof(List<>).GetMethod("Add").ReturnParameter.GetOptionalCustomModifiers(), "List<>.Add return");

        ExpectRequired(g.GetMethod("InInt").GetParameters()[0].GetRequiredCustomModifiers(), typeof(InAttribute), "G<>.InInt a");
        ExpectNone(g.GetMethod("InInt").GetParameters()[0].GetOptionalCustomModifiers(), "G<>.InInt a optional");

        ExpectRequired(g.GetMethod("InT").GetParameters()[1].GetRequiredCustomModifiers(), typeof(InAttribute), "G<>.InT b");
        ExpectNone(g.GetMethod("InT").GetParameters()[0].GetRequiredCustomModifiers(), "G<>.InT a");

        ExpectRequired(g.GetMethod("ReadonlyReturn").ReturnParameter.GetRequiredCustomModifiers(), typeof(InAttribute), "G<>.ReadonlyReturn return");

        ExpectRequired(
            g.GetProperty("Init").SetMethod.ReturnParameter.GetRequiredCustomModifiers(),
            typeof(IsExternalInit),
            "G<>.Init setter return");

        // A generic method definition: the method's own variables are open, on a closed declaring type.
        var genericIn = typeof(H).GetMethod("GenericIn");
        ExpectRequired(genericIn.GetParameters()[1].GetRequiredCustomModifiers(), typeof(InAttribute), "H.GenericIn<> b");
        ExpectNone(genericIn.GetParameters()[0].GetRequiredCustomModifiers(), "H.GenericIn<> a");

        return failures;
    }
}
