using System;
using System.Collections.Generic;
using System.Reflection;

class Sample
{
    public unsafe void Pointers(int* value, int** twice, ref int* byRef) { }

    public class Nested<T>
    {
    }

    public ref struct RefStruct
    {
        public int Field;
    }
}

static class Program
{
    static unsafe int Main()
    {
        // `Type.MakePointerType` on a RuntimeType is the `RuntimeTypeHandle_MakePointer` QCall.
        Type intPointer = typeof(int).MakePointerType();
        if (!intPointer.IsPointer) return 1;
        if (!intPointer.HasElementType) return 2;
        if (intPointer.IsByRef || intPointer.IsArray) return 3;
        if (intPointer.GetElementType() != typeof(int)) return 4;
        if (intPointer.Name != "Int32*") return 5;
        if (intPointer.FullName != "System.Int32*") return 6;
        if (intPointer.IsValueType) return 7;
        if (intPointer.BaseType != null) return 8;
        if (intPointer.IsGenericType || intPointer.ContainsGenericParameters) return 9;

        // One RuntimeType per type handle: the pointer reached through `typeof`, through reflection
        // on an `int*` parameter, and through a second call are all the same object.
        ParameterInfo[] parameters = typeof(Sample).GetMethod("Pointers").GetParameters();
        if (!ReferenceEquals(intPointer, typeof(int*))) return 10;
        if (!ReferenceEquals(intPointer, parameters[0].ParameterType)) return 11;
        if (!ReferenceEquals(intPointer, typeof(int).MakePointerType())) return 12;

        // A pointer to a pointer, and a byref over a pointer.
        Type twice = intPointer.MakePointerType();
        if (twice.Name != "Int32**") return 13;
        if (!ReferenceEquals(twice, typeof(int**))) return 14;
        if (!ReferenceEquals(twice, parameters[1].ParameterType)) return 15;
        if (!ReferenceEquals(intPointer.MakeByRefType(), parameters[2].ParameterType)) return 16;

        // `void*` is the pointer type a signature spells most often.
        Type voidPointer = typeof(void).MakePointerType();
        if (voidPointer.FullName != "System.Void*") return 17;
        if (!ReferenceEquals(voidPointer, typeof(void*))) return 18;

        Type stringPointer = typeof(string).MakePointerType();
        if (stringPointer.FullName != "System.String*") return 19;
        if (stringPointer.GetElementType() != typeof(string)) return 20;

        Type arrayPointer = typeof(int[,]).MakePointerType();
        if (arrayPointer.Name != "Int32[,]*") return 21;
        if (arrayPointer.GetElementType() != typeof(int[,])) return 22;
        if (arrayPointer.IsArray) return 23;

        Type nestedPointer = typeof(Sample.Nested<int>).MakePointerType();
        if (nestedPointer.FullName != typeof(Sample.Nested<int>).FullName + "*") return 24;
        if (nestedPointer.IsGenericType) return 25;

        Type fnptrPointer = typeof(delegate*<int, string>).MakePointerType();
        if (!fnptrPointer.IsPointer) return 26;
        if (!ReferenceEquals(fnptrPointer, typeof(delegate*<int, string>*))) return 27;

        // Unlike an array, a pointer may be built over a byref-like type: the runtime leaves it to
        // languages to restrict such pointers.
        Type spanPointer = typeof(Span<int>).MakePointerType();
        if (!spanPointer.IsPointer) return 28;
        if (spanPointer.GetElementType() != typeof(Span<int>)) return 29;
        if (spanPointer.IsByRefLike) return 30;
        Type refStructPointer = typeof(Sample.RefStruct).MakePointerType();
        if (refStructPointer.FullName != "Sample+RefStruct*") return 31;

        // Elements that are not closed: an open definition and a type variable. Reflection reports
        // the variable itself beneath the pointer.
        Type listDefinition = typeof(List<>);
        Type openPointer = listDefinition.MakePointerType();
        if (openPointer.Name != "List`1*") return 32;
        if (!openPointer.ContainsGenericParameters) return 33;
        if (!ReferenceEquals(openPointer.GetElementType(), listDefinition)) return 34;
        Type variable = listDefinition.GetGenericArguments()[0];
        Type variablePointer = variable.MakePointerType();
        if (variablePointer.Name != "T*") return 35;
        if (!variablePointer.ContainsGenericParameters) return 36;
        if (!ReferenceEquals(variablePointer.GetElementType(), variable)) return 37;
        if (!ReferenceEquals(variablePointer, variable.MakePointerType())) return 38;

        // The one input the type loader refuses: a pointer to a byref. The message names the byref
        // being wrapped and the assembly that declares its element; `TypeName` carries the same
        // type string.
        string coreLib = typeof(int).Assembly.FullName;
        string here = typeof(Sample).Assembly.FullName;
        if (PointerToByRef(typeof(int).MakeByRefType(), "System.Int32&", coreLib) is int r1) return 100 + r1;
        if (PointerToByRef(typeof(Sample.Nested<int>).MakeByRefType(), "Sample+Nested`1[System.Int32]&", here) is int r2) return 110 + r2;
        if (PointerToByRef(typeof(int*).MakeByRefType(), "System.Int32*&", coreLib) is int r3) return 120 + r3;
        if (PointerToByRef(typeof(void).MakeByRefType(), "System.Void&", coreLib) is int r4) return 130 + r4;
        if (PointerToByRef(typeof(Span<int>).MakeByRefType(), "System.Span`1[System.Int32]&", coreLib) is int r5) return 140 + r5;
        if (PointerToByRef(typeof(delegate*<int, string>).MakeByRefType(), "System.String(System.Int32)&", coreLib) is int r6) return 150 + r6;

        return 0;
    }

    // Null when the call behaves as CoreCLR does; otherwise a small code saying which check failed.
    static int? PointerToByRef(Type byRef, string expectedTypeName, string expectedAssembly)
    {
        try
        {
            byRef.MakePointerType();
            return 1;
        }
        catch (TypeLoadException e)
        {
            string expected = $"Could not create a pointer to a ByRef. Type: '{expectedTypeName}'. Assembly: '{expectedAssembly}'.";
            if (e.Message != expected)
            {
                Console.Error.WriteLine($"message: {e.Message}");
                return 2;
            }
            if (e.TypeName != expectedTypeName)
            {
                Console.Error.WriteLine($"TypeName: {e.TypeName}");
                return 3;
            }

            // The mscorrc resource id of the message's format string, IDS_CLASSLOAD_POINTER_OF_BYREF.
            BindingFlags privateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
            object resourceId = typeof(TypeLoadException).GetField("_resourceId", privateInstance).GetValue(e);
            if ((int)resourceId != 0x17b0)
            {
                Console.Error.WriteLine($"_resourceId: {resourceId}");
                return 4;
            }
        }

        return null;
    }
}
