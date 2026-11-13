using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

// A type name holding a character that type names reserve (`, [ ] & * + \`), as reflection renders
// it: `FullName`, `Name`, `ToString()` and `AssemblyQualifiedName` put a backslash before each such
// character, in the namespace and the name alike, and wherever the type appears inside another
// type's name. `Namespace` is not escaped. No C# source can spell such a name, so these types come
// from `ModuleBuilder.DefineType`. Measured on .NET 10.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Outer
{
    public class Inner<T>
    {
    }
}

public static class Program
{
    const string DynSuffix = ", Dyn, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null";

    public static void Generic<T> ()
    {
    }

    static int failed;

    static void Check (int code, string? actual, string? expected)
    {
        if (failed == 0 && actual != expected)
        {
            Console.WriteLine ($"check {code}: expected <{expected}>, got <{actual}>");
            failed = code;
        }
    }

    static Type Create (ModuleBuilder module, string name)
    {
        TypeBuilder builder = module.DefineType (name, TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        return builder.CreateType ();
    }

    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Dyn");

        // Each reserved character in turn, in a name with no namespace.
        string reserved = ",[]&*+\\";
        Type? plus = null;
        for (int i = 0; i < reserved.Length; i++)
        {
            Type created = Create (module, "Odd" + reserved[i] + "One");
            string escaped = "Odd\\" + reserved[i] + "One";
            int code = 10 * (i + 1);
            Check (code + 1, created.FullName, escaped);
            Check (code + 2, created.Name, escaped);
            Check (code + 3, created.ToString (), escaped);
            Check (code + 4, created.AssemblyQualifiedName, escaped + DynSuffix);
            if (reserved[i] == '+')
            {
                plus = created;
            }
        }

        if (plus is null)
        {
            return 1;
        }

        // A reserved character in the namespace is escaped too, though `Namespace` is not.
        Type spaced = Create (module, "Name+Space.Plain");
        Check (101, spaced.FullName, "Name\\+Space.Plain");
        Check (102, spaced.Name, "Plain");
        Check (103, spaced.ToString (), "Name\\+Space.Plain");
        Check (104, spaced.Namespace, "Name+Space");

        Type both = Create (module, "A[0].B&C");
        Check (111, both.FullName, "A\\[0\\].B\\&C");
        Check (112, both.Name, "B\\&C");
        Check (113, both.ToString (), "A\\[0\\].B\\&C");
        Check (114, both.Namespace, "A[0]");

        // The element of an array.
        Check (121, plus.MakeArrayType ().FullName, "Odd\\+One[]");
        Check (122, plus.MakeArrayType ().Name, "Odd\\+One[]");
        Check (126, plus.MakeArrayType ().AssemblyQualifiedName, "Odd\\+One[]" + DynSuffix);

        // A generic argument, rendered in full under `FullName` and by name under `ToString()`.
        Type list = typeof (List<>).MakeGenericType (plus);
        Check (131, list.FullName, "System.Collections.Generic.List`1[[Odd\\+One" + DynSuffix + "]]");
        Check (132, list.ToString (), "System.Collections.Generic.List`1[Odd\\+One]");
        Check (133, list.Name, "List`1");

        Type dictionary = typeof (Dictionary<,>).MakeGenericType (plus, both);
        Check (141, dictionary.FullName, "System.Collections.Generic.Dictionary`2[[Odd\\+One" + DynSuffix + "],[A\\[0\\].B\\&C" + DynSuffix + "]]");
        Check (142, dictionary.ToString (), "System.Collections.Generic.Dictionary`2[Odd\\+One,A\\[0\\].B\\&C]");

        Type listOfArray = typeof (List<>).MakeGenericType (plus.MakeArrayType ());
        Check (151, listOfArray.ToString (), "System.Collections.Generic.List`1[Odd\\+One[]]");

        // The `+` between a nested type and the type enclosing it is not escaped; the argument is.
        Type inner = typeof (Outer.Inner<>).MakeGenericType (plus);
        Check (161, inner.FullName, "Outer+Inner`1[[Odd\\+One" + DynSuffix + "]]");
        Check (162, inner.ToString (), "Outer+Inner`1[Odd\\+One]");

        // A generic method's instantiation.
        MethodInfo generic = typeof (Program).GetMethod ("Generic")!;
        Check (171, generic.MakeGenericMethod (plus).ToString (), "Void Generic[Odd\\+One]()");
        Check (172, generic.MakeGenericMethod (both).ToString (), "Void Generic[B\\&C]()");

        return failed;
    }
}
