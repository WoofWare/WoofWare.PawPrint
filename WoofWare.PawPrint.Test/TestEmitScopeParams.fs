namespace WoofWare.PawPrint.Test

open System
open System.Collections.Immutable
open System.Reflection
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// A dynamic module's parameters: `EmitScope.addParam`'s refusals.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestEmitScopeParams =

    /// A scope with `<Module>`, one type and `methods` methods of it, each `void (int32, int32)`.
    let private withMethods (methods : int) : EmitScope * MethodDefRid list =
        let scope = EmitScope.create "Params" Guid.Empty

        let typeDef, scope =
            match
                EmitScopeDefinitions.defineTopLevelTypeDef
                    "T"
                    (TypeAttributes.Public ||| TypeAttributes.Abstract)
                    None
                    []
                    scope
            with
            | TypeDefDefinition.Defined (rid, scope) -> rid, scope
            | TypeDefDefinition.Duplicate _ -> failwith "T is defined twice"

        let signature =
            ImmutableArray.Create<byte> [| 0x00uy ; 0x02uy ; 0x01uy ; 0x08uy ; 0x08uy |]

        let rids, scope =
            List.mapFold
                (fun scope i ->
                    EmitScopeDefinitions.defineMethod
                        typeDef
                        $"M%d{i}"
                        signature
                        (MethodAttributes.Public ||| MethodAttributes.Static)
                        scope
                )
                scope
                [ 1..methods ]

        scope, rids

    [<TestCase 0x10000>]
    [<TestCase -1>]
    let ``addParam refuses flags wider than the column's two bytes`` (flags : int) : unit =
        let scope, methods = withMethods 1

        Assert.Throws<ArgumentException> (fun () ->
            EmitScope.addParam
                {
                    Owner = List.head methods
                    Flags = enum<ParameterAttributes> flags
                    Sequence = 1us
                    Name = StringOffset 0
                }
                scope
            |> ignore<ParamRid * EmitScope>
        )
        |> ignore<ArgumentException>
