namespace WoofWare.PawPrint.Test

open System
open System.IO
open System.Reflection
open System.Reflection.Emit
open NUnit.Framework
open WoofWare.PawPrint

/// Instantiations of generic methods and types whose arguments satisfy, or violate, the type
/// parameter's constraints, named by each instruction that names a method: one rule of
/// `TypeVarTypeDesc::SatisfiesConstraints` (typedesc.cpp) per definition.
///
/// CoreCLR checks a method instantiation's constraints as `InstantiatedMethodDesc::NewInstantiatedMethodDesc`
/// first creates it (genmeth.cpp), raising `VerificationException`, and a type instantiation's as
/// `MethodTable::DoFullyLoad` completes its load (`SatisfiesClassConstraints`), raising
/// `TypeLoadException`. Both happen as the JIT compiles the method whose instruction names the
/// instantiation. C# refuses to spell the violating instantiations.
///
/// The JIT also checks an instantiation that a generic method's instruction spells over the method's
/// own type variables, with the variables standing as arguments (`CEEInfo::GetMethodForSecurity`,
/// jitinterface.cpp): a variable satisfies a rule only where its own constraints demand it. So it
/// refuses `M<U>()` in a method whose `U` lacks `M`'s constraint, whatever `U` is instantiated at.
/// PawPrint does not model that check, and parks those cases.
///
/// Each caller here puts the instruction first and has no handler of its own, so whether the refusal
/// is raised as the caller is compiled or as its instruction runs cannot show, except in the callers
/// over a type variable, which increment `Plain.Counter` first.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestFabricatedInstantiationConstraints =

    /// One rule of a type parameter's constraints, which a definition's `T` carries alone.
    [<RequireQualifiedAccess>]
    type private Rule =
        /// No `allows ref struct`: rejects `Span<int>`, admits `int`.
        | ByRefLike
        /// `struct`: rejects `string`, admits `int`.
        | NonNullableValueType
        /// `class`: rejects `int`, admits `string`.
        | ReferenceType
        /// `new()`: rejects the abstract class `Abs`, admits `Conc`.
        | ParameterlessConstructor
        /// `IMarker`: rejects `string`, admits `Conc`.
        | Assignable

    let private rules : Rule list =
        [
            Rule.ByRefLike
            Rule.NonNullableValueType
            Rule.ReferenceType
            Rule.ParameterlessConstructor
            Rule.Assignable
        ]

    /// The instruction that names the instantiation, and where.
    [<RequireQualifiedAccess>]
    type private Position =
        /// `call int Plain::M<X>()`, returning 7.
        | MethodCall
        /// `ldftn Plain::M<X>()`, then 7.
        | MethodLdftn
        /// `ldtoken method Plain::M<X>()`, then 7.
        | MethodLdtoken
        /// `ldnull; callvirt instance int Virt::GM<X>()`: a `NullReferenceException` once it binds.
        | GenericVirtualCallvirt
        /// `call int G<X>::Get()`, returning 9.
        | TypeStaticCall
        /// `newobj G<X>::.ctor()`, then 9.
        | TypeNewobj
        /// `ldnull; callvirt instance int G<X>::Inst()`: a `NullReferenceException` once it binds.
        | TypeCallvirt
        /// `Plain.Counter++; call int Plain::M<U>()` in `OverVariableMethod<U>`, whose `U` carries no
        /// constraint, called at `X`.
        | OverVariableMethodCall
        /// `Plain.Counter++; call int G<U>::Get()` in `OverVariableType<U>`, whose `U` carries no
        /// constraint, called at `X`.
        | OverVariableTypeStaticCall

    let private positions : Position list =
        [
            Position.MethodCall
            Position.MethodLdftn
            Position.MethodLdtoken
            Position.GenericVirtualCallvirt
            Position.TypeStaticCall
            Position.TypeNewobj
            Position.TypeCallvirt
            Position.OverVariableMethodCall
            Position.OverVariableTypeStaticCall
        ]

    /// The name of the `Plain` method that names `rule`'s definition at `position`, at the violating
    /// argument if `violates` and the admitted one otherwise.
    let private callerName (rule : Rule) (position : Position) (violates : bool) : string =
        let verdict = if violates then "Bad" else "Good"
        $"Calls%O{rule}%O{position}%s{verdict}"

    /// The image: `interface IMarker`; `abstract class Abs` and `class Conc : IMarker`, each with a
    /// public constructor; and for each rule `Plain::M<T>()`, `class G<T>` with a constructor,
    /// `static int Get()` and `int Inst()`, and `Virt::GM<T>()`, each `T` carrying the rule alone;
    /// and `Plain`'s callers, one per rule, position and verdict, each taking nothing and returning
    /// an `int`, with the generic callers over a type variable that they use.
    let private fabricate () : byte[] =
        // F# refuses `typeof` of a byref-like type (FS0412).
        let span =
            typeof<obj>.Assembly.GetType("System.Span`1", true).MakeGenericType [| typeof<int> |]

        let builder =
            PersistedAssemblyBuilder (AssemblyName "InstantiationConstraints", typeof<obj>.Assembly)

        let modul = builder.DefineDynamicModule "InstantiationConstraints"

        let marker =
            modul.DefineType ("IMarker", TypeAttributes.Public ||| TypeAttributes.Interface ||| TypeAttributes.Abstract)

        marker.CreateType () |> ignore<Type>

        let withConstructor (ty : TypeBuilder) : ConstructorBuilder =
            ty.DefineDefaultConstructor MethodAttributes.Public

        let abs =
            modul.DefineType ("Abs", TypeAttributes.Public ||| TypeAttributes.Abstract, typeof<obj>)

        withConstructor abs |> ignore<ConstructorBuilder>
        abs.CreateType () |> ignore<Type>

        let conc = modul.DefineType ("Conc", TypeAttributes.Public, typeof<obj>)
        conc.AddInterfaceImplementation marker
        withConstructor conc |> ignore<ConstructorBuilder>
        conc.CreateType () |> ignore<Type>

        let arguments (rule : Rule) : Type * Type =
            match rule with
            | Rule.ByRefLike -> span, typeof<int>
            | Rule.NonNullableValueType -> typeof<string>, typeof<int>
            | Rule.ReferenceType -> typeof<int>, typeof<string>
            | Rule.ParameterlessConstructor -> (abs :> Type), (conc :> Type)
            | Rule.Assignable -> typeof<string>, (conc :> Type)

        let constrain (rule : Rule) (parameter : GenericTypeParameterBuilder) : unit =
            match rule with
            | Rule.ByRefLike -> ()
            | Rule.NonNullableValueType ->
                parameter.SetGenericParameterAttributes GenericParameterAttributes.NotNullableValueTypeConstraint
            | Rule.ReferenceType ->
                parameter.SetGenericParameterAttributes GenericParameterAttributes.ReferenceTypeConstraint
            | Rule.ParameterlessConstructor ->
                parameter.SetGenericParameterAttributes GenericParameterAttributes.DefaultConstructorConstraint
            | Rule.Assignable -> parameter.SetInterfaceConstraints [| marker :> Type |]

        let returns (il : ILGenerator) (value : int) : unit =
            il.Emit (OpCodes.Ldc_I4, value)
            il.Emit OpCodes.Ret

        let plain =
            modul.DefineType ("Plain", TypeAttributes.Public ||| TypeAttributes.Abstract ||| TypeAttributes.Sealed)

        let counter =
            plain.DefineField ("Counter", typeof<int>, FieldAttributes.Public ||| FieldAttributes.Static)

        let virt = modul.DefineType ("Virt", TypeAttributes.Public, typeof<obj>)
        withConstructor virt |> ignore<ConstructorBuilder>

        let staticAttributes = MethodAttributes.Public ||| MethodAttributes.Static

        // Per rule: `Plain::M<T>`, `G<T>` and its members, `Virt::GM<T>`.
        let definitions =
            rules
            |> List.map (fun rule ->
                let m =
                    plain.DefineMethod ($"M%O{rule}", staticAttributes, typeof<int>, Type.EmptyTypes)

                constrain rule (m.DefineGenericParameters [| "T" |]).[0]
                returns (m.GetILGenerator ()) 7

                let generic =
                    modul.DefineType ($"G%O{rule}`1", TypeAttributes.Public ||| TypeAttributes.Class, typeof<obj>)

                constrain rule (generic.DefineGenericParameters [| "T" |]).[0]
                let ctor = withConstructor generic

                let get =
                    generic.DefineMethod ("Get", staticAttributes, typeof<int>, Type.EmptyTypes)

                returns (get.GetILGenerator ()) 9

                let inst =
                    generic.DefineMethod ("Inst", MethodAttributes.Public, typeof<int>, Type.EmptyTypes)

                returns (inst.GetILGenerator ()) 9

                generic.CreateType () |> ignore<Type>

                let gm =
                    virt.DefineMethod (
                        $"GM%O{rule}",
                        MethodAttributes.Public
                        ||| MethodAttributes.Virtual
                        ||| MethodAttributes.NewSlot,
                        typeof<int>,
                        Type.EmptyTypes
                    )

                constrain rule (gm.DefineGenericParameters [| "U" |]).[0]
                returns (gm.GetILGenerator ()) 7

                rule, (m, generic, ctor, get, inst, gm)
            )
            |> Map.ofList

        virt.CreateType () |> ignore<Type>

        let caller (name : string) : ILGenerator =
            plain.DefineMethod(name, staticAttributes, typeof<int>, Type.EmptyTypes).GetILGenerator ()

        // `name<U>()`, which increments `Counter` and then runs `emit` over its `U`.
        let overVariable (name : string) (emit : ILGenerator -> Type -> unit) : MethodBuilder =
            let method =
                plain.DefineMethod (name, staticAttributes, typeof<int>, Type.EmptyTypes)

            let u = (method.DefineGenericParameters [| "U" |]).[0]
            let il = method.GetILGenerator ()
            il.Emit (OpCodes.Ldsfld, counter)
            il.Emit OpCodes.Ldc_I4_1
            il.Emit OpCodes.Add
            il.Emit (OpCodes.Stsfld, counter)
            emit il u
            il.Emit OpCodes.Ret
            method

        for rule in rules do
            let m, generic, ctor, get, inst, gm = definitions.[rule]
            let bad, good = arguments rule

            let overVariableMethod =
                overVariable
                    $"OverVariableMethod%O{rule}"
                    (fun il u -> il.Emit (OpCodes.Call, m.MakeGenericMethod [| u |]))

            let overVariableType =
                overVariable
                    $"OverVariableType%O{rule}"
                    (fun il u -> il.Emit (OpCodes.Call, TypeBuilder.GetMethod (generic.MakeGenericType [| u |], get)))

            for violates, argument in [ true, bad ; false, good ] do
                let instantiated = generic.MakeGenericType [| argument |]

                for position in positions do
                    let il = caller (callerName rule position violates)

                    match position with
                    | Position.MethodCall ->
                        il.Emit (OpCodes.Call, m.MakeGenericMethod [| argument |])
                        il.Emit OpCodes.Ret
                    | Position.MethodLdftn ->
                        il.Emit (OpCodes.Ldftn, m.MakeGenericMethod [| argument |])
                        il.Emit OpCodes.Pop
                        returns il 7
                    | Position.MethodLdtoken ->
                        il.Emit (OpCodes.Ldtoken, m.MakeGenericMethod [| argument |])
                        il.Emit OpCodes.Pop
                        returns il 7
                    | Position.GenericVirtualCallvirt ->
                        il.Emit OpCodes.Ldnull
                        il.Emit (OpCodes.Callvirt, gm.MakeGenericMethod [| argument |])
                        il.Emit OpCodes.Ret
                    | Position.TypeStaticCall ->
                        il.Emit (OpCodes.Call, TypeBuilder.GetMethod (instantiated, get))
                        il.Emit OpCodes.Ret
                    | Position.TypeNewobj ->
                        il.Emit (OpCodes.Newobj, TypeBuilder.GetConstructor (instantiated, ctor))
                        il.Emit OpCodes.Pop
                        returns il 9
                    | Position.TypeCallvirt ->
                        il.Emit OpCodes.Ldnull
                        il.Emit (OpCodes.Callvirt, TypeBuilder.GetMethod (instantiated, inst))
                        il.Emit OpCodes.Ret
                    | Position.OverVariableMethodCall ->
                        il.Emit (OpCodes.Call, overVariableMethod.MakeGenericMethod [| argument |])
                        il.Emit OpCodes.Ret
                    | Position.OverVariableTypeStaticCall ->
                        il.Emit (OpCodes.Call, overVariableType.MakeGenericMethod [| argument |])
                        il.Emit OpCodes.Ret

        plain.CreateType () |> ignore<Type>

        use stream = new MemoryStream ()
        builder.Save stream
        stream.ToArray ()

    let private image : Lazy<byte[]> = lazy (fabricate ())

    /// Returns what `Plain`'s caller returns, or 102 for a `TypeLoadException`, 103 for a
    /// `VerificationException`, 104 for a `NullReferenceException` and 105 for any other exception;
    /// plus ten times `Plain.Counter`.
    let private driver (caller : string) : string =
        $$"""
using System;

public static class Program
{
    static int Run()
    {
        try
        {
            return Plain.{{caller}}();
        }
        catch (TypeLoadException)
        {
            return 102;
        }
        catch (System.Security.VerificationException)
        {
            return 103;
        }
        catch (NullReferenceException)
        {
            return 104;
        }
        catch (Exception)
        {
            return 105;
        }
    }

    public static int Main()
    {
        int result = Run();
        return result + 10 * Plain.Counter;
    }
}
"""

    /// What the real runtime returns for `rule` at `position`, where the argument violates its
    /// parameter's constraints if `violates`.
    let private expected (rule : Rule) (position : Position) (violates : bool) : int =
        match position, violates with
        // The caller over a type variable is refused as it is compiled, whatever its argument:
        // its `U` lacks the callee's constraint. Under `Rule.ByRefLike` nothing has a constraint,
        // and the violating argument is refused as the caller's own instantiation.
        | Position.OverVariableMethodCall, _ when rule <> Rule.ByRefLike -> 103
        | Position.OverVariableTypeStaticCall, _ when rule <> Rule.ByRefLike -> 102
        | Position.MethodCall, true
        | Position.MethodLdftn, true
        | Position.MethodLdtoken, true
        | Position.GenericVirtualCallvirt, true -> 103
        | Position.TypeStaticCall, true
        | Position.TypeNewobj, true
        | Position.TypeCallvirt, true -> 102
        | Position.OverVariableMethodCall, true
        | Position.OverVariableTypeStaticCall, true -> 103
        | Position.MethodCall, false
        | Position.MethodLdftn, false
        | Position.MethodLdtoken, false -> 7
        | Position.GenericVirtualCallvirt, false
        | Position.TypeCallvirt, false -> 104
        | Position.TypeStaticCall, false
        | Position.TypeNewobj, false -> 9
        | Position.OverVariableMethodCall, false -> 17
        | Position.OverVariableTypeStaticCall, false -> 19

    let private cases : (Rule * Position * bool) list =
        [
            for rule in rules do
                for position in positions do
                    for violates in [ true ; false ] do
                        yield rule, position, violates
        ]

    let private caseNames : string list =
        cases
        |> List.map (fun (rule, position, violates) -> callerName rule position violates)

    [<TestCaseSource(nameof caseNames)>]
    let ``an instantiation's constraints are checked where the runtime checks them`` (caseName : string) : unit =
        let rule, position, violates =
            cases
            |> List.find (fun (rule, position, violates) -> callerName rule position violates = caseName)

        match position with
        | Position.OverVariableMethodCall
        | Position.OverVariableTypeStaticCall when rule <> Rule.ByRefLike ->
            ParkedOn.check
                EmulatedRuntime.supported
                "PawPrint does not check an instantiation a generic method spells over its own type variables against the variables' constraints, as CoreCLR's JIT does when it compiles the method; checking it would un-park this"
        | _ -> ()

        FabricatedGuest.run
            "InstantiationConstraints"
            image.Value
            $"Driver%s{caseName}"
            (driver caseName)
            (expected rule position violates)
