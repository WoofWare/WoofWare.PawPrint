namespace WoofWare.PawPrint

open System.Reflection.PortableExecutable

[<RequireQualifiedAccess>]
type ImageRefusal =
    | NoArchitecture of PEImageHeaders
    | ContentType of manifestFlags : int
    | MachineSpecific of PEImageHeaders
    | ReferenceAssembly
    | NoPlatform of manifestFlags : int

[<RequireQualifiedAccess>]
module ImageAdmission =
    let refusal (image : string) (peReader : PEReader) : ImageRefusal option = None
