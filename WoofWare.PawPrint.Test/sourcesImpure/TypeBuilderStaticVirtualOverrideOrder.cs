using System;
using System.Reflection;
using System.Reflection.Emit;

// Which of a type's MethodImpl rows a static virtual call runs, when more than one of them matches. CoreCLR resolves
// the call by walking the type's rows (`MethodTable::TryResolveVirtualStaticMethodOnThisType`) and taking the first
// that matches. A dynamic module's rows come from its read-write metadata, which scans them in the order added while
// the module has fewer than 25 MethodImpl rows, and from 25 rows walks a lookup hash that gives them in reverse. So the
// first row added runs below 25 rows, and the last from 25, counted in the module of the type whose rows are walked,
// at the moment of the call. An image on disk is read in RID order whatever its size. Each case is a module of its
// own, and each type is resolved once, after its module's last row, because CoreCLR caches a resolution where it
// makes one. Measured on .NET 10 and 11 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public interface IDef { static virtual int M() => 0; }
public interface IDef2 { static virtual int M2() => 0; }
public interface IVar<in T> { static virtual int M() => 0; }
public interface IFill { int F(); }

// Two rows on one interface, both variance-compatible with `IVar<string>`, in this assembly, which has 34
// MethodImpl rows.
public interface IDisk : IVar<object>, IVar<IComparable>
{
    static int IVar<object>.M() => 1;
    static int IVar<IComparable>.M() => 2;
}
public class CDisk : IDisk { }

public interface IDiskSwapped : IVar<IComparable>, IVar<object>
{
    static int IVar<IComparable>.M() => 2;
    static int IVar<object>.M() => 1;
}
public class CDiskSwapped : IDiskSwapped { }

public class Fill0 : IFill { int IFill.F() => 0; }
public class Fill1 : IFill { int IFill.F() => 1; }
public class Fill2 : IFill { int IFill.F() => 2; }
public class Fill3 : IFill { int IFill.F() => 3; }
public class Fill4 : IFill { int IFill.F() => 4; }
public class Fill5 : IFill { int IFill.F() => 5; }
public class Fill6 : IFill { int IFill.F() => 6; }
public class Fill7 : IFill { int IFill.F() => 7; }
public class Fill8 : IFill { int IFill.F() => 8; }
public class Fill9 : IFill { int IFill.F() => 9; }
public class Fill10 : IFill { int IFill.F() => 10; }
public class Fill11 : IFill { int IFill.F() => 11; }
public class Fill12 : IFill { int IFill.F() => 12; }
public class Fill13 : IFill { int IFill.F() => 13; }
public class Fill14 : IFill { int IFill.F() => 14; }
public class Fill15 : IFill { int IFill.F() => 15; }
public class Fill16 : IFill { int IFill.F() => 16; }
public class Fill17 : IFill { int IFill.F() => 17; }
public class Fill18 : IFill { int IFill.F() => 18; }
public class Fill19 : IFill { int IFill.F() => 19; }
public class Fill20 : IFill { int IFill.F() => 20; }
public class Fill21 : IFill { int IFill.F() => 21; }
public class Fill22 : IFill { int IFill.F() => 22; }
public class Fill23 : IFill { int IFill.F() => 23; }
public class Fill24 : IFill { int IFill.F() => 24; }
public class Fill25 : IFill { int IFill.F() => 25; }
public class Fill26 : IFill { int IFill.F() => 26; }
public class Fill27 : IFill { int IFill.F() => 27; }
public class Fill28 : IFill { int IFill.F() => 28; }
public class Fill29 : IFill { int IFill.F() => 29; }

public static class Caller
{
    public static int CallDef<T>() where T : IDef => T.M();
    public static int CallDefAgain<T>() where T : IDef => T.M();
    public static int CallDef2<T>() where T : IDef2 => T.M2();
    public static int CallVar<T>() where T : IVar<string> => T.M();
}

public static class Program
{
    static int failed = 0;

    static void Check(int number, int actual, int expected)
    {
        if (failed == 0 && actual != expected)
        {
            Console.WriteLine($"check {number}: got {actual}, expected {expected}");
            failed = number;
        }
    }

