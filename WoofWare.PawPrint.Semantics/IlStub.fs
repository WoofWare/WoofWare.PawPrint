namespace WoofWare.PawPrint

open System.Collections.Immutable

/// One element of a stub's code, as the runtime's stub linker (`ILCodeStream`, stubgen.cpp) is given
/// it: an instruction, a branch to a label, or the label's position.
[<RequireQualifiedAccess>]
type IlStubCode =
    /// An instruction with no label operand.
    | Op of IlOp
    /// A long-form branch to the position `Mark target` names, which is the only form the stub
    /// linker emits: `br`, `brfalse`, `leave` and the rest, given the offset relative to the end of
    /// the branch.
    | Branch of make : (int32 -> UnaryConstIlOp) * target : string
    /// The position of the next instruction, named `label`.
    | Mark of label : string

/// The kind of handler an exception clause of a stub has.
[<RequireQualifiedAccess>]
type IlStubHandler =
    /// A `catch` of the type the clause names.
    | Catch of ExceptionCatchType
    | Finally

/// An exception clause of a stub, its bounds named by labels: the `try` runs from `TryStart` up to
/// `TryEnd`, and the handler from `HandlerStart` up to `HandlerEnd`.
type IlStubClause =
    {
        TryStart : string
        TryEnd : string
        HandlerStart : string
        HandlerEnd : string
        Handler : IlStubHandler
    }

/// Method bodies that a runtime writes itself rather than reads from an image.
[<RequireQualifiedAccess>]
module IlStub =

    /// The size of a branch, which is the size of its long form.
    let private branchSize : int =
        IlOp.NumberOfBytes (IlOp.UnaryConst (UnaryConstIlOp.Br 0))

    /// A body of `code` at the offsets its encodings give it, with `locals` as its local signature
    /// and `clauses` as its exception regions, inner clauses before the clauses enclosing them, as
    /// ECMA-335 II.19 orders them. Every label a branch or clause names must be marked exactly once.
    let assemble (locals : TypeDefn list) (code : IlStubCode list) (clauses : IlStubClause list) : MethodInstructions =
        let labels : Map<string, int> =
            ((0, Map.empty), code)
            ||> List.fold (fun (offset, labels) element ->
                match element with
                | IlStubCode.Op op -> offset + IlOp.NumberOfBytes op, labels
                | IlStubCode.Branch _ -> offset + branchSize, labels
                | IlStubCode.Mark label ->
                    if Map.containsKey label labels then
                        failwith $"logic error: a stub marks the label %s{label} twice"

                    offset, Map.add label offset labels
            )
            |> snd

        let at (label : string) : int =
            match Map.tryFind label labels with
            | Some offset -> offset
            | None -> failwith $"logic error: a stub names the label %s{label}, which it never marks"

        let located =
            ((0, []), code)
            ||> List.fold (fun (offset, acc) element ->
                match element with
                | IlStubCode.Op op -> offset + IlOp.NumberOfBytes op, (op, offset) :: acc
                | IlStubCode.Branch (make, target) ->
                    let next = offset + branchSize
                    let op = IlOp.UnaryConst (make (at target - next))

                    if IlOp.NumberOfBytes op <> branchSize then
                        failwith $"logic error: a stub's branch %O{op} is not a long-form branch"

                    next, (op, offset) :: acc
                | IlStubCode.Mark _ -> offset, acc
            )
            |> snd
            |> List.rev

        let regions =
            clauses
            |> List.map (fun clause ->
                let offsets =
                    {
                        TryOffset = at clause.TryStart
                        TryLength = at clause.TryEnd - at clause.TryStart
                        HandlerOffset = at clause.HandlerStart
                        HandlerLength = at clause.HandlerEnd - at clause.HandlerStart
                    }

                match clause.Handler with
                | IlStubHandler.Catch catchType -> ExceptionRegion.Catch (catchType, offsets)
                | IlStubHandler.Finally -> ExceptionRegion.Finally offsets
            )

        {
            Instructions = located
            Locations = located |> List.map (fun (op, offset) -> offset, op) |> Map.ofList
            // The stub linker sets no flags in the header it gives the JIT (`CreateILHeader`,
            // ilstubresolver.cpp).
            LocalsInit = false
            LocalVars =
                match locals with
                | [] -> None
                | locals -> Some (ImmutableArray.CreateRange locals)
            ExceptionRegions = ImmutableArray.CreateRange regions
        }

    /// A body of `instructions` at the offsets their encodings give them, with no locals and no
    /// exception regions.
    let ofInstructions (instructions : IlOp list) : MethodInstructions =
        assemble [] (instructions |> List.map IlStubCode.Op) []
