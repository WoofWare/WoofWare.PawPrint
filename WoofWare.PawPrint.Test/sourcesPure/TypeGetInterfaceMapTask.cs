using System;
using System.Reflection;
using System.Threading.Tasks;

// `Type.GetInterfaceMap` over an interface whose members return `Task`, `Task<T>` and `ValueTask`.
// .NET 11 gives each such method an async variant, in a slot of its own; `GetInterfaceMap` skips those,
// so the map has one row per declared method, the instance methods in declaration order and then the
// static virtuals, and each row's target is the class's method. The map is the same on .NET 10 and 11.
//
// Measured on real .NET 10 and 11. Returns 0 on success, or the number of the first check that
// failed.

public interface ITask
{
    Task<int> A();
    int B();
    ValueTask C();
    static abstract Task S();
    static abstract int T();
}

public class CTask : ITask
{
    public Task<int> A() => Task.FromResult(1);
    public int B() => 2;
    public ValueTask C() => default;
    public static Task S() => Task.CompletedTask;
    public static int T() => 3;
}

public static class Program
{
    static string Target(MethodInfo m) =>
        m == null ? "null" : m.DeclaringType + "::" + m.Name + " [" + m.ReflectedType + "]";

    // 0 if `actual` is `expected`; otherwise `code`, having printed both.
    static int Check(int code, string actual, string expected)
    {
        if (actual == expected) return 0;
        Console.WriteLine("check " + code + ": expected " + expected + ", got " + actual);
        return code;
    }

    // Whether the map's target at `row` is the method reflection gives for `name` on `CTask`.
    static string IsDeclared(InterfaceMapping map, int row, string name) =>
        (map.TargetMethods[row] == typeof(CTask).GetMethod(name)).ToString();

    public static int Main()
    {
        InterfaceMapping map = typeof(CTask).GetInterfaceMap(typeof(ITask));

        if (map.InterfaceMethods.Length != map.TargetMethods.Length) return 1;

        var rows = new string[map.InterfaceMethods.Length];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = map.InterfaceMethods[i].Name + " -> " + Target(map.TargetMethods[i]);

        int r;
        if ((r = Check(2, string.Join("; ", rows), "A -> CTask::A [CTask]; B -> CTask::B [CTask]; C -> CTask::C [CTask]; S -> CTask::S [CTask]; T -> CTask::T [CTask]")) != 0) return r;
        if ((r = Check(3, IsDeclared(map, 0, "A"), "True")) != 0) return r;
        if ((r = Check(4, IsDeclared(map, 1, "B"), "True")) != 0) return r;
        if ((r = Check(5, IsDeclared(map, 2, "C"), "True")) != 0) return r;
        if ((r = Check(6, IsDeclared(map, 3, "S"), "True")) != 0) return r;
        if ((r = Check(7, IsDeclared(map, 4, "T"), "True")) != 0) return r;

        return 0;
    }
}