    static ModuleBuilder NewModule(string name) =>
        AssemblyBuilder.DefineDynamicAssembly(new AssemblyName(name), AssemblyBuilderAccess.Run).DefineDynamicModule(name);

    static MethodBuilder StaticBody(TypeBuilder type, string name, int value)
    {
        MethodBuilder method = type.DefineMethod(name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, typeof(int), Type.EmptyTypes);
        ILGenerator il = method.GetILGenerator();
        il.Emit(OpCodes.Ldc_I4, value);
        il.Emit(OpCodes.Ret);
        return method;
    }

    // `count` other types of `module`, each with one MethodImpl row (for `IFill.F`). None is created.
    static void Fill(ModuleBuilder module, int count, string prefix)
    {
        for (int i = 0; i < count; i++)
        {
            TypeBuilder type = module.DefineType(prefix + i, TypeAttributes.Public);
            type.AddInterfaceImplementation(typeof(IFill));
            MethodBuilder method = type.DefineMethod("F", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Final | MethodAttributes.HideBySig, typeof(int), Type.EmptyTypes);
            ILGenerator il = method.GetILGenerator();
            il.Emit(OpCodes.Ldc_I4, i);
            il.Emit(OpCodes.Ret);
            type.DefineMethodOverride(method, typeof(IFill).GetMethod("F"));
        }
    }

    static int Call(string name, Type type) => (int)typeof(Caller).GetMethod(name).MakeGenericMethod(type).Invoke(null, null);

    // A call of `IDef.M` from a call site that has not resolved it before.
    static int CallAgain(Type type) => Call("CallDefAgain", type);

    // Whether a call of `IDef.M` on `type` throws `AmbiguousImplementationException`.
    static bool Ambiguous(Type type)
    {
        try
        {
            Call("CallDef", type);
            return false;
        }
        catch (TargetInvocationException e) when (e.InnerException is System.Runtime.AmbiguousImplementationException)
        {
            return true;
        }
    }

    // An interface of `module` extending `parent`, with a row naming `IDef.M` for `Dn` for each n of `bodies` that is
    // not 0, in order; `Dn` returns n.
    static TypeBuilder OrderDependentInterface(ModuleBuilder module, string name, Type parent, (int, int) bodies)
    {
        TypeBuilder i = module.DefineType(name, TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        i.AddInterfaceImplementation(parent);
        foreach (int body in new[] { bodies.Item1, bodies.Item2 })
        {
            if (body != 0) i.DefineMethodOverride(StaticBody(i, "D" + body, body), typeof(IDef).GetMethod("M"));
        }
        return i;
    }

    // `C : IDef` (a class, or a struct) with one row naming `IDef.M` for each of `bodies`, in order; `Bn` returns n.
    static TypeBuilder DefineC(ModuleBuilder module, bool isStruct, params int[] bodies)
    {
        TypeBuilder c = isStruct
            ? module.DefineType("C", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType))
            : module.DefineType("C", TypeAttributes.Public);
        c.AddInterfaceImplementation(typeof(IDef));
        MethodBuilder[] made = new MethodBuilder[10];
        foreach (int body in bodies)
        {
            if (made[body] == null) made[body] = StaticBody(c, "B" + body, body);
            c.DefineMethodOverride(made[body], typeof(IDef).GetMethod("M"));
        }
        return c;
    }

    // `C`'s rows `B1`, `B2` in a module of `rows` MethodImpl rows, `C`'s rows first or last; created after the last.
    static int Def(int rows, bool cFirst)
    {
        ModuleBuilder module = NewModule($"Def{rows}{(cFirst ? "a" : "b")}");
        if (!cFirst) Fill(module, rows - 2, "F");
        TypeBuilder c = DefineC(module, false, 1, 2);
        if (cFirst) Fill(module, rows - 2, "F");
        return Call("CallDef", c.CreateType());
    }

