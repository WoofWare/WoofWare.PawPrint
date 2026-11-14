using System;
using System.Collections.Generic;
using System.Reflection;

// `Type.IsVisible` of a `RuntimeType` is the `RuntimeTypeHandle_IsVisible` QCall, CoreCLR's
// `TypeHandle::IsExternallyVisible`: a public top-level type is visible; a nested type is visible
// exactly when it and every encloser out to a public top-level type are nested public; an
// instantiation needs its definition and every argument visible; an array, pointer or byref asks
// its element; a function pointer asks its return type and every parameter type; a generic
// parameter is always visible. Every answer below was measured on .NET 10 and 11.
//
// Returns 0 on success, or the number of the first check that failed.

public class Pub
{
    public class NPub
    {
        public class NPubPub { }
        protected class NFamInNPub { }
    }

    protected class NFam
    {
        public class NFamPub { }
    }

    internal class NAsm
    {
        public class NAsmPub { }
    }

    protected internal class NFamOrAsm { }
    private protected class NFamAndAsm { }

    private class NPriv
    {
        public class NPrivPub { }
    }

    public class G<T>
    {
        public class Inner { }
    }

    public static void M<U>() { }
}

internal class Internal
{
    public class NPub { }
}

public class PubG<T> { }
internal class InternalG<T> { }
public struct PubS { }
internal struct IntS { }
public interface IPub { }
internal interface IInt { }
public enum PubE { A }
public delegate void PubD();
internal delegate void IntD();

public unsafe class FnHolder<T>
{
    public static void M(delegate*<T, void> p) { }
    internal static void N(delegate*<T, Internal> p) { }
}

public static unsafe class Program
{
    static int failed;
    static int check;

    static void Expect(Type t, bool visible)
    {
        check++;
        if (failed == 0 && t.IsVisible != visible)
        {
            failed = check;
        }
    }

    static Type Nested(Type outer, string name)
    {
        return outer.GetNestedType(name, BindingFlags.Public | BindingFlags.NonPublic);
    }

    public static int Main()
    {
        // Top-level types of each visibility, and CoreLib's.
        Expect(typeof(Pub), true); // 1
        Expect(typeof(Internal), false);
        Expect(typeof(PubS), true);
        Expect(typeof(IPub), true);
        Expect(typeof(IInt), false); // 5
        Expect(typeof(PubE), true);
        Expect(typeof(PubD), true);
        Expect(typeof(IntD), false);
        Expect(typeof(int), true);
        Expect(typeof(object), true); // 10
        Expect(typeof(void), true);
        Expect(typeof(TypedReference), true);
        Expect(typeof(PubS?), true);
        Expect(Type.GetType("System.RuntimeType"), false);

        // Nesting: every level out to a public top-level type must be nested public.
        Expect(typeof(Pub.NPub), true); // 15
        Expect(typeof(Pub.NPub.NPubPub), true);
        Expect(Nested(typeof(Pub.NPub), "NFamInNPub"), false);
        Expect(Nested(typeof(Pub), "NFam"), false);
        Expect(Nested(Nested(typeof(Pub), "NFam"), "NFamPub"), false);
        Expect(typeof(Pub.NAsm), false); // 20
        Expect(typeof(Pub.NAsm.NAsmPub), false);
        Expect(typeof(Pub.NFamOrAsm), false);
        Expect(Nested(typeof(Pub), "NFamAndAsm"), false);
        Expect(Nested(typeof(Pub), "NPriv"), false);
        Expect(Nested(Nested(typeof(Pub), "NPriv"), "NPrivPub"), false); // 25
        Expect(typeof(Internal.NPub), false);

        // Closed instantiations.
        Expect(typeof(PubG<int>), true);
        Expect(typeof(PubG<Internal>), false);
        Expect(typeof(PubG<PubG<Internal>>), false);
        Expect(typeof(PubG<Internal[]>), false); // 30
        Expect(typeof(InternalG<int>), false);
        Expect(typeof(Pub.G<int>.Inner), true);
        Expect(typeof(Pub.G<Internal>.Inner), false);
        Expect(typeof(Dictionary<int, Internal>), false);
        Expect(typeof(List<>).MakeGenericType(Type.GetType("System.RuntimeType")), false); // 35

        // Generic type definitions, alone and as an argument.
        Expect(typeof(PubG<>), true);
        Expect(typeof(InternalG<>), false);
        Expect(typeof(Pub.G<>.Inner), true);
        Expect(typeof(PubG<>).MakeGenericType(typeof(PubG<>)), true);
        Expect(typeof(PubG<>).MakeGenericType(typeof(InternalG<>)), false); // 40

        // Generic parameters, whatever their owner, and open constructions over them.
        Type t = typeof(PubG<>).GetGenericArguments()[0];
        Type internalT = typeof(InternalG<>).GetGenericArguments()[0];
        Expect(t, true);
        Expect(internalT, true);
        Expect(typeof(Pub).GetMethod("M").GetGenericArguments()[0], true);
        Expect(typeof(PubG<>).MakeGenericType(internalT), true);
        Expect(typeof(InternalG<>).MakeGenericType(t), false); // 45
        Expect(typeof(PubG<>).MakeGenericType(typeof(Dictionary<,>).MakeGenericType(typeof(Internal), t)), false);
        Expect(typeof(PubG<>).MakeGenericType(typeof(Dictionary<,>).MakeGenericType(typeof(Pub), t)), true);
        Expect(typeof(Pub.G<>.Inner).MakeGenericType(t), true);
        Expect(internalT.MakeArrayType(), true);
        Expect(internalT.MakeByRefType(), true); // 50
        Expect(t.MakePointerType(), true);
        Expect(typeof(Dictionary<,>).MakeGenericType(typeof(Internal), t).MakeArrayType(), false);

        // Arrays, pointers and byrefs ask their element.
        Expect(typeof(Pub[]), true);
        Expect(typeof(Pub[][]), true);
        Expect(typeof(Internal[]), false); // 55
        Expect(typeof(Internal[,]), false);
        Expect(typeof(Internal[][]), false);
        Expect(typeof(Pub.NFamOrAsm[]), false);
        Expect(typeof(int*), true);
        Expect(typeof(IntS*), false); // 60
        Expect(typeof(Pub).MakeByRefType(), true);
        Expect(typeof(Internal).MakeByRefType(), false);

        // Function pointers ask their return type and every parameter type.
        Expect(typeof(delegate*<int, void>), true);
        Expect(typeof(delegate*<Internal, void>), false);
        Expect(typeof(delegate*<Internal>), false); // 65
        Expect(typeof(delegate* unmanaged<IntS, int>), false);
        Expect(typeof(FnHolder<>).GetMethod("M").GetParameters()[0].ParameterType, true);
        Expect(
            typeof(FnHolder<>)
                .GetMethod("N", BindingFlags.NonPublic | BindingFlags.Static)
                .GetParameters()[0]
                .ParameterType,
            false);

        return failed;
    }
}
