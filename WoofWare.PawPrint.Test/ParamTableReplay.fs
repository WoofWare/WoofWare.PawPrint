namespace WoofWare.PawPrint.Test

open System.Collections.Immutable

/// The reference model of CoreCLR's `Param` table as its read-write metadata keeps it while a dynamic module
/// is emitted (`md/enc/metamodelrw.cpp`): each MethodDef row's `ParamList`, the Param rows' (method,
/// Sequence), and the `ParamPtr` table once one exists. `addMethod` is `AddMethodRecord`, and `addParam` is
/// `AddChildRowDirectForParent`, `AddChildRowIndirectForParent` and `FixParamSequence`, step for step. It
/// models nothing once a call strands a MethodDef row: a pointer table beside a `ParamList` that still holds
/// the `END_OF_TABLE` marker, which real .NET then misreads.
[<RequireQualifiedAccess>]
module ParamTableReplay =

    /// `END_OF_TABLE`: a MethodDef row's `ParamList` before any parameter is added to it or after it,
    /// while no `ParamPtr` exists.
    [<Literal>]
    let EndOfTable : int = 0

    /// The tables, with every index 1-based as CoreCLR's.
    type State =
        private
            {
                /// Each MethodDef row's `ParamList`, by RID - 1.
                ParamList : ImmutableList<int>
                /// Each Param row's (method RID, Sequence), by RID - 1.
                Params : ImmutableList<int * uint16>
                /// The `ParamPtr` rows' Param RIDs, by index - 1, once the table exists.
                Pointers : ImmutableList<int> option
            }

    /// How a pointer table came to exist.
    [<RequireQualifiedAccess>]
    type TableStart =
        /// `AddChildRowDirectForParent` started it, because a later method already had parameters.
        | Direct
        /// `FixParamSequence` started it, because the new row slid ahead of an earlier one of its method.
        | Slide

    /// What one `addParam` did.
    [<RequireQualifiedAccess>]
    type Step =
        /// The row was added at `rid`. `started` says whether this call started the pointer table, and how;
        /// `slots` is how many of its method's rows the new row slid ahead of.
        | Added of rid : int * state : State * started : TableStart option * slots : int
        /// The call added its row, with that token, and left a pointer table beside a `ParamList` marker.
        | Stranded of rid : int

    let empty : State =
        {
            ParamList = ImmutableList.Empty
            Params = ImmutableList.Empty
            Pointers = None
        }

    let methodCount (state : State) : int = state.ParamList.Count

    let paramCount (state : State) : int = state.Params.Count

    /// The `ParamPtr` table's Param RIDs, if it exists.
    let pointers (state : State) : int list option = state.Pointers |> Option.map List.ofSeq

    /// Each Param row's (method RID, Sequence), in RID order.
    let paramRows (state : State) : (int * uint16) list = List.ofSeq state.Params

    /// `AddMethodRecord`: a new MethodDef row, whose `ParamList` is `NewRecordPointerEndValue`.
    let addMethod (state : State) : State =
        let value =
            match state.Pointers with
            | Some _ -> state.Params.Count + 1
            | None -> EndOfTable

        { state with
            ParamList = state.ParamList.Add value
        }

    /// `Impl_GetEndRidForColumn` for a MethodDef row's `ParamList`.
    let private endOfRun (paramList : int -> int) (methodRows : int) (paramRowCount : int) (md : int) : int =
        let next = if md < methodRows then paramList md else EndOfTable

        if next <> EndOfTable then next
        elif paramList (md - 1) = EndOfTable then EndOfTable
        else paramRowCount + 1

    /// The Param RIDs of a method's run, in the order the method lists them.
    let runOf (md : int) (state : State) : int list =
        if md < 1 || md > state.ParamList.Count then
            failwith $"ParamTableReplay.runOf: no MethodDef row %d{md} among %d{state.ParamList.Count}"

        let start = state.ParamList.[md - 1]

        let finish =
            endOfRun (fun i -> state.ParamList.[i]) state.ParamList.Count state.Params.Count md

        [
            for index in start .. finish - 1 do
                match state.Pointers with
                | Some pointers -> yield pointers.[index - 1]
                | None -> yield index
        ]

    /// `ConvertMarkerToEndOfTable`: from `ridParent` down, each row holding the marker gets `ixEnd`, until a
    /// row holds a real value.
    let private convertMarkerToEndOfTable (paramList : int[]) (ixEnd : int) (ridParent : int) : unit =
        let mutable rid = ridParent

        while rid > 0 && paramList.[rid - 1] = EndOfTable do
            paramList.[rid - 1] <- ixEnd
            rid <- rid - 1

    /// `DefineParam`'s row and `AddParamToMethod` for a parameter of method `md` with this sequence.
    let addParam (md : int) (sequence : uint16) (state : State) : Step =
        let methodRows = state.ParamList.Count

        if md < 1 || md > methodRows then
            failwith $"ParamTableReplay.addParam: no MethodDef row %d{md} among %d{methodRows}"

        // `AddParamRecord`, then the sequence: the row exists before its method lists it.
        let paramsAfter = state.Params.Add ((md, sequence))
        let n = paramsAfter.Count
        let paramList = Array.ofSeq state.ParamList

        let mutable pointers : ImmutableList<int>.Builder option =
            state.Pointers |> Option.map (fun table -> table.ToBuilder ())

        let mutable started : TableStart option = None

        // `AddChildRowDirectForParent`: true for `S_OK`, false for `S_FALSE`.
        let direct =
            match pointers with
            | Some _ -> false
            | None ->
                if paramList.[md - 1] = EndOfTable then
                    convertMarkerToEndOfTable paramList n md
                    true
                elif md = methodRows then
                    true
                else
                    let next = paramList.[md]

                    if next = EndOfTable then
                        true
                    elif next = n then
                        for i in methodRows .. -1 .. md + 1 do
                            paramList.[i - 1] <- next + 1

                        true
                    else
                        convertMarkerToEndOfTable paramList n methodRows
                        // `CreateIndirectTable` with one entry fewer than the Param rows.
                        pointers <- Some (ImmutableList.CreateRange([ 1 .. n - 1 ]).ToBuilder ())
                        started <- Some TableStart.Direct
                        false

        if not direct then
            let table = pointers.Value

            // `AddChildRowIndirectForParent`, then the new entry's column.
            if md = methodRows then
                table.Add n
            else
                let ixInsert = paramList.[md]

                if ixInsert = table.Count + 1 then
                    table.Add n
                elif ixInsert < 1 || ixInsert > table.Count then
                    failwith
                        $"ParamTableReplay.addParam: InsertRecord(%d{ixInsert}) into %d{table.Count} pointers, which CoreCLR refuses; the replay models nothing after a strand"
                else
                    table.Insert (ixInsert - 1, n)

                for i in methodRows .. -1 .. md + 1 do
                    paramList.[i - 1] <- paramList.[i - 1] + 1

        // `FixParamSequence`.
        let sequenceOf (rid : int) : uint16 = snd paramsAfter.[rid - 1]
        let ixStart = paramList.[md - 1]
        let mutable ixEnd = endOfRun (fun i -> paramList.[i]) methodRows n md

        if ixEnd <= ixStart then
            failwith
                $"ParamTableReplay.addParam: method %d{md}'s run [%d{ixStart}, %d{ixEnd}) is empty after its row was added"

        ixEnd <- ixEnd - 1

        let ridNew =
            match pointers with
            | Some table -> table.[ixEnd - 1]
            | None -> ixEnd

        let mutable slots = 0
        let mutable walking = true

        while walking && ixStart < ixEnd do
            let ridOld =
                match pointers with
                | Some table -> table.[ixEnd - 2]
                | None -> ixEnd - 1

            if sequenceOf ridNew < sequenceOf ridOld then
                slots <- slots + 1
                ixEnd <- ixEnd - 1
            else
                walking <- false

        if slots > 0 then
            if pointers.IsNone then
                // `CreateIndirectTable(TBL_Param, false)`, which converts no marker.
                pointers <- Some (ImmutableList.CreateRange([ 1..n ]).ToBuilder ())
                started <- Some TableStart.Slide

            let table = pointers.Value
            let endRid = endOfRun (fun i -> paramList.[i]) methodRows n md
            let mutable index = endRid - 1
            let mutable remaining = slots

            while remaining > 0 do
                table.[index - 1] <- table.[index - 2]
                remaining <- remaining - 1
                index <- index - 1

            table.[index - 1] <- ridNew

        let stranded =
            pointers.IsSome && paramList |> Array.exists (fun value -> value = EndOfTable)

        if stranded then
            Step.Stranded n
        else
            let after =
                {
                    ParamList = ImmutableList.CreateRange paramList
                    Params = paramsAfter
                    Pointers = pointers |> Option.map (fun table -> table.ToImmutable ())
                }

            Step.Added (n, after, started, slots)
