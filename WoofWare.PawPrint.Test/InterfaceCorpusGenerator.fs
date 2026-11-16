namespace WoofWare.PawPrint.Test

open System
open FsCheck
open FsCheck.FSharp

/// A generator of small C# libraries: interfaces with every kind of member a virtual slot or a static
/// virtual can come from, deriving from one another and overriding or reabstracting what they inherit,
/// and classes and structs implementing them. `render` gives the C# source, and `pool` the host
/// types a property asks about once the source is compiled and loaded.
module InterfaceCorpusGenerator =

    /// What a generated member returns: `int`, or the declaring interface's parameter.
    type Returns =
        | RInt
        | RParam

    type MemberKind =
        | InstanceAbstract
        | DefaultBody
        | SealedBody
        | PrivateInstance
        | StaticAbstract
        | StaticVirtual
        | PlainStatic
        | InstanceProperty
        | StaticProperty
        | InstanceEvent
        | StaticEvent
        | InstanceGeneric
        | StaticGeneric

    type Member =
        {
            Kind : MemberKind
            Returns : Returns
        }

    /// How a derived interface overrides a method of an interface it inherits.
    type OverrideKind =
        /// An explicit default body, `R IBase.M() => default;`.
        | ExplicitBody
        /// An instance reabstraction, `abstract R IBase.M();`.
        | Reabstract
        /// A static reabstraction, `static abstract R IBase.M();`.
        | StaticReabstract

    type Override =
        {
            Interface : int
            Member : int
            Kind : OverrideKind
        }

    /// A generic argument in a generated declaration.
    type Argument =
        | AInt
        | AString
        /// The declaring entity's own parameter.
        | AParam

    type InterfaceUse =
        {
            Interface : int
            /// `None` exactly when the interface is not generic.
            Argument : Argument option
        }

    type InterfaceDecl =
        {
            Generic : bool
            Members : Member list
            Bases : InterfaceUse list
            Overrides : Override list
        }

    type TypeDecl =
        {
            IsStruct : bool
            Generic : bool
            Implements : InterfaceUse list
        }

    type Corpus =
        {
            Interfaces : InterfaceDecl list
            Types : TypeDecl list
        }

    let substitute (outer : Argument option) (argument : Argument option) : Argument option =
        match argument with
        | Some Argument.AParam -> outer
        | other -> other

    /// `used` and every interface it inherits, with arguments in the user's context.
    let rec closure (interfaces : InterfaceDecl list) (used : InterfaceUse) : InterfaceUse list =
        used
        :: (interfaces.[used.Interface].Bases
            |> List.collect (fun b ->
                closure
                    interfaces
                    { b with
                        Argument = substitute used.Argument b.Argument
                    }
            ))

    /// Keep each declared interface whose closure names no interface with a different argument from
    /// one already kept: two instantiations of one interface could unify, which C# refuses.
    let dropConflicts (interfaces : InterfaceDecl list) (declared : InterfaceUse list) : InterfaceUse list =
        let conflicts (a : InterfaceUse) (b : InterfaceUse) : bool =
            a.Interface = b.Interface && a.Argument <> b.Argument

        let kept, _ =
            (([], []), declared)
            ||> List.fold (fun (kept, seen) used ->
                let mine = closure interfaces used

                if List.contains used kept then
                    kept, seen
                elif mine |> List.exists (fun m -> seen |> List.exists (conflicts m)) then
                    kept, seen
                elif mine |> List.exists (fun m -> mine |> List.exists (conflicts m)) then
                    kept, seen
                else
                    used :: kept, mine @ seen
            )

        List.rev kept

    let canOverride (kind : OverrideKind) (target : MemberKind) : bool =
        match kind, target with
        | OverrideKind.ExplicitBody, (MemberKind.InstanceAbstract | MemberKind.DefaultBody)
        | OverrideKind.Reabstract, (MemberKind.InstanceAbstract | MemberKind.DefaultBody)
        | OverrideKind.StaticReabstract, (MemberKind.StaticAbstract | MemberKind.StaticVirtual) -> true
        | _ -> false

    /// Make a generated corpus valid C#: no argument names a parameter its declarer does not have, no
    /// two instantiations of one interface in a closure, a member returns the parameter only of a
    /// generic interface, and each override names a method of an inherited interface which it can
    /// override and which nothing else in the corpus overrides.
    let repair (corpus : Corpus) : Corpus =
        let fixUse (generic : bool) (interfaces : InterfaceDecl list) (used : InterfaceUse) : InterfaceUse =
            let argument =
                if not interfaces.[used.Interface].Generic then
                    None
                else
                    match used.Argument with
                    | Some Argument.AParam when not generic -> Some Argument.AInt
                    | None -> Some Argument.AInt
                    | other -> other

            { used with
                Argument = argument
            }

        let interfaces, _ =
            ((([] : InterfaceDecl list), (Set.empty : Set<int * int>)), corpus.Interfaces)
            ||> List.fold (fun (earlier, overridden) decl ->
                let members =
                    decl.Members
                    |> List.map (fun m ->
                        if decl.Generic then
                            m
                        else
                            { m with
                                Returns = Returns.RInt
                            }
                    )

                let bases =
                    decl.Bases
                    |> List.filter (fun b -> b.Interface < earlier.Length)
                    |> List.map (fixUse decl.Generic earlier)
                    |> dropConflicts earlier

                let inherited =
                    bases |> List.collect (closure earlier) |> List.map _.Interface |> Set.ofList

                let overrides, overridden =
                    ((([] : Override list), overridden), decl.Overrides)
                    ||> List.fold (fun (kept, overridden) o ->
                        if
                            Set.contains o.Interface inherited
                            && o.Member < earlier.[o.Interface].Members.Length
                            && canOverride o.Kind earlier.[o.Interface].Members.[o.Member].Kind
                            && not (Set.contains (o.Interface, o.Member) overridden)
                        then
                            o :: kept, Set.add (o.Interface, o.Member) overridden
                        else
                            kept, overridden
                    )

                earlier
                @ [
                    {
                        Generic = decl.Generic
                        Members = members
                        Bases = bases
                        Overrides = List.rev overrides
                    }
                ],
                overridden
            )

        let types =
            corpus.Types
            |> List.map (fun decl ->
                { decl with
                    Implements =
                        decl.Implements
                        |> List.map (fixUse decl.Generic interfaces)
                        |> dropConflicts interfaces
                }
            )

        {
            Interfaces = interfaces
            Types = types
        }

    let interfaceName (index : int) : string = $"I%d{index}"
    let typeName (index : int) : string = $"D%d{index}"

    let memberName (kind : MemberKind) (i : int) (k : int) : string =
        match kind with
        | MemberKind.InstanceProperty
        | MemberKind.StaticProperty -> $"P%d{i}_%d{k}"
        | MemberKind.InstanceEvent
        | MemberKind.StaticEvent -> $"E%d{i}_%d{k}"
        | MemberKind.InstanceGeneric
        | MemberKind.StaticGeneric -> $"G%d{i}_%d{k}"
        | _ -> $"M%d{i}_%d{k}"

    let render (corpus : Corpus) : string =
        let builder = Text.StringBuilder ()

        let argumentText (parameter : string) (argument : Argument) : string =
            match argument with
            | Argument.AInt -> "int"
            | Argument.AString -> "string"
            | Argument.AParam -> parameter

        let useText (parameter : string) (used : InterfaceUse) : string =
            match used.Argument with
            | None -> interfaceName used.Interface
            | Some argument -> $"%s{interfaceName used.Interface}<%s{argumentText parameter argument}>"

        // What a member of `used`'s interface returns, in the context whose parameter is `parameter`.
        let returnText (parameter : string) (used : InterfaceUse) (returns : Returns) : string =
            match returns, used.Argument with
            | Returns.RInt, _ -> "int"
            | Returns.RParam, Some argument -> argumentText parameter argument
            | Returns.RParam, None -> failwith "a non-generic interface's member returns a parameter"

        corpus.Interfaces
        |> List.iteri (fun i decl ->
            let self =
                {
                    Interface = i
                    Argument = if decl.Generic then Some Argument.AParam else None
                }

            let bases =
                match decl.Bases with
                | [] -> ""
                | bs -> " : " + (bs |> List.map (useText "P0") |> String.concat ", ")

            let selfText = useText "P0" self
            builder.Append ($"public interface %s{selfText}%s{bases}\n{{\n") |> ignore

            decl.Members
            |> List.iteri (fun k m ->
                let r = returnText "P0" self m.Returns
                let name = memberName m.Kind i k

                let text =
                    match m.Kind with
                    | MemberKind.InstanceAbstract -> $"%s{r} %s{name}();"
                    | MemberKind.DefaultBody -> $"%s{r} %s{name}() => default;"
                    | MemberKind.SealedBody -> $"sealed %s{r} %s{name}() => default;"
                    | MemberKind.PrivateInstance -> $"private %s{r} %s{name}() => default;"
                    | MemberKind.StaticAbstract -> $"static abstract %s{r} %s{name}();"
                    | MemberKind.StaticVirtual -> $"static virtual %s{r} %s{name}() => default;"
                    | MemberKind.PlainStatic -> $"static %s{r} %s{name}() => default;"
                    | MemberKind.InstanceProperty -> $"%s{r} %s{name} {{ get; }}"
                    | MemberKind.StaticProperty -> $"static abstract %s{r} %s{name} {{ get; }}"
                    | MemberKind.InstanceEvent -> $"event System.Action %s{name};"
                    | MemberKind.StaticEvent -> $"static abstract event System.Action %s{name};"
                    | MemberKind.InstanceGeneric -> $"void %s{name}<U>();"
                    | MemberKind.StaticGeneric -> $"static abstract void %s{name}<U>();"

                builder.Append ($"    %s{text}\n") |> ignore
            )

            let inherited = decl.Bases |> List.collect (closure corpus.Interfaces)

            for o in decl.Overrides do
                let used = inherited |> List.find (fun u -> u.Interface = o.Interface)

                let target = corpus.Interfaces.[o.Interface].Members.[o.Member]
                let r = returnText "P0" used target.Returns
                let usedText = useText "P0" used
                let name = $"%s{usedText}.%s{memberName target.Kind o.Interface o.Member}"

                let text =
                    match o.Kind with
                    | OverrideKind.ExplicitBody -> $"%s{r} %s{name}() => default;"
                    | OverrideKind.Reabstract -> $"abstract %s{r} %s{name}();"
                    | OverrideKind.StaticReabstract -> $"static abstract %s{r} %s{name}();"

                builder.Append ($"    %s{text}\n") |> ignore

            builder.Append "}\n" |> ignore
        )

        corpus.Types
        |> List.iteri (fun index decl ->
            let self =
                if decl.Generic then
                    $"%s{typeName index}<T>"
                else
                    typeName index

            let parents =
                match decl.Implements with
                | [] -> ""
                | uses -> " : " + (uses |> List.map (useText "T") |> String.concat ", ")

            let kind = if decl.IsStruct then "struct" else "class"
            builder.Append ($"public %s{kind} %s{self}%s{parents}\n{{\n") |> ignore

            // Every virtual member of every interface it inherits, explicitly, so that no member's most
            // specific implementation is ambiguous.
            for used in decl.Implements |> List.collect (closure corpus.Interfaces) |> List.distinct do
                let owner = useText "T" used

                corpus.Interfaces.[used.Interface].Members
                |> List.iteri (fun k m ->
                    let r = returnText "T" used m.Returns
                    let name = $"%s{owner}.%s{memberName m.Kind used.Interface k}"

                    let text =
                        match m.Kind with
                        | MemberKind.InstanceAbstract
                        | MemberKind.DefaultBody -> Some $"%s{r} %s{name}() => default;"
                        | MemberKind.StaticAbstract
                        | MemberKind.StaticVirtual -> Some $"static %s{r} %s{name}() => default;"
                        | MemberKind.InstanceProperty -> Some $"%s{r} %s{name} => default;"
                        | MemberKind.StaticProperty -> Some $"static %s{r} %s{name} => default;"
                        | MemberKind.InstanceEvent ->
                            Some $"event System.Action %s{name} {{ add {{ }} remove {{ }} }}"
                        | MemberKind.StaticEvent ->
                            Some $"static event System.Action %s{name} {{ add {{ }} remove {{ }} }}"
                        | MemberKind.InstanceGeneric -> Some $"void %s{name}<U>() {{ }}"
                        | MemberKind.StaticGeneric -> Some $"static void %s{name}<U>() {{ }}"
                        | MemberKind.SealedBody
                        | MemberKind.PrivateInstance
                        | MemberKind.PlainStatic -> None

                    match text with
                    | Some text -> builder.Append ($"    %s{text}\n") |> ignore
                    | None -> ()
                )

            builder.Append "}\n" |> ignore
        )

        builder.ToString ()

    let genMember : Gen<Member> =
        Gen.map2
            (fun kind returns ->
                {
                    Kind = kind
                    Returns = returns
                }
            )
            (Gen.elements
                [
                    MemberKind.InstanceAbstract
                    MemberKind.DefaultBody
                    MemberKind.SealedBody
                    MemberKind.PrivateInstance
                    MemberKind.StaticAbstract
                    MemberKind.StaticVirtual
                    MemberKind.PlainStatic
                    MemberKind.InstanceProperty
                    MemberKind.StaticProperty
                    MemberKind.InstanceEvent
                    MemberKind.StaticEvent
                    MemberKind.InstanceGeneric
                    MemberKind.StaticGeneric
                ])
            (Gen.elements [ Returns.RInt ; Returns.RParam ])

    let genUse (count : int) : Gen<InterfaceUse> =
        Gen.map2
            (fun index argument ->
                {
                    Interface = index
                    Argument = Some argument
                }
            )
            (Gen.choose (0, count - 1))
            (Gen.elements [ Argument.AInt ; Argument.AString ; Argument.AParam ])

    let genInterfaceDecl (index : int) : Gen<InterfaceDecl> =
        gen {
            let! generic = Gen.elements [ false ; true ]
            let! memberCount = Gen.choose (0, 4)
            let! members = Gen.listOfLength memberCount genMember

            let! bases =
                if index = 0 then
                    Gen.constant []
                else
                    Gen.choose (0, 2) |> Gen.bind (fun n -> Gen.listOfLength n (genUse index))

            let! overrides =
                if index = 0 then
                    Gen.constant []
                else
                    Gen.choose (0, 3)
                    |> Gen.bind (fun n ->
                        Gen.listOfLength
                            n
                            (Gen.map3
                                (fun i k kind ->
                                    {
                                        Interface = i
                                        Member = k
                                        Kind = kind
                                    }
                                )
                                (Gen.choose (0, index - 1))
                                (Gen.choose (0, 3))
                                (Gen.elements
                                    [
                                        OverrideKind.ExplicitBody
                                        OverrideKind.Reabstract
                                        OverrideKind.StaticReabstract
                                    ]))
                    )

            return
                {
                    Generic = generic
                    Members = members
                    Bases = bases
                    Overrides = overrides
                }
        }

    let genCorpus : Gen<Corpus> =
        gen {
            let! interfaceCount = Gen.choose (1, 4)

            let! interfaces = [ 0 .. interfaceCount - 1 ] |> List.map genInterfaceDecl |> Gen.sequenceToList

            let! typeCount = Gen.choose (1, 3)

            let! types =
                Gen.listOfLength
                    typeCount
                    (gen {
                        let! isStruct = Gen.elements [ false ; true ]
                        let! generic = Gen.elements [ false ; true ]
                        let! implementCount = Gen.choose (0, 3)
                        let! implements = Gen.listOfLength implementCount (genUse interfaceCount)

                        return
                            {
                                IsStruct = isStruct
                                Generic = generic
                                Implements = implements
                            }
                    })

            return
                repair
                    {
                        Interfaces = interfaces
                        Types = types
                    }
        }

    let listT : Type =
        typedefof<System.Collections.Generic.List<obj>>.GetGenericArguments().[0]

    /// Each interface's definition, its instantiations over `int` and `string`, and an open
    /// construction over a class's own parameter; each class and struct, and an instantiation of each
    /// generic one; and an array of each closed type among them.
    let pool (corpus : Corpus) (host : Reflection.Assembly) : Type list =
        let hostType (name : string) : Type =
            match host.GetType name with
            | null -> failwith $"generated type %s{name} not found"
            | t -> t

        let variable =
            corpus.Types
            |> List.indexed
            |> List.tryPick (fun (index, decl) ->
                if decl.Generic then
                    Some ((hostType $"%s{typeName index}`1").GetGenericArguments().[0])
                else
                    None
            )
            |> Option.defaultValue listT

        let interfaces =
            corpus.Interfaces
            |> List.mapi (fun index decl ->
                if decl.Generic then
                    let definition = hostType $"%s{interfaceName index}`1"

                    [
                        definition
                        definition.MakeGenericType typeof<int>
                        definition.MakeGenericType typeof<string>
                        definition.MakeGenericType variable
                    ]
                else
                    [ hostType (interfaceName index) ]
            )
            |> List.concat

        let types =
            corpus.Types
            |> List.mapi (fun index decl ->
                if decl.Generic then
                    let definition = hostType $"%s{typeName index}`1"
                    [ definition ; definition.MakeGenericType typeof<int> ]
                else
                    [ hostType (typeName index) ]
            )
            |> List.concat

        let all = interfaces @ types

        all
        @ (all
           |> List.filter (fun t -> not t.ContainsGenericParameters)
           |> List.map _.MakeArrayType())
