using System;
using System.Collections.Generic;
using System.Reflection;

namespace MakeGenericConstraintViolationMessages
{
    public interface IMark { }
    public class Base { }
    public class NoCtor { private NoCtor() { } }
    public class S<T> where T : struct { }
    public class C<T> where T : class { }
    public class New<T> where T : new() { }
    public class Iface<T> where T : IMark { }
    public class Bas<T> where T : Base { }
    public class Cmp<T> where T : IComparable<T> { }
    public class CmpArr<T> where T : IComparable<T[]> { }
    public class Ref<T> { }
    public class TU<T, U> where T : U where U : IMark { }
    public class UT<T, U> where U : T { }
    public class Two<A, B> where A : class where B : struct { }
    public class Outer { public class Inner<T> where T : struct { } }

    public class G<T>
    {
        public static void M<W>() where W : struct { }
        public static void WT<W>() where W : T { }
        public static void TwoM<A, B>() where A : class where B : struct { }
        public static void AT<A, B>() where A : T where B : struct { }
        public static void EA<W>() where W : IEnumerable<T[]> { }
    }

    public static class Statics
    {
        public static void M<W>() where W : struct { }
        public static void XY<X, Y>() where X : Y where Y : IMark { }
        public static void CA<W>() where W : IComparable<W[]> { }
    }

    // A constraint violation in `MakeGenericType` or `MakeGenericMethod` reaches the guest as an
    // `ArgumentException` wrapping what the binder threw: a `TypeLoadException` for a type, a
    // `VerificationException` for a method. CoreLib builds the outer message itself, after asking
    // `RuntimeTypeHandle.SatisfiesConstraints` which argument to name. It asks in the context of
    // the generic *definition*, so a constraint naming another parameter is checked against that
    // parameter rather than its argument: the outer exception can name an earlier parameter than
    // the binder did (`TU<string, object>`, `Statics.XY<string, object>`). Every expected string
    // was measured on .NET 10.
    public class Program
    {
        const string Argument = "System.ArgumentException";
        const string TypeLoad = "System.TypeLoadException";
        const string Verification = "System.Security.VerificationException";

        // The whole chain, one line per exception, outermost first.
        static string Describe(Exception e)
        {
            var lines = new List<string>();
            for (var x = e; x != null; x = x.InnerException)
            {
                string extra = x switch
                {
                    ArgumentException a => " ParamName=" + (a.ParamName ?? "<null>"),
                    TypeLoadException t => " TypeName=[" + t.TypeName + "]",
                    _ => ""
                };
                lines.Add(x.GetType().FullName + " HResult=0x" + x.HResult.ToString("X8") + extra + " " + x.Message);
            }
            return string.Join("\n", lines);
        }

        static string Expected(string outer, string innerType, string inner)
        {
            string innerHResult = innerType == TypeLoad ? "0x80131522 TypeName=[]" : "0x8013150D";
            return Argument + " HResult=0x80070057 ParamName=<null> " + outer + "\n"
                + innerType + " HResult=" + innerHResult + " " + inner;
        }

        static string TypeCase(string index, string argument, string definition, string parameter, string innerIndex, string innerArgument, string innerParameter) =>
            Expected(
                "GenericArguments[" + index + "], '" + argument + "', on '" + definition + "' violates the constraint of type '" + parameter + "'.",
                TypeLoad,
                "GenericArguments[" + innerIndex + "], '" + innerArgument + "', on '" + definition + "' violates the constraint of type parameter '" + innerParameter + "'.");

        static string SameTypeCase(string index, string argument, string definition, string parameter) =>
            TypeCase(index, argument, definition, parameter, index, argument, parameter);

        static string MethodCase(string index, string argument, string method, string parameter, string declaringType, string methodName, string innerArgument, string innerParameter) =>
            Expected(
                "GenericArguments[" + index + "], '" + argument + "', on '" + method + "' violates the constraint of type '" + parameter + "'.",
                Verification,
                "Method " + declaringType + "." + methodName + ": type argument '" + innerArgument + "' violates the constraint of type parameter '" + innerParameter + "'.");

        static int failures;

        static void Check(int code, Func<object> make, string expected)
        {
            string actual;
            try
            {
                actual = "returned " + make();
            }
            catch (Exception e)
            {
                actual = Describe(e);
            }

            if (actual != expected)
            {
                Console.WriteLine("case " + code + ":\n  expected:\n" + expected + "\n  actual:\n" + actual);
                if (failures == 0) failures = code;
            }
        }

        static Type T1(Type definition, Type argument) => definition.MakeGenericType(argument);
        static Type T2(Type definition, Type first, Type second) => definition.MakeGenericType(first, second);
        static MethodInfo M(Type declaring, string name, params Type[] arguments) => declaring.GetMethod(name).MakeGenericMethod(arguments);

