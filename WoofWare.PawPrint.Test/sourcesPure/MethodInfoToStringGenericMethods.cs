using System;
using System.Collections.Generic;
using System.Reflection;

namespace MethodInfoToStringGenericMethods
{
    public class Outer { public class Inner<T> { } public class Plain { } }
    public struct Pair<A, B> { }

    public class G<T>
    {
        public static void M<W>() { }
        public static T Echo<W>(W w, T t) => t;
    }

    public static class Statics
    {
        public static void One<W>() { }
        public static void Two<A, B>() { }
        public static W Ret<W>(W[] xs, List<W> l, ref W r) => r;
        public static void Ref<W>() where W : allows ref struct { }
        public static void NotGeneric() { }
    }

    // `MethodInfo.ToString()` spells a generic method's instantiation through the
    // `RuntimeMethodHandle.ConstructInstantiation` QCall, with `TypeNameFormatFlags.FormatBasic`.
    // CoreCLR renders each argument without its namespace, and so without the instantiation of a
    // generic argument and without the enclosing type of a nested one: `One[List`1]`, `One[Inner`1]`.
    // A generic method definition spells its own type parameters. Every expected string was
    // measured on .NET 10.
    public class Program
    {
        static int failures;

        static void Check(int code, MethodInfo method, string expected)
        {
            string actual = method.ToString();
            if (actual != expected)
            {
                Console.WriteLine("case " + code + ": expected [" + expected + "], got [" + actual + "]");
                if (failures == 0) failures = code;
            }
        }

        static MethodInfo Make(Type declaring, string name, params Type[] arguments) =>
            declaring.GetMethod(name).MakeGenericMethod(arguments);

        public static int Main(string[] args)
        {
            Type s = typeof(Statics);

            Check(1, s.GetMethod("One"), "Void One[W]()");
            Check(2, s.GetMethod("Two"), "Void Two[A,B]()");
            Check(3, s.GetMethod("Ret"), "W Ret[W](W[], System.Collections.Generic.List`1[W], W ByRef)");
            Check(4, Make(s, "One", typeof(string)), "Void One[String]()");
            Check(5, Make(s, "One", typeof(int)), "Void One[Int32]()");
            Check(6, Make(s, "One", typeof(List<string>)), "Void One[List`1]()");
            Check(7, Make(s, "One", typeof(Dictionary<int, List<string>>)), "Void One[Dictionary`2]()");
            Check(8, Make(s, "One", typeof(Outer.Inner<int>)), "Void One[Inner`1]()");
            Check(9, Make(s, "One", typeof(Outer.Plain)), "Void One[Plain]()");
            Check(10, Make(s, "One", typeof(int[])), "Void One[Int32[]]()");
            Check(11, Make(s, "One", typeof(int[,])), "Void One[Int32[,]]()");
            Check(12, Make(s, "One", typeof(string[][])), "Void One[String[][]]()");
            Check(13, Make(s, "One", typeof(List<int>[])), "Void One[List`1[]]()");
            Check(14, Make(s, "One", typeof(int?)), "Void One[Nullable`1]()");
            Check(15, Make(s, "Two", typeof(Pair<int, string>), typeof(object)), "Void Two[Pair`2,Object]()");
            Check(16, Make(s, "Ret", typeof(string)), "System.String Ret[String](System.String[], System.Collections.Generic.List`1[System.String], System.String ByRef)");
            Check(17, Make(s, "Ref", typeof(Span<int>)), "Void Ref[Span`1]()");
            Check(18, typeof(G<int>).GetMethod("M"), "Void M[W]()");
            Check(19, typeof(G<>).GetMethod("M"), "Void M[W]()");
            Check(20, Make(typeof(G<int>), "M", typeof(string)), "Void M[String]()");
            Check(21, Make(typeof(G<>), "M", typeof(string)), "Void M[String]()");
            Check(22, typeof(G<>).GetMethod("Echo"), "T Echo[W](W, T)");
            Check(23, Make(typeof(G<long>), "Echo", typeof(byte)), "Int64 Echo[Byte](Byte, Int64)");
            Check(24, s.GetMethod("NotGeneric"), "Void NotGeneric()");
            Check(25, typeof(List<int>).GetMethod("ConvertAll"), "System.Collections.Generic.List`1[TOutput] ConvertAll[TOutput](System.Converter`2[System.Int32,TOutput])");
            Check(26, Make(typeof(Array), "Empty", typeof(string)), "System.String[] Empty[String]()");

            return failures;
        }
    }
}
