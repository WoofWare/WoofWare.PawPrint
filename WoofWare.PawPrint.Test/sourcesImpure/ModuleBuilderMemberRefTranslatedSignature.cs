using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

// `ModuleBuilder_GetMemberRefOfMethodInfo` for methods whose signatures name a type by token: a class,
// a value type, a nested type and a generic instantiation, each a TypeDef of CoreLib. The emitter rewrites
// each such token to name a TypeRef of the dynamic module (`TranslateSigWithScope`), found or added in
// the scope of CoreLib's AssemblyRef, before it looks for or adds the MemberRef. Asking for a type the
// translation has already named finds its TypeRef. Measured on .NET 10 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Dyn");

        // TypeRef 1 is the parent, `Type`, which the signature names again.
        if (module.GetMethodMetadataToken (typeof (Type).GetMethod (nameof (Type.GetType), new[] { typeof (string) })!) != 0x0A000001)
        {
            return 1;
        }

        if (module.GetMethodMetadataToken (typeof (Guid).GetMethod (nameof (Guid.Parse), new[] { typeof (string) })!) != 0x0A000002)
        {
            return 2;
        }

        // `Environment` (TypeRef 3) is the parent, and the translation adds `SpecialFolder` (TypeRef 4),
        // scoped by it.
        MethodInfo getFolderPath = typeof (Environment).GetMethod (nameof (Environment.GetFolderPath), new[] { typeof (Environment.SpecialFolder) })!;
        if (module.GetMethodMetadataToken (getFolderPath) != 0x0A000003)
        {
            return 3;
        }

        // `String` (TypeRef 5) is the parent, and the translation adds `IEnumerable`1` (TypeRef 6).
        MethodInfo join = typeof (string).GetMethod (nameof (string.Join), new[] { typeof (string), typeof (IEnumerable<string>) })!;
        if (module.GetMethodMetadataToken (join) != 0x0A000004)
        {
            return 4;
        }

        // Asked again, the same reference.
        if (module.GetMethodMetadataToken (typeof (Guid).GetMethod (nameof (Guid.Parse), new[] { typeof (string) })!) != 0x0A000002)
        {
            return 5;
        }

        // The types the translation named are found.
        if (module.GetTypeMetadataToken (typeof (Environment.SpecialFolder)) != 0x01000004)
        {
            return 6;
        }

        if (module.GetTypeMetadataToken (typeof (IEnumerable<>)) != 0x01000006)
        {
            return 7;
        }

        return 0;
    }
}