        public static int Main(string[] args)
        {
            const string ns = "MakeGenericConstraintViolationMessages.";

            Check(1, () => T1(typeof(S<>), typeof(string)), SameTypeCase("0", "System.String", ns + "S`1[T]", "T"));
            Check(2, () => T1(typeof(S<>), typeof(int?)), SameTypeCase("0", "System.Nullable`1[System.Int32]", ns + "S`1[T]", "T"));
            Check(3, () => T1(typeof(C<>), typeof(int)), SameTypeCase("0", "System.Int32", ns + "C`1[T]", "T"));
            Check(4, () => T1(typeof(New<>), typeof(NoCtor)), SameTypeCase("0", ns + "NoCtor", ns + "New`1[T]", "T"));
            Check(5, () => T1(typeof(Iface<>), typeof(string)), SameTypeCase("0", "System.String", ns + "Iface`1[T]", "T"));
            Check(6, () => T1(typeof(Bas<>), typeof(string)), SameTypeCase("0", "System.String", ns + "Bas`1[T]", "T"));
            Check(7, () => T1(typeof(Cmp<>), typeof(object)), SameTypeCase("0", "System.Object", ns + "Cmp`1[T]", "T"));
            Check(8, () => T1(typeof(Cmp<>), typeof(int[])), SameTypeCase("0", "System.Int32[]", ns + "Cmp`1[T]", "T"));
            Check(9, () => T1(typeof(S<>), typeof(List<string>)), SameTypeCase("0", "System.Collections.Generic.List`1[System.String]", ns + "S`1[T]", "T"));
            Check(10, () => T1(typeof(Ref<>), typeof(Span<int>)), SameTypeCase("0", "System.Span`1[System.Int32]", ns + "Ref`1[T]", "T"));
            // The binder refuses `U`; CoreLib, checking `T : U` against the formal `U`, names `T`.
            Check(11, () => T2(typeof(TU<,>), typeof(string), typeof(object)), TypeCase("0", "System.String", ns + "TU`2[T,U]", "T", "1", "System.Object", "U"));
            Check(12, () => T2(typeof(UT<,>), typeof(int), typeof(object)), SameTypeCase("1", "System.Object", ns + "UT`2[T,U]", "U"));
            Check(13, () => T2(typeof(Two<,>), typeof(string), typeof(string)), SameTypeCase("1", "System.String", ns + "Two`2[A,B]", "B"));
            Check(14, () => T2(typeof(Two<,>), typeof(int), typeof(int)), SameTypeCase("0", "System.Int32", ns + "Two`2[A,B]", "A"));
            Check(15, () => T1(typeof(Outer.Inner<>), typeof(string)), SameTypeCase("0", "System.String", ns + "Outer+Inner`1[T]", "T"));
            Check(16, () => T2(typeof(UT<,>), typeof(object), typeof(string)), "returned " + ns + "UT`2[System.Object,System.String]");

            Check(17, () => M(typeof(Statics), "M", typeof(string)), MethodCase("0", "System.String", "Void M[W]()", "W", ns + "Statics", "M", "System.String", "W"));
            // As case 11, for a method: the binder refuses `Y`, CoreLib names `X`.
            Check(18, () => M(typeof(Statics), "XY", typeof(string), typeof(object)), MethodCase("0", "System.String", "Void XY[X,Y]()", "X", ns + "Statics", "XY", "System.Object", "Y"));
            Check(19, () => M(typeof(G<int>), "M", typeof(string)), MethodCase("0", "System.String", "Void M[W]()", "W", ns + "G`1[System.Int32]", "M", "System.String", "W"));
            Check(20, () => M(typeof(G<>), "M", typeof(string)), MethodCase("0", "System.String", "Void M[W]()", "W", ns + "G`1[T]", "M", "System.String", "W"));
            // CoreLib's context for a method is its declaring type as reflected, here `G<string>`.
            Check(21, () => M(typeof(G<string>), "WT", typeof(object)), MethodCase("0", "System.Object", "Void WT[W]()", "W", ns + "G`1[System.String]", "WT", "System.Object", "W"));
            Check(22, () => M(typeof(G<>), "WT", typeof(string)), MethodCase("0", "System.String", "Void WT[W]()", "W", ns + "G`1[T]", "WT", "System.String", "W"));
            Check(23, () => M(typeof(G<int>), "TwoM", typeof(string), typeof(string)), MethodCase("1", "System.String", "Void TwoM[A,B]()", "B", ns + "G`1[System.Int32]", "TwoM", "System.String", "B"));
            Check(24, () => M(typeof(G<int>), "TwoM", typeof(int), typeof(int)), MethodCase("0", "System.Int32", "Void TwoM[A,B]()", "A", ns + "G`1[System.Int32]", "TwoM", "System.Int32", "A"));
            Check(25, () => M(typeof(Statics), "M", typeof(Span<int>)), MethodCase("0", "System.Span`1[System.Int32]", "Void M[W]()", "W", ns + "Statics", "M", "System.Span`1[System.Int32]", "W"));
            Check(26, () => M(typeof(G<object>), "WT", typeof(string)), "returned Void WT[String]()");
            // In `G<object>`'s context `A : T` admits `string`, so CoreLib names `B`, as the binder
            // does; against the formal `T` it would have named `A`.
            Check(27, () => M(typeof(G<object>), "AT", typeof(string), typeof(string)), MethodCase("1", "System.String", "Void AT[A,B]()", "B", ns + "G`1[System.Object]", "AT", "System.String", "B"));

            // Constraints over an array of a parameter: CoreLib reads each against the formal, so
            // the cast relation sees `T[]` with `T` unbound.
            Check(28, () => T1(typeof(CmpArr<>), typeof(string)), SameTypeCase("0", "System.String", ns + "CmpArr`1[T]", "T"));
            Check(29, () => M(typeof(Statics), "CA", typeof(string)), MethodCase("0", "System.String", "Void CA[W]()", "W", ns + "Statics", "CA", "System.String", "W"));
            Check(30, () => M(typeof(G<>), "EA", typeof(List<object[]>)), MethodCase("0", "System.Collections.Generic.List`1[System.Object[]]", "Void EA[W]()", "W", ns + "G`1[T]", "EA", "System.Collections.Generic.List`1[System.Object[]]", "W"));
            Check(31, () => M(typeof(G<object>), "EA", typeof(List<string[]>)), "returned Void EA[List`1]()");

            return failures;
        }
    }
}
