using System;
using System.Reflection;
using System.Runtime.CompilerServices;

// `Assembly.GetExportedTypes()` and `Assembly.ExportedTypes`, which reach the
// `AssemblyNative_GetExportedTypes` QCall. CoreCLR walks the TypeDef table from row 2 and keeps
// each row whose enclosers are all `NestedPublic` up to a `Public` top-level type, in row order;
// then each ExportedType row implemented by a File of this assembly (a single-source guest has
// none); and returns a `Type[]`. The forwarders below are ExportedType rows too, and are never
// kept.
[assembly: TypeForwardedTo(typeof(System.Text.StringBuilder))]
[assembly: TypeForwardedTo(typeof(System.Environment))]
[assembly: TypeForwardedTo(typeof(System.Collections.Generic.Dictionary<,>))]

public class PublicTop
{
    public class NestedPublic
    {
        public class NestedPublicDeep { }
        protected class DeepProtected { }
    }
    protected class NestedProtected { public class InsideProtected { } }
    internal class NestedInternal { public class InsideInternal { } }
    protected internal class NestedProtectedInternal { }
    private protected class NestedPrivateProtected { }
    private class NestedPrivate { public class InsidePrivate { } }
    public class NestedGeneric<T> { public class InnerOfGeneric { } }
    public delegate void NestedDelegate();
    public enum NestedEnum { A }
    public interface INested { }
    public struct NestedStruct { }
}

internal class InternalTop
{
    public class NestedPublicInInternal { public class Deeper { } }
}

public class GenericTop<T, U> { public class Nested<V> { } }
public static class StaticTop { }
public interface IPublic { }
public struct PublicStruct { }
public enum PublicEnum { X }
public delegate int PublicDelegate(int x);
public abstract class AbstractTop { }
public sealed class SealedTop { }

namespace Ns.Inner
{
    public class InNamespace { public class NestedInNamespace { } }
    class InternalInNamespace { }
}

file class FileLocal { }

// The witness for "loading a type is not initialising it". It lives on a different type from the
// initialiser that writes it, so that reading the witness cannot itself be what triggers that
// initialiser.
public static class Witness
{
    public static bool Ran;
}

public class HasCctor
{
    static HasCctor()
    {
        Witness.Ran = true;
    }

    public static int Value = 1;
}

public class Program
{
    // In ascending TypeDef row order: Roslyn writes the top-level types in source order, then the
    // nested ones level by level.
    static readonly string[] Expected =
    {
        "PublicTop",
        "GenericTop`2",
        "StaticTop",
        "IPublic",
        "PublicStruct",
        "PublicEnum",
        "PublicDelegate",
        "AbstractTop",
        "SealedTop",
        "Witness",
        "HasCctor",
        "Program",
        "Ns.Inner.InNamespace",
        "PublicTop+NestedPublic",
        "PublicTop+NestedGeneric`1",
        "PublicTop+NestedDelegate",
        "PublicTop+NestedEnum",
        "PublicTop+INested",
        "PublicTop+NestedStruct",
        "GenericTop`2+Nested`1",
        "Ns.Inner.InNamespace+NestedInNamespace",
        "PublicTop+NestedPublic+NestedPublicDeep",
        "PublicTop+NestedGeneric`1+InnerOfGeneric",
    };

    // Whether `exported` is exactly the elements of `all` that are visible, in the same order.
    static bool IsVisibleSubsequence(Type[] exported, Type[] all)
    {
        int next = 0;

        for (int i = 0; i < all.Length; i++)
        {
            if (!all[i].IsVisible)
            {
                continue;
            }

            if (next >= exported.Length || !ReferenceEquals(exported[next], all[i]))
            {
                return false;
            }

            next++;
        }

        return next == exported.Length;
    }

    public static int Main(string[] args)
    {
        Assembly assembly = typeof(Program).Assembly;
        Type[] types = assembly.GetExportedTypes();

        // Loading a type is not initialising it.
        if (Witness.Ran)
        {
            return 14;
        }

        if (types.Length != Expected.Length)
        {
            return 1;
        }

        for (int i = 0; i < types.Length; i++)
        {
            if (types[i].FullName != Expected[i])
            {
                return 2;
            }
        }

        // `System.Type[]`, not the `RuntimeType[]` that `GetTypes` returns.
        if (types.GetType() != typeof(Type[]))
        {
            return 3;
        }

        // A fresh array each call, holding the same objects.
        Type[] again = assembly.GetExportedTypes();

        if (ReferenceEquals(types, again))
        {
            return 4;
        }

        for (int i = 0; i < types.Length; i++)
        {
            if (!ReferenceEquals(types[i], again[i]))
            {
                return 5;
            }
        }

        // The same object `typeof` gives.
        if (!ReferenceEquals(types[0], typeof(PublicTop)))
        {
            return 6;
        }

        // `ExportedTypes` is the same sequence.
        int count = 0;

        foreach (Type t in assembly.ExportedTypes)
        {
            if (count >= types.Length || !ReferenceEquals(t, types[count]))
            {
                return 7;
            }

            count++;
        }

        if (count != types.Length)
        {
            return 7;
        }

        // A generic row is its open definition, including a type nested in a generic.
        if (!typeof(GenericTop<,>).IsGenericTypeDefinition
            || !ReferenceEquals(types[1], typeof(GenericTop<,>))
            || !types[14].IsGenericTypeDefinition
            || !types[19].IsGenericTypeDefinition
            || !types[22].IsGenericTypeDefinition)
        {
            return 8;
        }

        // No forwarded type, nor any of a forwarded type's nested types, is kept.
        for (int i = 0; i < types.Length; i++)
        {
            if (!ReferenceEquals(types[i].Assembly, assembly))
            {
                return 9;
            }

            string name = types[i].Name;

            if (name == "StringBuilder" || name == "Environment" || name == "Dictionary`2"
                || name == "Enumerator" || name == "KeyCollection" || name == "ValueCollection"
                || name == "ChunkEnumerator" || name == "SpecialFolder" || name == "SpecialFolderOption")
            {
                return 9;
            }
        }

        // A facade whose types are all forwarders: an empty array, not null.
        Type[] facade = Assembly.Load("System.Runtime").GetExportedTypes();

        if (facade == null)
        {
            return 10;
        }

        if (facade.Length != 0)
        {
            return 11;
        }

        // For a single-module assembly whose types all load, the exported types are the visible
        // ones of `GetTypes`, in the same order.
        if (!IsVisibleSubsequence(types, assembly.GetTypes()))
        {
            return 12;
        }

        Assembly coreLib = typeof(object).Assembly;

        if (!IsVisibleSubsequence(coreLib.GetExportedTypes(), coreLib.GetTypes()))
        {
            return 13;
        }

        // ... and the witness above says that because no initialiser ran, not because nothing
        // would ever have written it. Touching a static of `HasCctor` runs its initialiser now.
        if (Witness.Ran)
        {
            return 15;
        }

        if (HasCctor.Value != 1)
        {
            return 16;
        }

        if (!Witness.Ran)
        {
            return 17;
        }

        return 0;
    }
}
