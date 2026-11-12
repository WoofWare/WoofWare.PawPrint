using System;
using System.Reflection;
using System.Reflection.Emit;

// One dynamic module's references to another's `FieldBuilder`s, `MethodBuilder` and `ConstructorBuilder`,
// which `ModuleBuilder_GetMemberRef` makes: the member's name and signature read from the other module,
// the signature translated into this one, and a MemberRef found or added under the declaring type's
// TypeRef. The created type's `RuntimeFieldInfo` and `RuntimeMethodInfo`, which take
// `ModuleBuilder_GetMemberRefOfFieldInfo` and `ModuleBuilder_GetMemberRefOfMethodInfo`, find the same
// rows. A method of the second module then constructs, calls, stores and loads through those tokens. A
// field and a constructor of an instantiation of the first module's generic type, never created, are
// named under the instantiation's TypeSpec. Measured on .NET 10 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    public static int Main ()
    {
        AssemblyBuilder libAssembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("DynLib"), AssemblyBuilderAccess.Run);
        ModuleBuilder lib = libAssembly.DefineDynamicModule ("DynLib");

        TypeBuilder thing = lib.DefineType ("Lib.Thing", TypeAttributes.Public | TypeAttributes.Class);
        FieldBuilder value = thing.DefineField ("Value", typeof (int), FieldAttributes.Public | FieldAttributes.Static);
        // A field whose type is a TypeDef of `DynLib`, which the translation names by `DynUser`'s TypeRef.
        FieldBuilder instance = thing.DefineField ("Instance", thing, FieldAttributes.Public | FieldAttributes.Static);

        MethodBuilder add = thing.DefineMethod ("Add", MethodAttributes.Public | MethodAttributes.Static, typeof (int), new[] { typeof (int) });
        ILGenerator addIl = add.GetILGenerator ();
        addIl.Emit (OpCodes.Ldarg_0);
        addIl.Emit (OpCodes.Ldc_I4_1);
        addIl.Emit (OpCodes.Add);
        addIl.Emit (OpCodes.Ret);

        ConstructorBuilder constructor = thing.DefineDefaultConstructor (MethodAttributes.Public);
        Type created = thing.CreateType ()!;

        AssemblyBuilder userAssembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("DynUser"), AssemblyBuilderAccess.Run);
        ModuleBuilder user = userAssembly.DefineDynamicModule ("DynUser");

        // `object` is TypeRef 1, `User`'s parent.
        TypeBuilder userType = user.DefineType (
            "User",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);

        // The field's declaring type is asked for first, which adds `Thing`'s TypeRef.
        if (user.GetFieldMetadataToken (value) != 0x0A000001)
        {
            return 1;
        }

        if (user.GetTypeMetadataToken (thing) != 0x01000002)
        {
            return 2;
        }

        if (user.GetFieldMetadataToken (instance) != 0x0A000002)
        {
            return 3;
        }

        if (user.GetMethodMetadataToken (add) != 0x0A000003)
        {
            return 4;
        }

        if (user.GetMethodMetadataToken (constructor) != 0x0A000004)
        {
            return 5;
        }

        if (user.GetFieldMetadataToken (value) != 0x0A000001)
        {
            return 6;
        }

        if (user.GetFieldMetadataToken (created.GetField ("Value")!) != 0x0A000001)
        {
            return 7;
        }

        if (user.GetMethodMetadataToken (created.GetMethod ("Add")!) != 0x0A000003)
        {
            return 8;
        }

        // `Instance`'s signature named `Thing` by the TypeRef already there, adding none.
        if (user.GetTypeMetadataToken (typeof (string)) != 0x01000003)
        {
            return 9;
        }

        // static int Run () { Thing.Instance = new Thing (); Thing.Value = Thing.Add (41); return Thing.Value; }
        MethodBuilder run = userType.DefineMethod ("Run", MethodAttributes.Public | MethodAttributes.Static, typeof (int), Type.EmptyTypes);
        ILGenerator il = run.GetILGenerator ();
        il.Emit (OpCodes.Newobj, constructor);
        il.Emit (OpCodes.Stsfld, instance);
        il.Emit (OpCodes.Ldc_I4, 41);
        il.Emit (OpCodes.Call, add);
        il.Emit (OpCodes.Stsfld, value);
        il.Emit (OpCodes.Ldsfld, value);
        il.Emit (OpCodes.Ret);

        Type createdUser = userType.CreateType ()!;

        if ((int) createdUser.GetMethod ("Run")!.Invoke (null, null)! != 42)
        {
            return 10;
        }

        if ((int) created.GetField ("Value")!.GetValue (null)! != 42)
        {
            return 11;
        }

        if (created.GetField ("Instance")!.GetValue (null)?.GetType () != created)
        {
            return 12;
        }

        // A field and a constructor of an instantiation of a generic type of `DynLib`, which is never created:
        // each a MemberRef under `Box<int>`'s TypeSpec, the field's signature `!0` as `DynLib` wrote it.
        TypeBuilder box = lib.DefineType ("Lib.Box", TypeAttributes.Public | TypeAttributes.Class);
        GenericTypeParameterBuilder[] parameters = box.DefineGenericParameters ("T");
        FieldBuilder content = box.DefineField ("Content", parameters[0], FieldAttributes.Public);
        ConstructorBuilder boxConstructor = box.DefineDefaultConstructor (MethodAttributes.Public);
        Type boxOfInt = box.MakeGenericType (typeof (int));

        if (user.GetFieldMetadataToken (TypeBuilder.GetField (boxOfInt, content)) != 0x0A000005)
        {
            return 13;
        }

        if (user.GetMethodMetadataToken (TypeBuilder.GetConstructor (boxOfInt, boxConstructor)) != 0x0A000006)
        {
            return 14;
        }

        if (user.GetTypeMetadataToken (boxOfInt) != 0x1B000001)
        {
            return 15;
        }

        if (user.GetTypeMetadataToken (box) != 0x01000004)
        {
            return 16;
        }

        if (user.GetFieldMetadataToken (TypeBuilder.GetField (boxOfInt, content)) != 0x0A000005)
        {
            return 17;
        }

        return 0;
    }
}