    // `I2 : IDef`, a module's interface with rows `D1`, `D2` for `IDef.M`, and `C : I2` with none.
    static int Iface(int rows)
    {
        ModuleBuilder module = NewModule($"Iface{rows}");
        TypeBuilder i2 = module.DefineType("I2", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        i2.AddInterfaceImplementation(typeof(IDef));
        i2.DefineMethodOverride(StaticBody(i2, "D1", 1), typeof(IDef).GetMethod("M"));
        i2.DefineMethodOverride(StaticBody(i2, "D2", 2), typeof(IDef).GetMethod("M"));
        Fill(module, rows - 2, "F");
        Type i2Created = i2.CreateType();
        TypeBuilder c = module.DefineType("C", TypeAttributes.Public);
        c.AddInterfaceImplementation(i2Created);
        return Call("CallDef", c.CreateType());
    }

    // `I2 : IVar<object>, IVar<IComparable>` with a row for each, `C : I2`, called through `IVar<string>`.
    static int Var(int rows, bool swapped)
    {
        ModuleBuilder module = NewModule($"Var{rows}{(swapped ? "b" : "a")}");
        TypeBuilder i2 = module.DefineType("I2", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        i2.AddInterfaceImplementation(typeof(IVar<object>));
        i2.AddInterfaceImplementation(typeof(IVar<IComparable>));
        MethodBuilder d1 = StaticBody(i2, "D1", 1);
        MethodBuilder d2 = StaticBody(i2, "D2", 2);
        if (swapped)
        {
            i2.DefineMethodOverride(d2, typeof(IVar<IComparable>).GetMethod("M"));
            i2.DefineMethodOverride(d1, typeof(IVar<object>).GetMethod("M"));
        }
        else
        {
            i2.DefineMethodOverride(d1, typeof(IVar<object>).GetMethod("M"));
            i2.DefineMethodOverride(d2, typeof(IVar<IComparable>).GetMethod("M"));
        }
        Fill(module, rows - 2, "F");
        Type i2Created = i2.CreateType();
        TypeBuilder c = module.DefineType("C", TypeAttributes.Public);
        c.AddInterfaceImplementation(i2Created);
        return Call("CallVar", c.CreateType());
    }

    public static int Main()
    {
        // 1-6: a class's two rows for one method, either side of 25 rows, wherever its rows are in the module.
        Check(1, Def(2, true), 1);
        Check(2, Def(24, true), 1);
        Check(3, Def(25, true), 2);
        Check(4, Def(26, true), 2);
        Check(5, Def(24, false), 1);
        Check(6, Def(25, false), 2);

        // 7-8: the rows added the other way round: it is the order added, not the bodies, that decides.
        foreach (var (number, rows, expected) in new[] { (7, 2, 2), (8, 25, 1) })
        {
            ModuleBuilder module = NewModule($"Swapped{rows}");
            TypeBuilder c = module.DefineType("C", TypeAttributes.Public);
            c.AddInterfaceImplementation(typeof(IDef));
            MethodBuilder b1 = StaticBody(c, "B1", 1);
            MethodBuilder b2 = StaticBody(c, "B2", 2);
            c.DefineMethodOverride(b2, typeof(IDef).GetMethod("M"));
            c.DefineMethodOverride(b1, typeof(IDef).GetMethod("M"));
            Fill(module, rows - 2, "F");
            Check(number, Call("CallDef", c.CreateType()), expected);
        }

        // 9-10: an interface's rows, which the default-body search walks.
        Check(9, Iface(2), 1);
        Check(10, Iface(25), 2);

        // 11-14: two rows for different declarations, both variance-compatible with the call's.
        Check(11, Var(2, false), 1);
        Check(12, Var(2, true), 2);
        Check(13, Var(25, false), 2);
        Check(14, Var(25, true), 1);

        // 15-16: an image on disk, with 34 MethodImpl rows, is read in RID order.
        Check(15, Caller.CallVar<CDisk>(), 1);
        Check(16, Caller.CallVar<CDiskSwapped>(), 2);

        // 17-18: a struct, whose call is exact code.
        foreach (var (number, rows, expected) in new[] { (17, 2, 1), (18, 25, 2) })
        {
            ModuleBuilder module = NewModule($"Struct{rows}");
            TypeBuilder c = DefineC(module, true, 1, 2);
            Fill(module, rows - 2, "F");
            Check(number, Call("CallDef", c.CreateType()), expected);
        }

        // 19: created at 25 rows, five more added before the call: reversed, whenever it is resolved.
        {
            ModuleBuilder module = NewModule("After25");
            TypeBuilder c = DefineC(module, false, 1, 2);
            Fill(module, 23, "F");
            Type created = c.CreateType();
            Fill(module, 5, "G");
            Check(19, Call("CallDef", created), 2);
        }

        // 20-21: a base type's rows are counted in its own module, not the derived type's.
        foreach (var (number, baseRows, derivedRows, expected) in new[] { (20, 25, 0, 2), (21, 2, 25, 1) })
        {
            ModuleBuilder baseModule = NewModule($"Base{baseRows}");
            TypeBuilder c = DefineC(baseModule, false, 1, 2);
            Fill(baseModule, baseRows - 2, "F");
            Type baseCreated = c.CreateType();
            ModuleBuilder derivedModule = NewModule($"Derived{derivedRows}");
            Fill(derivedModule, derivedRows, "G");
            Type derived = derivedModule.DefineType("D", TypeAttributes.Public, baseCreated).CreateType();
            Check(number, Call("CallDef", derived), expected);
        }

        // 22-25: created below 25 rows and called from 25, where the rows found in the order added and in reverse
        // give one body: one row for each method, a row repeated, and `B1`, `B2`, `B1`.
        {
            ModuleBuilder module = NewModule("WindowOne");
            TypeBuilder c = module.DefineType("C", TypeAttributes.Public);
            c.AddInterfaceImplementation(typeof(IDef));
            c.AddInterfaceImplementation(typeof(IDef2));
            c.DefineMethodOverride(StaticBody(c, "B1", 1), typeof(IDef).GetMethod("M"));
            c.DefineMethodOverride(StaticBody(c, "B2", 2), typeof(IDef2).GetMethod("M2"));
            Type created = c.CreateType();
            Fill(module, 23, "F");
            Check(22, Call("CallDef", created), 1);
            Check(23, Call("CallDef2", created), 2);
        }
        foreach (var (number, bodies) in new[] { (24, new[] { 1, 1 }), (25, new[] { 1, 2, 1 }) })
        {
            ModuleBuilder module = NewModule($"Window{bodies.Length}");
            Type created = DefineC(module, false, bodies).CreateType();
            Fill(module, 25 - bodies.Length, "F");
            Check(number, Call("CallDef", created), 1);
        }

        // 26-27: `I2 : IDef` with rows `D1`, `D2`, created below 25 rows, whose two orders disagree once its module
        // reaches 25; but `I3 : I2` supplies `D3`, and is more specific, so the call runs `D3` before and after.
        {
            ModuleBuilder module = NewModule("Overridden");
            Type i2 = OrderDependentInterface(module, "I2", typeof(IDef), (1, 2)).CreateType();
            Type i3 = OrderDependentInterface(module, "I3", i2, (3, 0)).CreateType();
            TypeBuilder c = module.DefineType("C", TypeAttributes.Public);
            c.AddInterfaceImplementation(i3);
            Type created = c.CreateType();
            Check(26, Call("CallDef", created), 3);
            Fill(module, 23, "F");
            Check(27, CallAgain(created), 3);
        }

        // 28-29: two equally specific interfaces, one or both of whose walks disagree, in the window: the call is
        // ambiguous whichever body each supplies.
        foreach (var (number, second) in new[] { (28, (4, 0)), (29, (4, 5)) })
        {
            ModuleBuilder module = NewModule($"Ambiguous{number}");
            Type i2 = OrderDependentInterface(module, "I2", typeof(IDef), (1, 2)).CreateType();
            Type i4 = OrderDependentInterface(module, "I4", typeof(IDef), second).CreateType();
            TypeBuilder c = module.DefineType("C", TypeAttributes.Public);
            c.AddInterfaceImplementation(i2);
            c.AddInterfaceImplementation(i4);
            Type created = c.CreateType();
            Fill(module, 23, "F");
            Check(number, Ambiguous(created) ? 1 : 0, 1);
        }

        return failed;
    }
}
