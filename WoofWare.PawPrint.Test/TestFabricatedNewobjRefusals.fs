namespace WoofWare.PawPrint.Test

open System
open System.IO
open System.Reflection
open System.Reflection.Emit
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// A `newobj` whose token names what CoreCLR's JIT will not construct: an abstract method
/// (`BadImageFormatException`, "Bad IL format."), a static one (`InvalidProgramException`), or an
/// abstract class's constructor (`InvalidOperationException`, "Instances of abstract classes cannot
/// be created."), each in a method of an image's own. C# spells none of them.
///
/// CoreCLR raises each as it compiles the method holding the `newobj`. PawPrint raises it at the
/// instruction (docs/divergences.md), so each fabricated method puts the `newobj` first and has no
/// handler of its own, so that the difference cannot show.
///
/// A `newobj` of an instance method that is not a constructor is a separate matter: PawPrint stops
/// at it by name, and the last test checks that.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestFabricatedNewobjRefusals =

    let private ctorAttributes : MethodAttributes =
        MethodAttributes.Public
        ||| MethodAttributes.HideBySig
        ||| MethodAttributes.SpecialName
        ||| MethodAttributes.RTSpecialName

    let private abstractAttributes : MethodAttributes =
        MethodAttributes.Public
        ||| MethodAttributes.Virtual
        ||| MethodAttributes.Abstract
        ||| MethodAttributes.HideBySig
        ||| MethodAttributes.NewSlot

    /// `public static object name(params)` on `owner`, whose body is `emit` and then `ret`.
    let private staticMethod
        (owner : TypeBuilder)
        (name : string)
        (parameters : Type[])
        (emit : ILGenerator -> unit)
        : unit
        =
        let m =
            owner.DefineMethod (
                name,
                MethodAttributes.Public
                ||| MethodAttributes.Static
                ||| MethodAttributes.HideBySig,
                typeof<obj>,
                parameters
            )

        let il = m.GetILGenerator ()
        emit il
        il.Emit OpCodes.Ret

    /// A public constructor of `owner` from an `int`, which stores it in `field`.
    let private storingConstructor (owner : TypeBuilder) (field : FieldBuilder) : ConstructorBuilder =
        let ctor =
            owner.DefineConstructor (ctorAttributes, CallingConventions.Standard, [| typeof<int> |])

        let il = ctor.GetILGenerator ()
        il.Emit OpCodes.Ldarg_0
        il.Emit OpCodes.Ldarg_1
        il.Emit (OpCodes.Stfld, field)
        il.Emit OpCodes.Ret
        ctor

    /// The image:
    ///   - `class Plain { public int V; public Plain(int); public static object Stat(); }`;
    ///   - `abstract class Abs { public int V; public Abs(int); public abstract int A(); }`;
    ///   - `abstract class AbsG<T> { public int V; public AbsG(int); }`;
    ///   - `interface I { int M(); static abstract int SA(); }`;
    ///   - `struct AbsS { public int V; public AbsS(int); }`, flagged abstract and sealed, which no
    ///     compiler spells and the loader accepts;
    ///   - `static class Calls`, one method for each, whose body starts with the `newobj`.
    let private fabricate () : byte[] =
        let builder =
            PersistedAssemblyBuilder (AssemblyName "NewobjRefusals", typeof<obj>.Assembly)

        let modul = builder.DefineDynamicModule "NewobjRefusals"

        let plain =
            modul.DefineType ("Plain", TypeAttributes.Public ||| TypeAttributes.Class)

        let plainV = plain.DefineField ("V", typeof<int>, FieldAttributes.Public)
        let plainCtor = storingConstructor plain plainV

        let stat =
            plain.DefineMethod (
                "Stat",
                MethodAttributes.Public
                ||| MethodAttributes.Static
                ||| MethodAttributes.HideBySig,
                typeof<obj>,
                [||]
            )

        let statIl = stat.GetILGenerator ()
        statIl.Emit OpCodes.Ldnull
        statIl.Emit OpCodes.Ret
        plain.CreateType () |> ignore<Type>

        let abs =
            modul.DefineType ("Abs", TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Class)

        let absV = abs.DefineField ("V", typeof<int>, FieldAttributes.Public)
        let absCtor = storingConstructor abs absV
        let absA = abs.DefineMethod ("A", abstractAttributes, typeof<int>, [||])
        abs.CreateType () |> ignore<Type>

        let absG =
            modul.DefineType ("AbsG", TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Class)

        absG.DefineGenericParameters [| "T" |] |> ignore<GenericTypeParameterBuilder[]>
        let absGV = absG.DefineField ("V", typeof<int>, FieldAttributes.Public)
        let absGCtor = storingConstructor absG absGV
        absG.CreateType () |> ignore<Type>

        let absGOfInt =
            TypeBuilder.GetConstructor (absG.MakeGenericType [| typeof<int> |], absGCtor)

        let i =
            modul.DefineType ("I", TypeAttributes.Public ||| TypeAttributes.Interface ||| TypeAttributes.Abstract)

        let iM = i.DefineMethod ("M", abstractAttributes, typeof<int>, [||])

        let iSA =
            i.DefineMethod (
                "SA",
                MethodAttributes.Public
                ||| MethodAttributes.Static
                ||| MethodAttributes.Virtual
                ||| MethodAttributes.Abstract
                ||| MethodAttributes.HideBySig,
                typeof<int>,
                [||]
            )

        i.CreateType () |> ignore<Type>

        let absS =
            modul.DefineType (
                "AbsS",
                TypeAttributes.Public
                ||| TypeAttributes.Abstract
                ||| TypeAttributes.Sealed
                ||| TypeAttributes.SequentialLayout,
                typeof<ValueType>
            )

        let absSV = absS.DefineField ("V", typeof<int>, FieldAttributes.Public)
        let absSCtor = storingConstructor absS absSV
        absS.CreateType () |> ignore<Type>

        let calls =
            modul.DefineType ("Calls", TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed)

        // The control: a constructor of a class that is not abstract.
        staticMethod
            calls
            "NewPlain"
            [| typeof<int> |]
            (fun il ->
                il.Emit OpCodes.Ldarg_0
                il.Emit (OpCodes.Newobj, plainCtor)
            )

        staticMethod calls "NewStatic" [||] (fun il -> il.Emit (OpCodes.Newobj, stat))
        staticMethod calls "NewAbstractMethod" [||] (fun il -> il.Emit (OpCodes.Newobj, absA))
        staticMethod calls "NewInterfaceMethod" [||] (fun il -> il.Emit (OpCodes.Newobj, iM))
        // Abstract and static: the abstract refusal comes first.
        staticMethod calls "NewStaticAbstract" [||] (fun il -> il.Emit (OpCodes.Newobj, iSA))

        staticMethod
            calls
            "NewAbstractClass"
            [| typeof<int> |]
            (fun il ->
                il.Emit OpCodes.Ldarg_0
                il.Emit (OpCodes.Newobj, absCtor)
            )

        staticMethod
            calls
            "NewAbstractGeneric"
            [| typeof<int> |]
            (fun il ->
                il.Emit OpCodes.Ldarg_0
                il.Emit (OpCodes.Newobj, absGOfInt)
            )

        // A value type is allocated without asking whether it is abstract, so this constructs. It
        // returns the field the constructor stored, boxed, because C# reads an abstract sealed type
        // as a static class, so the driver cannot name the struct.
        staticMethod
            calls
            "NewAbstractStruct"
            [| typeof<int> |]
            (fun il ->
                let local = il.DeclareLocal absS
                il.Emit OpCodes.Ldarg_0
                il.Emit (OpCodes.Newobj, absSCtor)
                il.Emit (OpCodes.Stloc, local)
                il.Emit (OpCodes.Ldloca, local)
                il.Emit (OpCodes.Ldfld, absSV)
                il.Emit (OpCodes.Box, typeof<int>)
            )

        calls.CreateType () |> ignore<Type>

        use image = new MemoryStream ()
        builder.Save image
        image.ToArray ()

    /// Each scenario returns its own code on failure and 0 on success, so a disagreement names
    /// the scenario.
    let private driverSource : string =
        """
using System;

public static class Driver
{
    private static int Expect<T>(Func<object> call, string message, int ifReturned, int ifWrong) where T : Exception
    {
        try
        {
            call();
            return ifReturned;
        }
        catch (Exception e)
        {
            return e.GetType() == typeof(T) && e.Message == message ? 0 : ifWrong;
        }
    }

    private static int Run()
    {
        int r;

        object plain = Calls.NewPlain(41);
        if (!(plain is Plain p) || p.V != 41) return 1;

        r = Expect<InvalidProgramException>(() => Calls.NewStatic(), "Common Language Runtime detected an invalid program.", 2, 3);
        if (r != 0) return r;
        r = Expect<BadImageFormatException>(() => Calls.NewAbstractMethod(), "Bad IL format.", 4, 5);
        if (r != 0) return r;
        r = Expect<BadImageFormatException>(() => Calls.NewInterfaceMethod(), "Bad IL format.", 6, 7);
        if (r != 0) return r;
        r = Expect<BadImageFormatException>(() => Calls.NewStaticAbstract(), "Bad IL format.", 8, 9);
        if (r != 0) return r;
        r = Expect<InvalidOperationException>(() => Calls.NewAbstractClass(5), "Instances of abstract classes cannot be created.", 10, 11);
        if (r != 0) return r;
        r = Expect<InvalidOperationException>(() => Calls.NewAbstractGeneric(6), "Instances of abstract classes cannot be created.", 12, 13);
        if (r != 0) return r;

        if (!(Calls.NewAbstractStruct(43) is int v) || v != 43) return 14;

        return 0;
    }

    public static int Main(string[] args)
    {
        return Run();
    }
}
"""

    [<Test>]
    let ``a newobj CoreCLR's JIT refuses raises what the real runtime raises`` () : unit =
        FabricatedGuest.run "NewobjRefusals" (fabricate ()) "NewobjRefusalsDriver" driverSource 0

    /// The image: `class Inst { public int V; public Inst(); public void NotCtor(); }`, where
    /// `NotCtor` sets `V` to 77; and `class Fake`, whose only constructor-like method is an
    /// instance method named `.ctor` without `RTSpecialName`. `Calls` has a method for each that
    /// starts with a `newobj` of it.
    let private fabricateNonConstructors () : byte[] =
        let builder =
            PersistedAssemblyBuilder (AssemblyName "NewobjNonConstructors", typeof<obj>.Assembly)

        let modul = builder.DefineDynamicModule "NewobjNonConstructors"

        let inst = modul.DefineType ("Inst", TypeAttributes.Public ||| TypeAttributes.Class)
        let instV = inst.DefineField ("V", typeof<int>, FieldAttributes.Public)

        inst.DefineDefaultConstructor MethodAttributes.Public
        |> ignore<ConstructorBuilder>

        let notCtor =
            inst.DefineMethod ("NotCtor", MethodAttributes.Public ||| MethodAttributes.HideBySig, typeof<Void>, [||])

        let notCtorIl = notCtor.GetILGenerator ()
        notCtorIl.Emit OpCodes.Ldarg_0
        notCtorIl.Emit (OpCodes.Ldc_I4, 77)
        notCtorIl.Emit (OpCodes.Stfld, instV)
        notCtorIl.Emit OpCodes.Ret
        inst.CreateType () |> ignore<Type>

        let fake = modul.DefineType ("Fake", TypeAttributes.Public ||| TypeAttributes.Class)

        let fakeCtor =
            fake.DefineMethod (
                ".ctor",
                MethodAttributes.Public
                ||| MethodAttributes.HideBySig
                ||| MethodAttributes.SpecialName,
                typeof<Void>,
                [||]
            )

        fakeCtor.GetILGenerator().Emit OpCodes.Ret
        fake.CreateType () |> ignore<Type>

        let calls =
            modul.DefineType ("Calls", TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed)

        staticMethod calls "NewNotCtor" [||] (fun il -> il.Emit (OpCodes.Newobj, notCtor))
        staticMethod calls "NewFake" [||] (fun il -> il.Emit (OpCodes.Newobj, fakeCtor))
        calls.CreateType () |> ignore<Type>

        use image = new MemoryStream ()
        builder.Save image
        image.ToArray ()

    let private nonConstructorDriver (call : string) : string =
        $$"""
public static class Driver
{
    public static int Main(string[] args)
    {
        return Calls.{{call}}() == null ? 1 : 0;
    }
}
"""

    /// Real .NET constructs and calls `NotCtor` (measured), and crashes the process compiling other
    /// such `newobj`s, so no answer can be copied; PawPrint stops by name instead. A method named
    /// `.ctor` without `RTSpecialName` is no constructor either.
    [<TestCase "NewNotCtor">]
    [<TestCase "NewFake">]
    let ``a newobj of an instance method that is not a constructor stops by name`` (call : string) : unit =
        let outcome =
            FabricatedGuest.runOnPawPrintBounded
                "NewobjNonConstructors"
                (fabricateNonConstructors ())
                $"NewobjNonConstructorsDriver%s{call}"
                (nonConstructorDriver call)
                10_000_000L

        match outcome with
        | FabricatedOutcome.Exited code -> failwith $"PawPrint ran the guest, which exited %d{code}"
        | FabricatedOutcome.Failed e -> e.ToString () |> shouldContainText "is not an instance constructor"
