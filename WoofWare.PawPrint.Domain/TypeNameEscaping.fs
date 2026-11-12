namespace WoofWare.PawPrint

open System.Text

/// <summary>
/// The characters a type name reserves (<c>, [ ] &amp; * + \</c>), which CoreCLR's type-name grammar
/// gives a meaning of their own, and the backslash that escapes one inside a name: CoreCLR's
/// <c>IsTypeNameReservedChar</c> (<c>vm/typeparse.h</c>) and <c>TypeNameBuilder::EscapeName</c>
/// (<c>vm/typestring.cpp</c>).
/// </summary>
[<RequireQualifiedAccess>]
module TypeNameEscaping =
    /// Whether a type name reserves `c`, so that a name holding it must escape it.
    let isReserved (c : char) : bool =
        match c with
        | ','
        | '['
        | ']'
        | '&'
        | '*'
        | '+'
        | '\\' -> true
        | _ -> false

    /// <summary>
    /// <c>name</c> with a backslash before each character it holds that a type name reserves, as
    /// CoreCLR's <c>TypeNameBuilder</c> appends a namespace, a type's own name or a generic
    /// parameter's name. A name holding none is returned as it is.
    /// </summary>
    let escape (name : string) : string =
        if not (String.exists isReserved name) then
            name
        else
            let result = StringBuilder (name.Length * 2)

            for c in name do
                if isReserved c then
                    result.Append '\\' |> ignore<StringBuilder>

                result.Append c |> ignore<StringBuilder>

            result.ToString ()
