# WoofWare.PawPrint.Loader

How the assemblies of a .NET program refer to one another.

This is a core part of the WoofWare.PawPrint interpreter.
It handles:

* locating the images which an assembly reference binds to
* deciding whether CoreCLR would load an image for execution at all
* which definition a type reference, forwarded type, or namespace-qualified name denotes
* which methods the class loader makes callable with the .NET 11 runtime async calling convention

This library also drives the static analysis in WoofWare.PawPrint.Analysis.

See WoofWare.PawPrint.TypeSystem for what the runtime actually does with definitions once they've been found/resolved
(e.g. how it handles concrete instantiation of types).

