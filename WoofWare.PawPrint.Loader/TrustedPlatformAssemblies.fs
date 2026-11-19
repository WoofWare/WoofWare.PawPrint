namespace WoofWare.PawPrint

open System

/// Why `TrustedPlatformAssemblies.tryCreate` refused a map from simple name to path.
[<RequireQualifiedAccess>]
type internal TpaRefusal =
    /// The path does not start with `/`. CoreCLR refuses a TPA holding a relative path, and the
    /// runtime does not start.
    | RelativePath of name : string * path : string
    /// The path holds `:`, which separates the entries of the host's TPA string.
    | HoldsPathListSeparator of name : string * path : string
    /// The path ends with a space, which CoreCLR drops from each entry of the TPA string.
    | EndsWithSpace of name : string * path : string
    /// The path does not end `.dll`, `.exe`, `.ni.dll` or `.ni.exe` in any ASCII case, so CoreCLR
    /// reads no simple name from it, and the runtime does not start.
    | NoAssemblyExtension of name : string * path : string
    /// The path ends `.exe`, `.ni.dll` or `.ni.exe`, in any ASCII case. PawPrint binds only `.dll`
    /// files.
    | NotDll of name : string * path : string
    /// The name is not the path's file name without its `.dll`, which is the name CoreCLR binds
    /// the path by.
    | NameIsNotFileStem of name : string * path : string
    /// Two names are equal ignoring case (`first` before `second` in ordinal order). CoreCLR
    /// binds both by whichever the host's TPA string holds first, and the host does not specify
    /// that order.
    | NamesEqualIgnoringCase of first : string * second : string

/// The trusted platform assemblies: the map from simple name to absolute `.dll` path which the
/// host gives CoreCLR, and from which CoreCLR binds a name. Build one with
/// `TrustedPlatformAssemblies.tryCreate`.
type internal TrustedPlatformAssemblies =
    private
        {
            Paths : Map<string, string>
        }

[<RequireQualifiedAccess>]
module internal TrustedPlatformAssemblies =

    /// Whether CoreCLR's case-insensitive map could take `a` and `b` for one name. CoreCLR folds
    /// case with its own tables, so this errs towards a collision: equal under either invariant
    /// casing.
    let private equalIgnoringCase (a : string) (b : string) : bool =
        String.Equals (a.ToUpperInvariant (), b.ToUpperInvariant (), StringComparison.Ordinal)
        || String.Equals (a.ToLowerInvariant (), b.ToLowerInvariant (), StringComparison.Ordinal)

    /// `OrdinalIgnoreCase` folds no non-ASCII character to an ASCII one, so against an ASCII
    /// suffix it ignores ASCII case alone, as CoreCLR's `EndsWithCaseInsensitive` does there.
    let private endsWithIgnoringCase (suffix : string) (path : string) : bool =
        path.EndsWith (suffix, StringComparison.OrdinalIgnoreCase)

    /// What `GetNextTPAPath` (`src/coreclr/binder/utils.cpp`) makes of `path`, and whether it
    /// gives `name`.
    let private checkEntry (name : string) (path : string) : TpaRefusal option =
        if not (path.StartsWith '/') then
            Some (TpaRefusal.RelativePath (name, path))
        elif path.Contains ':' then
            Some (TpaRefusal.HoldsPathListSeparator (name, path))
        elif path.EndsWith ' ' then
            Some (TpaRefusal.EndsWithSpace (name, path))
        elif not (endsWithIgnoringCase ".dll" path || endsWithIgnoringCase ".exe" path) then
            Some (TpaRefusal.NoAssemblyExtension (name, path))
        elif endsWithIgnoringCase ".exe" path || endsWithIgnoringCase ".ni.dll" path then
            Some (TpaRefusal.NotDll (name, path))
        else
            // The path ends `.dll`, so its file name is at least that long.
            let fileName = path.Substring (path.LastIndexOf '/' + 1)

            if String.Equals (fileName.Substring (0, fileName.Length - 4), name, StringComparison.Ordinal) then
                None
            else
                Some (TpaRefusal.NameIsNotFileStem (name, path))

    /// A TPA mapping each name of `paths` to its path, or why CoreCLR would not bind it so.
    /// Each name must be its path's file name without `.dll`; each path absolute, free of `:`, not
    /// ending with a space; and no two names equal ignoring case. The entries are checked in
    /// ordinal order of name, and the first refusal is returned.
    let tryCreate (paths : Map<string, string>) : Result<TrustedPlatformAssemblies, TpaRefusal> =
        match paths |> Map.toSeq |> Seq.tryPick (fun (name, path) -> checkEntry name path) with
        | Some refusal -> Error refusal
        | None ->

        let names = paths |> Map.keys |> Array.ofSeq
        let upper = names |> Array.map (fun n -> n.ToUpperInvariant ())
        let lower = names |> Array.map (fun n -> n.ToLowerInvariant ())

        let collision =
            seq {
                for k in 0 .. names.Length - 1 do
                    match
                        seq { 0 .. k - 1 }
                        |> Seq.tryFind (fun j ->
                            String.Equals (upper.[j], upper.[k], StringComparison.Ordinal)
                            || String.Equals (lower.[j], lower.[k], StringComparison.Ordinal)
                        )
                    with
                    | Some j -> yield TpaRefusal.NamesEqualIgnoringCase (names.[j], names.[k])
                    | None -> ()
            }
            |> Seq.tryHead

        match collision with
        | Some refusal -> Error refusal
        | None ->
            Ok
                {
                    Paths = paths
                }

    /// The map from simple name to path.
    let toMap (tpa : TrustedPlatformAssemblies) : Map<string, string> = tpa.Paths

    /// The first entry, in ordinal order of name, whose name equals `name` ignoring case, as
    /// `tryCreate` compares names.
    let tryFindIgnoringCase (name : string) (tpa : TrustedPlatformAssemblies) : (string * string) option =
        tpa.Paths |> Map.toSeq |> Seq.tryFind (fun (n, _) -> equalIgnoringCase n name)
