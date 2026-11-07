using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// `ParameterInfo.GetRequiredCustomModifiers` and `GetOptionalCustomModifiers` on a method's
// parameters and return. Each asks `Signature.GetCustomModifiers(index)`, which finds the
// parameter's byte offset in the method signature with `Signature.GetParameterOffsetInternal`
// (index 0 the return, index i the i-th parameter) and reads the modifiers there. The offset walk
// skips every earlier element of the signature, so each method puts the modified parameter after
// parameters of a different encoding: a primitive, a class, a generic instantiation, a
// multi-dimensional array, a byref, and a generic method's `!!0`. A walk that skips one of those
// by the wrong number of bytes lands mid-element and reads no modifiers, or the wrong ones.
//
// Roslyn emits `modreq(InAttribute)` for an `in` parameter and a `ref readonly` return only on a
// virtual method, so these are virtual. An `init` accessor carries `modreq(IsExternalInit)` on its
// return type whether virtual or not.

public class C
{
    public int Init { get; init; }

    public virtual void AfterPrimitive(int a, in int b) { }

    public virtual void AfterClass(string a, object b, in int c) { }

    public virtual void AfterGenericInstantiation(List<string> a, Dictionary<int, string> b, in int c) { }

    public virtual void AfterArray(int[,] a, string[] b, in int c) { }

    public virtual void AfterByRef(ref long a, out int b, in int c) { b = 0; }

    public virtual void Generic<T>(T a, in T b) { }

    public virtual ref readonly int ReadonlyReturn(int a) => ref _field;

    public virtual void Unmodified(in int a, string b) { }

    private int _field;
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
        var c = typeof(C);

        ExpectRequired(
            c.GetMethod("AfterPrimitive").GetParameters()[1].GetRequiredCustomModifiers(),
            typeof(InAttribute),
            "AfterPrimitive b");
        ExpectNone(
            c.GetMethod("AfterPrimitive").GetParameters()[0].GetRequiredCustomModifiers(),
            "AfterPrimitive a");
        ExpectNone(
            c.GetMethod("AfterPrimitive").ReturnParameter.GetRequiredCustomModifiers(),
            "AfterPrimitive return");

        ExpectRequired(
            c.GetMethod("AfterClass").GetParameters()[2].GetRequiredCustomModifiers(),
            typeof(InAttribute),
            "AfterClass c");

        ExpectRequired(
            c.GetMethod("AfterGenericInstantiation").GetParameters()[2].GetRequiredCustomModifiers(),
            typeof(InAttribute),
            "AfterGenericInstantiation c");

        ExpectRequired(
            c.GetMethod("AfterArray").GetParameters()[2].GetRequiredCustomModifiers(),
            typeof(InAttribute),
            "AfterArray c");

        ExpectRequired(
            c.GetMethod("AfterByRef").GetParameters()[2].GetRequiredCustomModifiers(),
            typeof(InAttribute),
            "AfterByRef c");
        ExpectNone(
            c.GetMethod("AfterByRef").GetParameters()[1].GetRequiredCustomModifiers(),
            "AfterByRef b");

        // `Generic`'s signature has the GENERIC calling convention, whose type-parameter count
        // precedes the parameter count. An instantiation, because modifiers are resolved against
        // the method's type arguments.
        var generic = c.GetMethod("Generic").MakeGenericMethod(typeof(int));
        ExpectRequired(generic.GetParameters()[1].GetRequiredCustomModifiers(), typeof(InAttribute), "Generic b");
        ExpectNone(generic.GetParameters()[0].GetRequiredCustomModifiers(), "Generic a");

        ExpectRequired(
            c.GetMethod("ReadonlyReturn").ReturnParameter.GetRequiredCustomModifiers(),
            typeof(InAttribute),
            "ReadonlyReturn return");
        ExpectNone(
            c.GetMethod("ReadonlyReturn").GetParameters()[0].GetRequiredCustomModifiers(),
            "ReadonlyReturn a");

        ExpectRequired(
            c.GetProperty("Init").SetMethod.ReturnParameter.GetRequiredCustomModifiers(),
            typeof(IsExternalInit),
            "Init setter return");

        // A required modifier is not an optional one.
        ExpectNone(
            c.GetMethod("Unmodified").GetParameters()[0].GetOptionalCustomModifiers(),
            "Unmodified a optional");
        ExpectNone(
            c.GetMethod("Unmodified").GetParameters()[1].GetRequiredCustomModifiers(),
            "Unmodified b");

        return failures;
    }
}
