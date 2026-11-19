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

    /// A TPA mapping each name of `paths` to its path, or why CoreCLR would not bind it so.
    /// Each name must be its path's file name without `.dll`; each path absolute, free of `:`, not
    /// ending with a space; and no two names equal ignoring case. The entries are checked in
    /// ordinal order of name, and the first refusal is returned.
    let tryCreate (paths : Map<string, string>) : Result<TrustedPlatformAssemblies, TpaRefusal> =
        Ok
            {
                Paths = paths
            }

    /// The map from simple name to path.
    let toMap (tpa : TrustedPlatformAssemblies) : Map<string, string> = tpa.Paths

    /// The first entry, in ordinal order of name, whose name equals `name` ignoring case, as
    /// `tryCreate` compares names.
    let tryFindIgnoringCase (name : string) (tpa : TrustedPlatformAssemblies) : (string * string) option = None
