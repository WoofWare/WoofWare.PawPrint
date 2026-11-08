using System;
using System.Collections.Generic;
using System.Reflection;

// `Module.ResolveType` with a generic context: CoreCLR's `ModuleHandle_ResolveType` builds a
// `SigTypeContext` from the caller's arrays, and only a TypeSpec's signature reads it. A TypeDef or a
// TypeRef names its type outright, so whatever the arrays hold, type variables or an open generic
// definition included, the answer is the type itself. `Module.ResolveMethod` passes such arrays on to
// `ResolveType` for a method of a generic type, which is how `Reflection.Emit` reaches this when it
// refers to a generic method of a generic instantiation (`RuntimeModuleBuilder`'s
// `GetGenericMethodBaseDefinition`).
//
// Exit code is the index of the first failing check, so a failure names itself.

public class Plain { }

public class Holder<T>
{
    public List<T> Make() => new List<T>();

    public U Convert<U>(T value) => default(U);
}

public static class Program
{
    public static int Main()
    {
        Module mod = typeof(Holder<>).Module;
        Type t = typeof(Holder<>).GetGenericArguments()[0];
        Type u = typeof(Holder<>).GetMethod("Convert").GetGenericArguments()[0];
        Type[] typeVariables = new Type[] { t };
        Type[] methodVariables = new Type[] { u };

        // A TypeDef, with type variables as the context.
        if (mod.ResolveType(typeof(Plain).MetadataToken, typeVariables, methodVariables) != typeof(Plain)) return 1;

        // A generic TypeDef, with its own type variable and a method's as the context: the generic
        // definition, not an instantiation over them.
        if (mod.ResolveType(typeof(Holder<>).MetadataToken, typeVariables, methodVariables) != typeof(Holder<>))
            return 2;

        // An open generic definition as the context, which no signature could be instantiated over.
        Type[] open = new Type[] { typeof(Holder<>) };
        if (mod.ResolveType(typeof(Plain).MetadataToken, open, open) != typeof(Plain)) return 3;

        // A TypeRef: this module's reference to `List<>`, found by resolving each TypeRef in turn.
        int listRef = 0;
        for (int rid = 1; listRef == 0; rid++)
        {
            Type referenced;
            try
            {
                referenced = mod.ResolveType(0x01000000 | rid);
            }
            catch (ArgumentOutOfRangeException)
            {
                break;
            }

            if (referenced == typeof(List<>)) listRef = 0x01000000 | rid;
        }

        if (listRef == 0) return 4;
        if (mod.ResolveType(listRef, typeVariables, methodVariables) != typeof(List<>)) return 5;
        if (mod.ResolveType(listRef, open, open) != typeof(List<>)) return 6;

        // A TypeSpec does read the context: `Make`'s `List<!0>`, the one TypeSpec that resolves to
        // `List<int>` under `int` and to `List<string>` under `string`.
        int listOfT = 0;
        for (int rid = 1; listOfT == 0; rid++)
        {
            int token = 0x1B000000 | rid;
            Type underInt;
            Type underString;
            try
            {
                underInt = mod.ResolveType(token, new Type[] { typeof(int) }, null);
                underString = mod.ResolveType(token, new Type[] { typeof(string) }, null);
            }
            catch (ArgumentOutOfRangeException)
            {
                break;
            }

            if (underInt == typeof(List<int>) && underString == typeof(List<string>)) listOfT = token;
        }

        if (listOfT == 0) return 7;

        // What `GetGenericMethodBaseDefinition` asks for `Holder<int>.Convert<long>`: the method's
        // token, with its generic definition's declaring type's arguments and its own type variables as
        // the context. `ResolveMethod` resolves the declaring TypeDef with that context, and answers
        // with the method on the generic definition.
        MethodInfo convertOfLong = typeof(Holder<int>).GetMethod("Convert").MakeGenericMethod(typeof(long));
        MethodInfo convertDefinition = convertOfLong.GetGenericMethodDefinition();
        MethodBase resolved = mod.ResolveMethod(
            convertOfLong.MetadataToken,
            convertDefinition.DeclaringType.GetGenericArguments(),
            convertDefinition.GetGenericArguments());
        if (!ReferenceEquals(resolved, typeof(Holder<>).GetMethod("Convert"))) return 8;

        return 0;
    }
}
