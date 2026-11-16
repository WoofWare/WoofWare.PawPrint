namespace WoofWare.PawPrint.Test

open Microsoft.Extensions.Logging
open WoofWare.PawPrint

/// Whether a closed type's interface map, built by `ConcreteInterfaceDispatch.interfaceMapOf`'s own
/// walk, agrees entry for entry with its definition's map (`interfaceMapOfDefinition`) closed under
/// the type's arguments: the same closed interface at each position, the same definition-rooted
/// substitution (by `substitutionsEquivalent`), and the same two flags.
[<RequireQualifiedAccess>]
module DefinitionInterfaceMapEquivalence =

    /// Each way the two maps of `receiver` disagree, described. The definition's map is closed in the
    /// state the walk left, so that both answers name the same handles.
    let disagreements
        (loggerFactory : ILoggerFactory)
        (dotnetRuntimeDirs : string seq)
        (baseClassTypes : BaseClassTypes<DumpedAssembly>)
        (state : TypeSystemState)
        (receiver : ConcreteTypeHandle)
        : TypeSystemState * string list
        =
        let state, walked =
            ConcreteInterfaceDispatch.interfaceMapOf
                loggerFactory
                dotnetRuntimeDirs
                baseClassTypes
                "test"
                state
                receiver

        let walked =
            match walked with
            | Ok walked -> walked
            | Error failure -> failwith $"%O{receiver}: interfaceMapOf failed: %A{failure}"

        let state, closed =
            ConcreteInterfaceDispatch.interfaceMapOfClosingDefinition
                loggerFactory
                dotnetRuntimeDirs
                baseClassTypes
                "test"
                state
                receiver

        let closed =
            match closed with
            | Ok closed -> closed
            | Error failure -> failwith $"%O{receiver}: interfaceMapOfClosingDefinition failed: %A{failure}"

        if walked.Length <> closed.Length then
            state,
            [
                $"%O{receiver}: interfaceMapOf has %d{walked.Length} entries, the closed definition map %d{closed.Length}"
            ]
        else

        ((state, []), List.indexed (List.zip walked closed))
        ||> List.fold (fun (state, failures) (i, (walkedEntry, (handle, interfaceTy, definitionEntry))) ->
            let state, equivalent =
                match
                    TypeSystemState.substitutionsEquivalent
                        loggerFactory
                        dotnetRuntimeDirs
                        baseClassTypes
                        state
                        walkedEntry.Substitution
                        definitionEntry.Substitution
                with
                | state, Ok equivalent -> state, equivalent
                | _, Error failure ->
                    failwith $"%O{receiver}: comparing entry %d{i}'s substitutions failed: %A{failure}"

            let mismatches =
                [
                    if walkedEntry.Interface <> handle then
                        yield $"interface %O{walkedEntry.Interface} against %O{handle}"
                    if walkedEntry.Type <> interfaceTy then
                        yield $"type %O{walkedEntry.Type} against %O{interfaceTy}"
                    if walkedEntry.Type.Identity <> definitionEntry.Interface then
                        yield "the definition entry names another interface"
                    if not (obj.ReferenceEquals (walkedEntry.TypeInfo, definitionEntry.TypeInfo)) then
                        yield "TypeInfo"
                    if not equivalent then
                        yield $"substitution %A{walkedEntry.Substitution} against %A{definitionEntry.Substitution}"
                    if walkedEntry.DeclaredOnType <> definitionEntry.DeclaredOnType then
                        yield
                            $"DeclaredOnType %b{walkedEntry.DeclaredOnType} against %b{definitionEntry.DeclaredOnType}"
                    if walkedEntry.ImplementedByParent <> definitionEntry.ImplementedByParent then
                        yield
                            $"ImplementedByParent %b{walkedEntry.ImplementedByParent} against %b{definitionEntry.ImplementedByParent}"
                ]

            let failures =
                mismatches
                |> List.map (fun mismatch -> $"%O{receiver} entry %d{i}: %s{mismatch}")
                |> List.append failures

            state, failures
        )
