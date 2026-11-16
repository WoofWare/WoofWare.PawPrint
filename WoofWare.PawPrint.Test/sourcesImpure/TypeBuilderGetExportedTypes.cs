using System;
using System.Reflection;
using System.Reflection.Emit;

// `GetExportedTypes()` on a dynamic assembly, through a created type's `Assembly`: the builder's
// internal `RuntimeAssembly`, which reaches the `AssemblyNative_GetExportedTypes` QCall where
// `AssemblyBuilder.GetExportedTypes()` throws without reaching it.
//
// `DynInternal` is defined and never created. CoreCLR keeps only the public TypeDef rows and so
// never loads it; loading it would throw `TypeLoadException`, as `GetTypes()` on the same
// assembly does.
public class Program
{
    public static int Main(string[] args)
    {
        AssemblyBuilder assemblyBuilder =
            AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("DynExported"), AssemblyBuilderAccess.Run);
        ModuleBuilder moduleBuilder = assemblyBuilder.DefineDynamicModule("DynExported");

        TypeBuilder publicBuilder = moduleBuilder.DefineType("DynPublic", TypeAttributes.Public);
        moduleBuilder.DefineType("DynInternal", TypeAttributes.NotPublic);
        TypeBuilder laterBuilder = moduleBuilder.DefineType("DynLater", TypeAttributes.Public);

        Type created = publicBuilder.CreateType();
        Type createdLater = laterBuilder.CreateType();

        Type[] types = created.Assembly.GetExportedTypes();

        if (types == null)
        {
            return 1;
        }

        if (types.Length != 2)
        {
            return 2;
        }

        if (!ReferenceEquals(types[0], created))
        {
            return 3;
        }

        if (!ReferenceEquals(types[1], createdLater))
        {
            return 4;
        }

        if (types.GetType() != typeof(Type[]))
        {
            return 5;
        }

        return 0;
    }
}
