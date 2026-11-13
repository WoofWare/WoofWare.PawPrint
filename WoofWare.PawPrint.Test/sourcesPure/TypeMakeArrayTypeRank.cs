using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;

// An array's ComponentSize is a UInt16, so 65535 is the largest value-type element it may have.
[StructLayout(LayoutKind.Sequential, Size = 65535)]
public struct AtLimit { }

[StructLayout(LayoutKind.Sequential, Size = 65536)]
public struct OverLimit { }

class Sample
{
    public void Arrays(int[,] rank2, string[,,] rank3) { }

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
        // `Type.MakeArrayType(rank)` on a RuntimeType is the `RuntimeTypeHandle_MakeArray` QCall,
        // for every rank: a rank-1 `ELEMENT_TYPE_ARRAY` is `int[*]`, a different type from the
        // szarray `int[]` that `MakeArrayType()` builds.
        Type rank2 = typeof(int).MakeArrayType(2);
        if (!rank2.IsArray) return 1;
        if (rank2.IsSZArray) return 2;
        if (rank2.GetArrayRank() != 2) return 3;
        if (!rank2.HasElementType) return 4;
        if (rank2.IsByRef || rank2.IsPointer) return 5;
        if (rank2.GetElementType() != typeof(int)) return 6;
        if (rank2.Name != "Int32[,]") return 7;
        if (rank2.FullName != "System.Int32[,]") return 8;
        if (rank2.IsValueType) return 9;
        if (rank2.BaseType != typeof(Array)) return 10;
        if (rank2.IsGenericType || rank2.ContainsGenericParameters) return 11;

        // One RuntimeType per type handle.
        ParameterInfo[] parameters = typeof(Sample).GetMethod("Arrays").GetParameters();
        if (!ReferenceEquals(rank2, typeof(int[,]))) return 12;
        if (!ReferenceEquals(rank2, parameters[0].ParameterType)) return 13;
        if (!ReferenceEquals(rank2, typeof(int).MakeArrayType(2))) return 14;
        if (!ReferenceEquals(rank2, Type.GetType("System.Int32[,]"))) return 15;

        Type rank3 = typeof(string).MakeArrayType(3);
        if (rank3.FullName != "System.String[,,]") return 16;
        if (!ReferenceEquals(rank3, parameters[1].ParameterType)) return 17;
        if (rank3 == typeof(string).MakeArrayType(2)) return 18;

        Type rank1 = typeof(int).MakeArrayType(1);
        if (!rank1.IsArray) return 20;
        if (rank1.IsSZArray) return 21;
        if (rank1.GetArrayRank() != 1) return 22;
        if (rank1.Name != "Int32[*]") return 23;
        if (rank1.FullName != "System.Int32[*]") return 24;
        if (rank1 == typeof(int[])) return 25;
        if (rank1.GetElementType() != typeof(int)) return 26;
        if (!ReferenceEquals(rank1, typeof(int).MakeArrayType(1))) return 27;

        // MAX_RANK, the most dimensions an array type can have.
        Type rank32 = typeof(int).MakeArrayType(32);
        if (rank32.GetArrayRank() != 32) return 30;
        if (rank32.Name != "Int32[" + new string(',', 31) + "]") return 31;

        // Arrays over other shapes.
        Type overSz = typeof(int[]).MakeArrayType(2);
        if (overSz.Name != "Int32[][,]") return 40;
        if (overSz.GetElementType() != typeof(int[])) return 41;
        Type overPointer = typeof(int*).MakeArrayType(2);
        if (!ReferenceEquals(overPointer, typeof(int*[,]))) return 42;
        Type overFnptr = typeof(delegate*<int, string>).MakeArrayType(2);
        if (!ReferenceEquals(overFnptr, typeof(delegate*<int, string>[,]))) return 43;
        Type overNested = typeof(Sample.Nested<int>).MakeArrayType(2);
        if (!ReferenceEquals(overNested, typeof(Sample.Nested<int>[,]))) return 44;
        if (typeof(AtLimit).MakeArrayType(2) != typeof(AtLimit[,])) return 45;

        // Elements that are not closed.
        Type listDefinition = typeof(List<>);
        Type openArray = listDefinition.MakeArrayType(2);
        if (openArray.Name != "List`1[,]") return 50;
        if (!openArray.ContainsGenericParameters) return 51;
        if (!ReferenceEquals(openArray.GetElementType(), listDefinition)) return 52;
        Type variable = listDefinition.GetGenericArguments()[0];
        Type variableArray = variable.MakeArrayType(2);
        if (variableArray.Name != "T[,]") return 53;
        if (!ReferenceEquals(variableArray.GetElementType(), variable)) return 54;

        // A rank the managed wrapper refuses before reaching the runtime.
        if (!ThrowsIndexOutOfRange(0)) return 60;
        if (!ThrowsIndexOutOfRange(-1)) return 61;

        // The type loader's refusals, in its order: a byref, a byref-like type, `void`, then a rank
        // above MAX_RANK, then (when the array's MethodTable is built) an oversized value type.
        string coreLib = typeof(int).Assembly.FullName;
        string here = typeof(Sample).Assembly.FullName;

