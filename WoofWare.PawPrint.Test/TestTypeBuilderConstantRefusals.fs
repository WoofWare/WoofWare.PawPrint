namespace WoofWare.PawPrint.Test

open System.IO
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// The constants `TypeBuilder_SetConstantValue` refuses, where real .NET writes them (measured on .NET 10 and 11, each
/// guest exiting 0 there): a constant of a parameter of a created type, which real .NET writes and which reflection
/// shows or not depending on what it has already read; and a constant of a property with a getter or a setter, which
/// CoreCLR's emitter detaches from the property. The constants PawPrint stores are in
/// `sourcesImpure/TypeBuilderSetConstant.cs`.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestTypeBuilderConstantRefusals =

    /// A guest whose `Main` runs `body` with `module` a dynamic module.
    let private source (body : string) : string =
        $$"""
using System;
using System.Reflection;
using System.Reflection.Emit;

public class Program
{
    public static int Main(string[] args)
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule("Dyn");
        {{body}}
        return 0;
    }
}
"""

    let private runRefused (name : string) (body : string) : GuestFailureException =
        let image = Roslyn.compile [ source body ]

        let _messages, loggerFactory =
            LoggerFactory.makeTestWithProperties [ "source_file", name ]

        use _loggerFactoryResource = loggerFactory
        use peImage = new MemoryStream (image)

        let host =
            HostConfig.Default (
                FrameworkUnderTest.assembliesFor
                    (GuestFramework.PinnedPackOf KernelConfig.Default.UnixPlatform)
                    GuestApplication.InMemory
            )

        let host =
            { host with
                Guest =
                    { host.Guest with
                        AppContext =
                            AppContextProperties.ofMap (
                                Map.ofList
                                    [
                                        "System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported", "true"
                                    ]
                            )
                    }
            }

        Assert.Throws<GuestFailureException> (fun () ->
            BoundedRun.runWith loggerFactory BoundedRun.defaultMaxSteps name (Some name) peImage host
            |> ExpectRun.ended
            |> ignore<RunOutcome>
        )

    [<Test>]
    let ``a constant on a parameter of a created type is refused`` () : unit =
        // Real .NET writes the row and the flag, and a method whose parameters reflection has not read shows them.
        let exc =
            runRefused
                "ParameterConstantOnCreatedType.cs"
                """
        // (a) A parameter of a created type given a constant: real .NET writes the row and the flag, and a method whose
        // parameters reflection has not read before shows them.
        TypeBuilder t = module.DefineType("T", TypeAttributes.Public | TypeAttributes.Abstract);
        MethodBuilder m = t.DefineMethod("M", MethodAttributes.Public | MethodAttributes.Static, typeof(void), new[] { typeof(int) });
        m.GetILGenerator().Emit(OpCodes.Ret);
        ParameterBuilder p = m.DefineParameter(1, ParameterAttributes.None, "p");
        Type created = t.CreateType();
        p.SetConstant(7);
        ParameterInfo pi = created.GetMethod("M").GetParameters()[0];
        if ((int)pi.Attributes != 0x1000) return 1;
        if (!pi.HasDefaultValue) return 2;
        if (!(pi.DefaultValue is int v) || v != 7) return 3;"""

        exc.Message |> shouldContainText "TODO: TypeBuilder_SetConstantValue"

        exc.Message
        |> shouldContainText "the owner 0x08000001 belongs to TypeDef 0x02000002, which is created"

    [<Test>]
    let ``a constant on a property with a getter is refused`` () : unit =
        // Real .NET detaches the getter, so that the property has no accessors, and writes the constant.
        let exc =
            runRefused
                "PropertyConstantWithGetter.cs"
                """
        // (b) A property with a getter only, then SetConstant: real .NET detaches the getter and writes the constant.
        TypeBuilder t = module.DefineType("T", TypeAttributes.Public);
        t.DefineDefaultConstructor(MethodAttributes.Public);
        MethodBuilder g = t.DefineMethod("get_P", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, typeof(int), Type.EmptyTypes);
        ILGenerator il = g.GetILGenerator(); il.Emit(OpCodes.Ldc_I4, 42); il.Emit(OpCodes.Ret);
        PropertyBuilder p = t.DefineProperty("P", PropertyAttributes.None, typeof(int), Type.EmptyTypes);
        p.SetGetMethod(g);
        p.SetConstant(7);
        Type created = t.CreateType();
        PropertyInfo pi = created.GetProperty("P", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        if (pi == null) return 1;
        if (pi.GetGetMethod(true) != null || pi.GetSetMethod(true) != null || pi.GetAccessors(true).Length != 0) return 2;
        if (created.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Length != 0) return 3;
        if ((int)pi.Attributes != 0x1000) return 4;
        if (!(pi.GetConstantValue() is int v) || v != 7) return 5;
        // The getter itself stays, and still works.
        MethodInfo getter = created.GetMethod("get_P");
        if (getter == null || (int)getter.Invoke(Activator.CreateInstance(created), null) != 42) return 6;"""

        exc.Message |> shouldContainText "TODO: TypeBuilder_SetConstantValue"

        exc.Message
        |> shouldContainText "the property 0x17000001 has a Getter or Setter accessor"

    [<Test>]
    let ``a constant on a property with a setter is refused`` () : unit =
        // Real .NET detaches the setter, so that the property has no accessors, and writes the constant.
        let exc =
            runRefused
                "PropertyConstantWithSetter.cs"
                """
        // (c) A property with a setter only, then SetConstant: real .NET detaches the setter and writes the constant.
        TypeBuilder t = module.DefineType("T", TypeAttributes.Public);
        t.DefineDefaultConstructor(MethodAttributes.Public);
        MethodBuilder s = t.DefineMethod("set_P", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, typeof(void), new[] { typeof(int) });
        s.GetILGenerator().Emit(OpCodes.Ret);
        PropertyBuilder p = t.DefineProperty("P", PropertyAttributes.None, typeof(int), Type.EmptyTypes);
        p.SetSetMethod(s);
        p.SetConstant(7);
        Type created = t.CreateType();
        PropertyInfo pi = created.GetProperty("P", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        if (pi == null) return 1;
        if (pi.GetGetMethod(true) != null || pi.GetSetMethod(true) != null || pi.GetAccessors(true).Length != 0) return 2;
        if (created.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Length != 0) return 3;
        if ((int)pi.Attributes != 0x1000) return 4;
        if (!(pi.GetConstantValue() is int v) || v != 7) return 5;
        // The setter itself stays.
        if (created.GetMethod("set_P") == null) return 6;"""

        exc.Message |> shouldContainText "TODO: TypeBuilder_SetConstantValue"

        exc.Message
        |> shouldContainText "the property 0x17000001 has a Getter or Setter accessor"

    [<Test>]
    let ``a constant on a property with a getter and a setter is refused`` () : unit =
        // Real .NET detaches both, so that the property has no accessors, and writes the constant.
        let exc =
            runRefused
                "PropertyConstantWithGetterAndSetter.cs"
                """
        // (d) A property with a getter and a setter, then SetConstant: real .NET detaches both and writes the constant.
        TypeBuilder t = module.DefineType("T", TypeAttributes.Public);
        t.DefineDefaultConstructor(MethodAttributes.Public);
        MethodBuilder g = t.DefineMethod("get_P", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, typeof(int), Type.EmptyTypes);
        ILGenerator il = g.GetILGenerator(); il.Emit(OpCodes.Ldc_I4, 42); il.Emit(OpCodes.Ret);
        MethodBuilder s = t.DefineMethod("set_P", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, typeof(void), new[] { typeof(int) });
        s.GetILGenerator().Emit(OpCodes.Ret);
        PropertyBuilder p = t.DefineProperty("P", PropertyAttributes.None, typeof(int), Type.EmptyTypes);
        p.SetGetMethod(g);
        p.SetSetMethod(s);
        p.SetConstant(7);
        Type created = t.CreateType();
        PropertyInfo pi = created.GetProperty("P", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
        if (pi == null) return 1;
        if (pi.GetGetMethod(true) != null || pi.GetSetMethod(true) != null || pi.GetAccessors(true).Length != 0) return 2;
        if (created.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Length != 0) return 3;
        if ((int)pi.Attributes != 0x1000) return 4;
        if (!(pi.GetConstantValue() is int v) || v != 7) return 5;
        try { pi.GetValue(Activator.CreateInstance(created)); return 6; }
        catch (ArgumentException e) { if (e.Message != "Property Get method was not found.") return 7; }
        if (created.GetMethod("get_P") == null || created.GetMethod("set_P") == null) return 8;"""

        exc.Message |> shouldContainText "TODO: TypeBuilder_SetConstantValue"

        exc.Message
        |> shouldContainText "the property 0x17000001 has a Getter or Setter accessor"
