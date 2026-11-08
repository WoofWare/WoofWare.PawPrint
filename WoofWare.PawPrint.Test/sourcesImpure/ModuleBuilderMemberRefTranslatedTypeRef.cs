using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

// `ModuleBuilder_GetMemberRefOfMethodInfo` for methods whose signatures name types of other assemblies
// by TypeRef. The emitter copies each TypeRef's AssemblyRef from the source module before it finds or
// adds the TypeRef, so `Console.SetOut` names `TextWriter` through System.Console's reference to
// System.Runtime, while `GetTypeMetadataToken(typeof(TextWriter))` names it through
// System.Private.CoreLib, its defining assembly: two TypeRefs. Measured on .NET 10 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Dyn");

        // `Console` (TypeRef 1, through System.Console) is the parent, and the translation adds
        // `TextWriter` (TypeRef 2, through a copy of System.Console's reference to System.Runtime).
        MethodInfo setOut = typeof (Console).GetMethod (nameof (Console.SetOut))!;
        if (module.GetMethodMetadataToken (setOut) != 0x0A000001)
        {
            return 1;
        }

        // The defining assembly's TextWriter is another TypeRef.
        if (module.GetTypeMetadataToken (typeof (TextWriter)) != 0x01000003)
        {
            return 2;
        }

        // The same TextWriter as `SetOut`'s, so no TypeRef is added.
        if (module.GetMethodMetadataToken (typeof (Console).GetMethod (nameof (Console.SetError))!) != 0x0A000002)
        {
            return 3;
        }

        // `Enumerable` (TypeRef 4) is the parent; the translation adds `List`1` (TypeRef 5, through a copy
        // of System.Linq's reference to System.Collections), and `IEnumerable`1` (TypeRef 6, through the
        // copy of the reference to System.Runtime it already has).
        if (module.GetMethodMetadataToken (typeof (Enumerable).GetMethod (nameof (Enumerable.ToList))!) != 0x0A000003)
        {
            return 4;
        }

        if (module.GetMethodMetadataToken (setOut) != 0x0A000001)
        {
            return 5;
        }

        return 0;
    }
}