        // For a byref element the rendered name is the byref alone.
        if (Refused(typeof(int).MakeByRefType(), 2, "System.Int32&", coreLib, ElementMessage("System.Int32&", coreLib, "ByRef"), 0x1775) is int r1) return 100 + r1;
        if (Refused(typeof(int).MakeByRefType(), 1, "System.Int32&", coreLib, ElementMessage("System.Int32&", coreLib, "ByRef"), 0x1775) is int r2) return 110 + r2;
        if (Refused(typeof(int).MakeByRefType(), 33, "System.Int32&", coreLib, ElementMessage("System.Int32&", coreLib, "ByRef"), 0x1775) is int r3) return 120 + r3;

        if (Refused(typeof(Span<int>), 2, "System.Span`1[System.Int32][,]", coreLib, ElementMessage("System.Span`1[System.Int32][,]", coreLib, "ByRef-like"), 0x1776) is int r4) return 130 + r4;
        if (Refused(typeof(Sample.RefStruct), 1, "Sample+RefStruct[*]", here, ElementMessage("Sample+RefStruct[*]", here, "ByRef-like"), 0x1776) is int r5) return 140 + r5;

        if (Refused(typeof(void), 2, "System.Void[,]", coreLib, ElementMessage("System.Void[,]", coreLib, "System.Void"), 0x1777) is int r6) return 150 + r6;
        if (Refused(typeof(void), 1, "System.Void[*]", coreLib, ElementMessage("System.Void[*]", coreLib, "System.Void"), 0x1777) is int r7) return 160 + r7;

        string rank33 = "[" + new string(',', 32) + "]";
        if (Refused(typeof(int), 33, "System.Int32" + rank33, coreLib, TooManyDimensions("System.Int32" + rank33, coreLib), 0x1785) is int r8) return 170 + r8;
        if (Refused(typeof(Sample.Nested<int>), 40, "Sample+Nested`1[System.Int32][" + new string(',', 39) + "]", here, TooManyDimensions("Sample+Nested`1[System.Int32][" + new string(',', 39) + "]", here), 0x1785) is int r9) return 180 + r9;
        // A void element is refused for being void before its rank is looked at, and an oversized
        // element only once the rank has been accepted.
        if (Refused(typeof(void), 33, "System.Void" + rank33, coreLib, ElementMessage("System.Void" + rank33, coreLib, "System.Void"), 0x1777) is int r10) return 190 + r10;
        if (Refused(typeof(OverLimit), 33, "OverLimit" + rank33, here, TooManyDimensions("OverLimit" + rank33, here), 0x1785) is int r11) return 200 + r11;
        // Above rank 64 the type-key renderer spells the rank as a number instead of commas
        // (`TypeNameBuilder::AddArray`), which also keeps the name short for any rank.
        string rank64 = "[" + new string(',', 63) + "]";
        if (Refused(typeof(int), 64, "System.Int32" + rank64, coreLib, TooManyDimensions("System.Int32" + rank64, coreLib), 0x1785) is int r14) return 230 + r14;
        if (Refused(typeof(int), 65, "System.Int32[65]", coreLib, TooManyDimensions("System.Int32[65]", coreLib), 0x1785) is int r15) return 240 + r15;
        if (Refused(typeof(int), int.MaxValue, "System.Int32[2147483647]", coreLib, TooManyDimensions("System.Int32[2147483647]", coreLib), 0x1785) is int r16) return 250 + r16;
        if (Refused(typeof(void), 65, "System.Void[65]", coreLib, ElementMessage("System.Void[65]", coreLib, "System.Void"), 0x1777) is int r17) return 260 + r17;

        // The size refusal names the element rather than the array.
        if (Refused(typeof(OverLimit), 2, "OverLimit", here, $"Array of type 'OverLimit' from assembly '{here}' cannot be created because base value type is too large.", 0x177b) is int r12) return 210 + r12;
        if (Refused(typeof(OverLimit), 1, "OverLimit", here, $"Array of type 'OverLimit' from assembly '{here}' cannot be created because base value type is too large.", 0x177b) is int r13) return 220 + r13;

        return 0;
    }

    static string ElementMessage(string typeName, string assembly, string why) =>
        $"Could not create array type '{typeName}' from assembly '{assembly}' because the element type is {why}.";

    static string TooManyDimensions(string typeName, string assembly) =>
        $"'{typeName}' from assembly '{assembly}' has too many dimensions.";

    static bool ThrowsIndexOutOfRange(int rank)
    {
        try
        {
            typeof(int).MakeArrayType(rank);
            return false;
        }
        catch (IndexOutOfRangeException)
        {
            return true;
        }
    }

    // Null when the call behaves as CoreCLR does; otherwise a small code saying which check failed.
    static int? Refused(Type element, int rank, string expectedTypeName, string expectedAssembly, string expectedMessage, int expectedResourceId)
    {
        try
        {
            element.MakeArrayType(rank);
            return 1;
        }
        catch (TypeLoadException e)
        {
            if (e.Message != expectedMessage)
            {
                Console.Error.WriteLine($"message: {e.Message}");
                return 2;
            }
            if (e.TypeName != expectedTypeName)
            {
                Console.Error.WriteLine($"TypeName: {e.TypeName}");
                return 3;
            }

            BindingFlags privateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
            object resourceId = typeof(TypeLoadException).GetField("_resourceId", privateInstance).GetValue(e);
            if ((int)resourceId != expectedResourceId)
            {
                Console.Error.WriteLine($"_resourceId: {resourceId}");
                return 4;
            }
        }

        return null;
    }
}
