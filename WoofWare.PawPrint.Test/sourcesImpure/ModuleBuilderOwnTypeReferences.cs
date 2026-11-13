using System;
using System.Reflection;
using System.Reflection.Emit;

// A dynamic module's references to its own created types: `ModuleBuilder_GetTypeRef` finds the type's
// TypeDef by name rather than adding a TypeRef, `ModuleBuilder_GetMemberRefOfMethodInfo` hands back a
// method's MethodDef token, and `ModuleBuilder_GetMemberRefOfFieldInfo` a field's FieldDef token. A
// second type's method then calls, reads and constructs through those tokens. Measured on .NET 10 to
// exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Dyn");

        // `object` is TypeRef 1, `Thing`'s parent; `Thing` is TypeDef 2.
        TypeBuilder thing = module.DefineType ("Ns.Thing", TypeAttributes.Public | TypeAttributes.Class);
        FieldBuilder value = thing.DefineField ("Value", typeof (int), FieldAttributes.Public | FieldAttributes.Static);

        MethodBuilder get = thing.DefineMethod ("Get", MethodAttributes.Public | MethodAttributes.Static, typeof (int), Type.EmptyTypes);
        ILGenerator getIl = get.GetILGenerator ();
        getIl.Emit (OpCodes.Ldc_I4, 42);
        getIl.Emit (OpCodes.Ret);

        ConstructorBuilder constructor = thing.DefineDefaultConstructor (MethodAttributes.Public);

        // A type with no namespace, TypeDef 3.
        TypeBuilder plain = module.DefineType ("Plain", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);

        // Names holding a character that type names reserve, TypeDefs 4 and 5. `GetTypeMetadataToken`
        // passes the type's `FullName`, escaped (`Odd\+One`, `Back\\\+Slash`), and `GetTypeRef` unescapes
        // it to find the TypeDef: `Back\+Slash` is found only if `FullName` escaped its backslash too.
        TypeBuilder odd = module.DefineType ("Odd+One", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);
        TypeBuilder backslash = module.DefineType ("Back\\+Slash", TypeAttributes.Public | TypeAttributes.Interface | TypeAttributes.Abstract);

        Type created = thing.CreateType ()!;
        Type createdPlain = plain.CreateType ()!;
        Type createdOdd = odd.CreateType ()!;
        Type createdBackslash = backslash.CreateType ()!;

        FieldInfo createdValue = created.GetField ("Value")!;
        MethodInfo createdGet = created.GetMethod ("Get")!;
        ConstructorInfo createdConstructor = created.GetConstructor (Type.EmptyTypes)!;

        if (module.GetTypeMetadataToken (created) != thing.MetadataToken || thing.MetadataToken != 0x02000002)
        {
            return 1;
        }

        if (module.GetTypeMetadataToken (createdPlain) != plain.MetadataToken || plain.MetadataToken != 0x02000003)
        {
            return 2;
        }

        if (module.GetTypeMetadataToken (createdOdd) != 0x02000004)
        {
            return 10;
        }

        if (module.GetTypeMetadataToken (createdBackslash) != 0x02000005)
        {
            return 11;
        }

        if (module.GetFieldMetadataToken (createdValue) != value.MetadataToken)
        {
            return 3;
        }

        if (module.GetMethodMetadataToken (createdGet) != get.MetadataToken)
        {
            return 4;
        }

        if (module.GetMethodMetadataToken (createdConstructor) != constructor.MetadataToken)
        {
            return 5;
        }

        // static int Run () { Thing.Value = Thing.Get (); new Thing (); return Thing.Value + 1; }
        TypeBuilder user = module.DefineType (
            "Ns.User",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);
        MethodBuilder run = user.DefineMethod ("Run", MethodAttributes.Public | MethodAttributes.Static, typeof (int), Type.EmptyTypes);
        ILGenerator il = run.GetILGenerator ();
        il.Emit (OpCodes.Call, createdGet);
        il.Emit (OpCodes.Stsfld, createdValue);
        il.Emit (OpCodes.Newobj, createdConstructor);
        il.Emit (OpCodes.Pop);
        il.Emit (OpCodes.Ldsfld, createdValue);
        il.Emit (OpCodes.Ldc_I4_1);
        il.Emit (OpCodes.Add);
        il.Emit (OpCodes.Ret);

        Type createdUser = user.CreateType ()!;

        if ((int) createdUser.GetMethod ("Run")!.Invoke (null, null)! != 43)
        {
            return 6;
        }

        if ((int) createdValue.GetValue (null)! != 42)
        {
            return 7;
        }

        // Nothing above added a TypeRef or a MemberRef for `Thing`: the module still has only `object`'s
        // TypeRef and the default constructor's reference to `object..ctor`.
        if (module.GetTypeMetadataToken (typeof (string)) != 0x01000002)
        {
            return 8;
        }

        if (module.GetMethodMetadataToken (typeof (object).GetMethod (nameof (object.ToString))!) != 0x0A000002)
        {
            return 9;
        }

        return 0;
    }
}
