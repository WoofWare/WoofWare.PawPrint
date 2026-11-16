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
        | AObject
        /// The declaring entity's own parameter.
        | AParam

    /// The variance of a generic interface's parameter.
    [<RequireQualifiedAccess>]
    type Variance =
        | Invariant
        /// `out`.
        | Covariant
        /// `in`.
        | Contravariant

    type InterfaceUse =
        {
            Interface : int
            /// `None` exactly when the interface is not generic.
            Argument : Argument option
        }

    type InterfaceDecl =
        {
            Generic : bool
            /// `Invariant` unless the interface is generic.
            Variance : Variance
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
    /// one in `inherited` or one already kept.
    let dropConflictsAgainst
        (interfaces : InterfaceDecl list)
        (inherited : InterfaceUse list)
        (declared : InterfaceUse list)
        : InterfaceUse list
        =
        let conflicts (a : InterfaceUse) (b : InterfaceUse) : bool =
            a.Interface = b.Interface && a.Argument <> b.Argument

        let kept, _ =
            (([], inherited), declared)
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

    /// Keep each declared interface whose closure names no interface with a different argument from
    /// one already kept: two instantiations of one interface could unify, which C# refuses.
    let dropConflicts (interfaces : InterfaceDecl list) (declared : InterfaceUse list) : InterfaceUse list =
        dropConflictsAgainst interfaces [] declared

    let canOverride (kind : OverrideKind) (target : MemberKind) : bool =
        match kind, target with
        | OverrideKind.ExplicitBody, (MemberKind.InstanceAbstract | MemberKind.DefaultBody)
        | OverrideKind.Reabstract, (MemberKind.InstanceAbstract | MemberKind.DefaultBody)
        | OverrideKind.StaticReabstract, (MemberKind.StaticAbstract | MemberKind.StaticVirtual) -> true
        | _ -> false

    /// `used` with an argument exactly when its interface is generic, naming a parameter only where
    /// the user, generic exactly when `generic` holds, has one.
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

    /// Make generated interfaces valid C#: no argument names a parameter its declarer does not have,
    /// no two instantiations of one interface in a closure, a member returns the parameter only of a
    /// generic interface and never of a contravariant one, a variant interface passes its parameter
    /// only to a base of the same variance, and each override names a method of an inherited
    /// interface which it can override and which nothing else in the corpus overrides.
    let repairInterfaces (declared : InterfaceDecl list) : InterfaceDecl list =
        let interfaces, _ =
            ((([] : InterfaceDecl list), (Set.empty : Set<int * int>)), declared)
            ||> List.fold (fun (earlier, overridden) decl ->
                let members =
                    decl.Members
                    |> List.map (fun m ->
                        if decl.Generic && decl.Variance <> Variance.Contravariant then
                            m
                        else
                            { m with
                                Returns = Returns.RInt
                            }
                    )

                // A variant parameter passed to an invariant base, or to one of the other variance, is
                // not variance-safe.
                let varianceSafe (used : InterfaceUse) : InterfaceUse =
                    match used.Argument with
                    | Some Argument.AParam when
                        decl.Variance <> Variance.Invariant
                        && earlier.[used.Interface].Variance <> decl.Variance
                        ->
                        { used with
                            Argument = Some Argument.AInt
                        }
                    | _ -> used

                let bases =
                    decl.Bases
                    |> List.filter (fun b -> b.Interface < earlier.Length)
                    |> List.map (fixUse decl.Generic earlier >> varianceSafe)
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
                        Variance = if decl.Generic then decl.Variance else Variance.Invariant
                        Members = members
                        Bases = bases
                        Overrides = List.rev overrides
                    }
                ],
                overridden
            )

        interfaces

    /// Make a generated corpus valid C#: its interfaces as `repairInterfaces` says, and no type
    /// implementing two instantiations of one interface.
    let repair (corpus : Corpus) : Corpus =
        let interfaces = repairInterfaces corpus.Interfaces

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

    /// `argument` in C#, in the context whose parameter is `parameter`.
    let argumentText (parameter : string) (argument : Argument) : string =
        match argument with
        | Argument.AInt -> "int"
        | Argument.AString -> "string"
        | Argument.AObject -> "object"
        | Argument.AParam -> parameter

    /// `used` in C#, in the context whose parameter is `parameter`.
    let useText (parameter : string) (used : InterfaceUse) : string =
        match used.Argument with
        | None -> interfaceName used.Interface
        | Some argument -> $"%s{interfaceName used.Interface}<%s{argumentText parameter argument}>"

    /// What a member of `used`'s interface returns, in the context whose parameter is `parameter`.
    let returnText (parameter : string) (used : InterfaceUse) (returns : Returns) : string =
        match returns, used.Argument with
        | Returns.RInt, _ -> "int"
        | Returns.RParam, Some argument -> argumentText parameter argument
        | Returns.RParam, None -> failwith "a non-generic interface's member returns a parameter"

    /// Append the C# declaration of every interface of `interfaces` to `builder`.
    let renderInterfaces (builder : Text.StringBuilder) (interfaces : InterfaceDecl list) : unit =
        interfaces
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

            let selfText =
                if decl.Generic then
                    let variance =
                        match decl.Variance with
                        | Variance.Invariant -> ""
                        | Variance.Covariant -> "out "
                        | Variance.Contravariant -> "in "

                    $"%s{interfaceName i}<%s{variance}P0>"
                else
                    interfaceName i

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

            let inherited = decl.Bases |> List.collect (closure interfaces)

            for o in decl.Overrides do
                let used = inherited |> List.find (fun u -> u.Interface = o.Interface)

                let target = interfaces.[o.Interface].Members.[o.Member]
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

    let render (corpus : Corpus) : string =
        let builder = Text.StringBuilder ()
        renderInterfaces builder corpus.Interfaces

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
                    Variance = Variance.Invariant
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

    // ----- Implementing corpora -----

    /// A generated class used as a base: its index, and its argument exactly when it is generic.
    type ClassUse =
        {
            Class : int
            Argument : Argument option
        }

    /// A class or struct of an implementing corpus.
    type ClassDecl =
        {
            IsStruct : bool
            /// Never for a struct.
            IsAbstract : bool
            Generic : bool
            /// An earlier class this one derives from. Never for a struct, and never a struct.
            Base : ClassUse option
            Implements : InterfaceUse list
            /// Consumed in turn, each choosing among the ways C# leaves open to supply the next member
            /// the type must supply.
            Choices : int list
        }

    /// Interfaces, and classes and structs implementing them in the ways C# allows.
    type ImplementingCorpus =
        {
            Interfaces : InterfaceDecl list
            Classes : ClassDecl list
        }

    /// How a class or struct supplies one member of an interface it implements.
    [<RequireQualifiedAccess>]
    type Supply =
        /// `R I.M() => default;`.
        | Explicit
        /// `public R M() => default;`.
        | Implicit
        /// `public virtual R M() => default;`.
        | Virtual
        /// `public abstract R M();`.
        | Abstract
        /// Nothing: a default body supplies it.
        | Default
        /// `public override R M() => default;`, of a base's virtual or abstract member.
        | Override
        /// `public new R M() => default;`, re-implementing a member of an interface the base implements.
        | New
        /// Nothing: the base class's implementation supplies it.
        | Inherit

    /// What a class leaves a class derived from it to do about one interface member.
    [<RequireQualifiedAccess>]
    type Supplied =
        /// Supplied by a method a derived class cannot override.
        | Final
        /// Supplied by a virtual method.
        | Overridable
        /// Supplied by an abstract method, which a concrete derived class must override.
        | MustOverride
        /// Supplied by a default body.
        | ByDefault

    /// What `planClasses` decides for one class.
    type ClassPlan =
        {
            /// Its interfaces in its own context, its base's included, each interface once.
            Closure : InterfaceUse list
            /// Keyed on interface and member index.
            Supplied : Map<int * int, Supplied>
            /// The members it declares, as C#.
            Lines : string list
        }

    /// Whether a type implementing an interface must supply a member of this kind, or could.
    let needsSupply (kind : MemberKind) : bool =
        match kind with
        | MemberKind.SealedBody
        | MemberKind.PrivateInstance
        | MemberKind.PlainStatic -> false
        | MemberKind.InstanceAbstract
        | MemberKind.DefaultBody
        | MemberKind.StaticAbstract
        | MemberKind.StaticVirtual
        | MemberKind.InstanceProperty
        | MemberKind.StaticProperty
        | MemberKind.InstanceEvent
        | MemberKind.StaticEvent
        | MemberKind.InstanceGeneric
        | MemberKind.StaticGeneric -> true

    let isStaticKind (kind : MemberKind) : bool =
        match kind with
        | MemberKind.StaticAbstract
        | MemberKind.StaticVirtual
        | MemberKind.PlainStatic
        | MemberKind.StaticProperty
        | MemberKind.StaticEvent
        | MemberKind.StaticGeneric -> true
        | MemberKind.InstanceAbstract
        | MemberKind.DefaultBody
        | MemberKind.SealedBody
        | MemberKind.PrivateInstance
        | MemberKind.InstanceProperty
        | MemberKind.InstanceEvent
        | MemberKind.InstanceGeneric -> false

    /// `used` with its parameter replaced by `outer`, an argument in the user's context.
    let substituteUse (outer : Argument option) (used : InterfaceUse) : InterfaceUse =
        { used with
            Argument = substitute outer used.Argument
        }

    /// The interfaces of `used`'s class, its bases' included, in the user's context.
    let rec classClosure
        (interfaces : InterfaceDecl list)
        (classes : ClassDecl list)
        (used : ClassUse)
        : InterfaceUse list
        =
        let decl = classes.[used.Class]

        let inherited =
            match decl.Base with
            | Some b -> classClosure interfaces classes b
            | None -> []

        inherited @ (decl.Implements |> List.collect (closure interfaces))
        |> List.map (substituteUse used.Argument)
        |> List.distinct

    /// Whether a type whose interfaces are `implemented` may leave member `k` of interface `i` to a
    /// default body: the member has one, or an interface among them gives it one explicitly, and none
    /// of them reabstracts it. No two interfaces of a repaired corpus override one member.
    let defaultSupplies (interfaces : InterfaceDecl list) (implemented : InterfaceUse list) (i : int) (k : int) : bool =
        let overrides =
            implemented
            |> List.collect (fun used -> interfaces.[used.Interface].Overrides)
            |> List.filter (fun o -> o.Interface = i && o.Member = k)
            |> List.distinct

        match overrides with
        | [] ->
            match interfaces.[i].Members.[k].Kind with
            | MemberKind.DefaultBody
            | MemberKind.StaticVirtual -> true
            | _ -> false
        | [ o ] -> o.Kind = OverrideKind.ExplicitBody
        | _ -> false

    /// Make an implementing corpus valid C#: its interfaces as `repairInterfaces` says; a base only
    /// for a class, naming an earlier class, with an argument exactly when that class is generic; no
    /// abstract struct; and no type implementing two instantiations of one interface, its base's
    /// interfaces included.
    let repairImplementing (corpus : ImplementingCorpus) : ImplementingCorpus =
        let interfaces = repairInterfaces corpus.Interfaces

        let classes =
            (([] : ClassDecl list), corpus.Classes)
            ||> List.fold (fun earlier decl ->
                let baseUse =
                    match decl.Base with
                    | Some b when not decl.IsStruct && b.Class < earlier.Length && not earlier.[b.Class].IsStruct ->
                        let argument =
                            if not earlier.[b.Class].Generic then
                                None
                            else
                                match b.Argument with
                                | Some Argument.AParam when not decl.Generic -> Some Argument.AInt
                                | None -> Some Argument.AInt
                                | other -> other

                        Some
                            {
                                Class = b.Class
                                Argument = argument
                            }
                    | _ -> None

                let inherited =
                    match baseUse with
                    | Some b -> classClosure interfaces earlier b
                    | None -> []

                let implements =
                    decl.Implements
                    |> List.map (fixUse decl.Generic interfaces)
                    |> dropConflictsAgainst interfaces inherited

                earlier
                @ [
                    { decl with
                        IsAbstract = decl.IsAbstract && not decl.IsStruct
                        Base = baseUse
                        Implements = implements
                    }
                ]
            )

        {
            Interfaces = interfaces
            Classes = classes
        }

    /// The declaration of member `k`, `m`, of `used` as `supply` gives it, in the context whose
    /// parameter is `parameter`; `None` where the supply declares nothing.
    let supplyLine (parameter : string) (used : InterfaceUse) (k : int) (m : Member) (supply : Supply) : string option =
        let r = returnText parameter used m.Returns
        let name = memberName m.Kind used.Interface k
        let explicitName = $"%s{useText parameter used}.%s{name}"
        let isStatic = isStaticKind m.Kind

        let withBody (prefix : string) (name : string) : string =
            match m.Kind with
            | MemberKind.InstanceProperty
            | MemberKind.StaticProperty -> $"%s{prefix}%s{r} %s{name} => default;"
            | MemberKind.InstanceEvent
            | MemberKind.StaticEvent -> $"%s{prefix}event System.Action %s{name} {{ add {{ }} remove {{ }} }}"
            | MemberKind.InstanceGeneric
            | MemberKind.StaticGeneric -> $"%s{prefix}void %s{name}<U>() {{ }}"
            | _ -> $"%s{prefix}%s{r} %s{name}() => default;"

        let withoutBody (prefix : string) (name : string) : string =
            match m.Kind with
            | MemberKind.InstanceProperty
            | MemberKind.StaticProperty -> $"%s{prefix}%s{r} %s{name} {{ get; }}"
            | MemberKind.InstanceEvent
            | MemberKind.StaticEvent -> $"%s{prefix}event System.Action %s{name};"
            | MemberKind.InstanceGeneric
            | MemberKind.StaticGeneric -> $"%s{prefix}void %s{name}<U>();"
            | _ -> $"%s{prefix}%s{r} %s{name}();"

        match supply with
        | Supply.Explicit -> Some (withBody (if isStatic then "static " else "") explicitName)
        | Supply.Implicit -> Some (withBody (if isStatic then "public static " else "public ") name)
        | Supply.Virtual -> Some (withBody "public virtual " name)
        | Supply.Abstract -> Some (withoutBody "public abstract " name)
        | Supply.Override -> Some (withBody "public override " name)
        | Supply.New -> Some (withBody (if isStatic then "public static new " else "public new ") name)
        | Supply.Default
        | Supply.Inherit -> None

    /// Decide, for each class of a repaired corpus in turn, how it supplies each member of each
    /// interface it implements, choosing among the ways C# leaves open by its `Choices`.
    let planClasses (corpus : ImplementingCorpus) : ClassPlan list =
        let interfaces = corpus.Interfaces

        (([] : ClassPlan list), corpus.Classes)
        ||> List.fold (fun plans decl ->
            let inherited, baseSupplied =
                match decl.Base with
                | Some b ->
                    let basePlan = plans.[b.Class]
                    basePlan.Closure |> List.map (substituteUse b.Argument), basePlan.Supplied
                | None -> [], Map.empty

            let inheritedIndices = inherited |> List.map _.Interface |> Set.ofList

            let own = decl.Implements |> List.collect (closure interfaces) |> List.distinct

            let relisted =
                own |> List.map _.Interface |> Set.ofList |> Set.intersect inheritedIndices

            let implemented =
                inherited
                @ (own |> List.filter (fun used -> not (inheritedIndices.Contains used.Interface)))

            let choose (turn : int) (options : Supply list) : Supply =
                match options, decl.Choices with
                | [], _ -> failwith "BUG: a generated type has no way to supply an interface member"
                | options, [] -> options.Head
                | options, choices -> options.[choices.[turn % choices.Length] % options.Length]

            let decisions =
                implemented
                |> List.collect (fun used ->
                    interfaces.[used.Interface].Members
                    |> List.indexed
                    |> List.filter (fun (_, m) -> needsSupply m.Kind)
                    |> List.map (fun (k, m) -> used, k, m)
                )
                |> List.mapi (fun turn (used, k, m) ->
                    let key = used.Interface, k
                    let defaults = defaultSupplies interfaces implemented used.Interface k

                    let options =
                        match Map.tryFind key baseSupplied with
                        | Some supplied ->
                            let relist =
                                if relisted.Contains used.Interface then
                                    [ Supply.Explicit ; Supply.New ]
                                else
                                    []

                            match supplied with
                            | Supplied.MustOverride when decl.IsAbstract -> [ Supply.Inherit ; Supply.Override ]
                            | Supplied.MustOverride -> [ Supply.Override ]
                            | Supplied.Overridable -> [ Supply.Inherit ; Supply.Override ] @ relist
                            | Supplied.Final -> Supply.Inherit :: relist
                            | Supplied.ByDefault -> (if defaults then [ Supply.Inherit ] else []) @ relist
                        | None ->
                            [ Supply.Explicit ; Supply.Implicit ]
                            @ (if decl.IsStruct || isStaticKind m.Kind then
                                   []
                               else
                                   [ Supply.Virtual ])
                            @ (if decl.IsAbstract && not (isStaticKind m.Kind) then
                                   [ Supply.Abstract ]
                               else
                                   [])
                            @ (if defaults then [ Supply.Default ] else [])

                    let supply = choose turn options

                    let supplied =
                        match supply with
                        | Supply.Explicit
                        | Supply.Implicit
                        | Supply.New -> Supplied.Final
                        | Supply.Virtual
                        | Supply.Override -> Supplied.Overridable
                        | Supply.Abstract -> Supplied.MustOverride
                        | Supply.Default -> Supplied.ByDefault
                        | Supply.Inherit -> baseSupplied.[key]

                    key, supplied, supplyLine "T" used k m supply
                )

            plans
            @ [
                {
                    Closure = implemented
                    Supplied = decisions |> List.map (fun (key, supplied, _) -> key, supplied) |> Map.ofList
                    Lines = decisions |> List.choose (fun (_, _, line) -> line)
                }
            ]
        )

    /// The C# source of a repaired implementing corpus.
    let renderImplementing (corpus : ImplementingCorpus) : string =
        let builder = Text.StringBuilder ()
        renderInterfaces builder corpus.Interfaces

        for index, (decl, plan) in List.indexed (List.zip corpus.Classes (planClasses corpus)) do
            let self =
                if decl.Generic then
                    $"%s{typeName index}<T>"
                else
                    typeName index

            let baseText =
                decl.Base
                |> Option.map (fun b ->
                    match b.Argument with
                    | Some argument ->
                        let argument = argumentText "T" argument
                        $"%s{typeName b.Class}<%s{argument}>"
                    | None -> typeName b.Class
                )
                |> Option.toList

            let parents =
                match baseText @ (decl.Implements |> List.map (useText "T")) with
                | [] -> ""
                | parents -> " : " + String.concat ", " parents

            let kind =
                if decl.IsStruct then "struct"
                elif decl.IsAbstract then "abstract class"
                else "class"

            builder.Append ($"public %s{kind} %s{self}%s{parents}\n{{\n") |> ignore

            for line in plan.Lines do
                builder.Append ($"    %s{line}\n") |> ignore

            builder.Append "}\n" |> ignore

        builder.ToString ()

    let genImplementingUse (count : int) : Gen<InterfaceUse> =
        Gen.map2
            (fun index argument ->
                {
                    Interface = index
                    Argument = Some argument
                }
            )
            (Gen.choose (0, count - 1))
            (Gen.elements [ Argument.AInt ; Argument.AString ; Argument.AObject ; Argument.AParam ])

    /// Two interfaces some implementing corpora begin with, so that a default body on a more specific
    /// interface and an interface's own reabstraction are common: `I0 { int M0_0(); int M0_1() =>
    /// default; }` and `I1 : I0 { int I0.M0_0() => default; abstract int I0.M0_1(); }`.
    let private openingInterfaces : InterfaceDecl list =
        [
            {
                Generic = false
                Variance = Variance.Invariant
                Members =
                    [
                        {
                            Kind = MemberKind.InstanceAbstract
                            Returns = Returns.RInt
                        }
                        {
                            Kind = MemberKind.DefaultBody
                            Returns = Returns.RInt
                        }
                    ]
                Bases = []
                Overrides = []
            }
            {
                Generic = false
                Variance = Variance.Invariant
                Members = []
                Bases =
                    [
                        {
                            Interface = 0
                            Argument = None
                        }
                    ]
                Overrides =
                    [
                        {
                            Interface = 0
                            Member = 0
                            Kind = OverrideKind.ExplicitBody
                        }
                        {
                            Interface = 0
                            Member = 1
                            Kind = OverrideKind.Reabstract
                        }
                    ]
            }
        ]

    let genImplementingCorpus : Gen<ImplementingCorpus> =
        gen {
            // A third of the corpora begin with `openingInterfaces`, and their first type implements `I1`.
            let! opening = Gen.frequency [ 2, Gen.constant [] ; 1, Gen.constant openingInterfaces ]
            let! generatedCount = Gen.choose (1, 4)
            let interfaceCount = opening.Length + generatedCount

            let! generated =
                [ opening.Length .. interfaceCount - 1 ]
                |> List.map (fun index ->
                    Gen.map2
                        (fun (decl : InterfaceDecl) variance ->
                            { decl with
                                Variance = variance
                            }
                        )
                        (genInterfaceDecl index)
                        (Gen.elements [ Variance.Invariant ; Variance.Covariant ; Variance.Contravariant ])
                )
                |> Gen.sequenceToList

            let! classCount = Gen.choose (1, 4)

            let! classes =
                [ 0 .. classCount - 1 ]
                |> List.map (fun index ->
                    gen {
                        let! isStruct = Gen.frequency [ 1, Gen.constant true ; 3, Gen.constant false ]
                        let! isAbstract = Gen.frequency [ 1, Gen.constant true ; 3, Gen.constant false ]
                        let! generic = Gen.elements [ false ; true ]

                        let! baseUse =
                            if index = 0 then
                                Gen.constant None
                            else
                                Gen.frequency
                                    [
                                        1, Gen.constant None
                                        2,
                                        Gen.map2
                                            (fun c argument ->
                                                Some
                                                    {
                                                        Class = c
                                                        Argument = Some argument
                                                    }
                                            )
                                            (Gen.choose (0, index - 1))
                                            (Gen.elements [ Argument.AInt ; Argument.AString ; Argument.AParam ])
                                    ]

                        let! implementCount = Gen.choose (0, 3)
                        let! implements = Gen.listOfLength implementCount (genImplementingUse interfaceCount)
                        let! choices = Gen.listOfLength 16 (Gen.choose (0, 999))

                        let implements =
                            if index = 0 && not opening.IsEmpty then
                                {
                                    Interface = 1
                                    Argument = None
                                }
                                :: implements
                            else
                                implements

                        return
                            {
                                IsStruct = isStruct
                                IsAbstract = isAbstract
                                Generic = generic
                                Base = baseUse
                                Implements = implements
                                Choices = choices
                            }
                    }
                )
                |> Gen.sequenceToList

            return
                repairImplementing
                    {
                        Interfaces = opening @ generated
                        Classes = classes
                    }
        }

    /// Each class and struct of `corpus`, loaded as `host`, a generic one as its definition and at
    /// `int` and at `string`, with each interface it implements and each variance-compatible
    /// instantiation of a variant one it does not: at `object` for a covariant interface it
    /// implements at `string`, and at `string` for a contravariant one it implements at `object`.
    let implementingPairs (corpus : ImplementingCorpus) (host : Reflection.Assembly) : (Type * Type) list =
        let hostType (name : string) : Type =
            match host.GetType name with
            | null -> failwith $"generated type %s{name} not found"
            | t -> t

        let receivers =
            corpus.Classes
            |> List.mapi (fun index decl ->
                if decl.Generic then
                    let definition = hostType $"%s{typeName index}`1"

                    [
                        definition
                        definition.MakeGenericType typeof<int>
                        definition.MakeGenericType typeof<string>
                    ]
                else
                    [ hostType (typeName index) ]
            )
            |> List.concat

        let varianceOf (i : Type) : Variance =
            if i.IsConstructedGenericType && Object.ReferenceEquals (i.Assembly, host) then
                let name = i.GetGenericTypeDefinition().Name
                corpus.Interfaces.[int (name.Substring (1, name.IndexOf '`' - 1))].Variance
            else
                Variance.Invariant

        [
            for receiver in receivers do
                let implemented = receiver.GetInterfaces ()

                for i in implemented do
                    yield receiver, i

                let compatible =
                    implemented
                    |> Array.choose (fun i ->
                        match varianceOf i with
                        | Variance.Covariant when i.GenericTypeArguments.[0] = typeof<string> ->
                            Some (i.GetGenericTypeDefinition().MakeGenericType typeof<obj>)
                        | Variance.Contravariant when i.GenericTypeArguments.[0] = typeof<obj> ->
                            Some (i.GetGenericTypeDefinition().MakeGenericType typeof<string>)
                        | _ -> None
                    )
                    |> Array.distinct
                    |> Array.filter (fun i -> not (Array.contains i implemented))

                for i in compatible do
                    yield receiver, i
        ]
