using System;
using System.Reflection;
using System.Reflection.Emit;

// A dynamic module's references to members of instantiations of its own generic type, which CoreCLR
// makes by translating the member's signature from the module into itself, keeping each TypeDef and
// TypeRef token. Before `Box<T>` is created, `ModuleBuilder_GetMemberRef` names a field and a constructor
// of `Box<int>` and `Box<Thing>`, and `Box<T>`'s own method reads its own field under `Box<!0>`'s
// TypeSpec; after, `ModuleBuilder_GetMemberRefOfFieldInfo` finds the same rows for the created type's
// fields, and `ModuleBuilder_GetMemberRefOfMethodInfo` hands back the methods' own MethodDefs. Methods of
// a second type construct, store, call and load through the references. Last, a `FieldBuilder` of another
// module's generic type is named by the FieldDef of this module that has its RID, as CoreCLR's managed
// caller passes this module as the field's. Measured on .NET 10 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

public static class Program
{
    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Dyn");

        // TypeDef 2, whose default constructor is MethodDef 1 and calls `object..ctor`, MemberRef 1.
        TypeBuilder thing = module.DefineType ("Thing", TypeAttributes.Public | TypeAttributes.Class);
        ConstructorBuilder thingConstructor = thing.DefineDefaultConstructor (MethodAttributes.Public);

        // TypeDef 3: `class Box<T> { public T Content; public Thing Partner; public Version Version; public T Get () => Content; }`.
        // `Version` is TypeRef 2, after `object`.
        TypeBuilder box = module.DefineType ("Box", TypeAttributes.Public | TypeAttributes.Class);
        GenericTypeParameterBuilder[] parameters = box.DefineGenericParameters ("T");
        FieldBuilder content = box.DefineField ("Content", parameters[0], FieldAttributes.Public);
        FieldBuilder partner = box.DefineField ("Partner", thing, FieldAttributes.Public);
        FieldBuilder version = box.DefineField ("Version", typeof (Version), FieldAttributes.Public);
        ConstructorBuilder boxConstructor = box.DefineDefaultConstructor (MethodAttributes.Public);
        MethodBuilder get = box.DefineMethod ("Get", MethodAttributes.Public, parameters[0], Type.EmptyTypes);

        // `Get` reads its own type's field, which is named under `Box<!0>`'s TypeSpec 1 as MemberRef 2.
        ILGenerator getIl = get.GetILGenerator ();
        getIl.Emit (OpCodes.Ldarg_0);
        getIl.Emit (OpCodes.Ldfld, content);
        getIl.Emit (OpCodes.Ret);

        // TypeDef 4.
        TypeBuilder user = module.DefineType (
            "User",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed);

        Type boxOfInt = box.MakeGenericType (typeof (int));
        Type boxOfThing = box.MakeGenericType (thing);

        // `Box<int>` is TypeSpec 2, made before the first of its members is named.
        if (module.GetFieldMetadataToken (TypeBuilder.GetField (boxOfInt, content)) != 0x0A000003)
        {
            return 1;
        }

        // `Partner`'s signature keeps `Thing`'s TypeDef, and `Version`'s its TypeRef.
        if (module.GetFieldMetadataToken (TypeBuilder.GetField (boxOfInt, partner)) != 0x0A000004)
        {
            return 2;
        }

        if (module.GetFieldMetadataToken (TypeBuilder.GetField (boxOfInt, version)) != 0x0A000005)
        {
            return 3;
        }

        if (module.GetMethodMetadataToken (TypeBuilder.GetConstructor (boxOfInt, boxConstructor)) != 0x0A000006)
        {
            return 4;
        }

        // A method of a `TypeBuilder` instantiation is named from a signature the managed caller makes.
        if (module.GetMethodMetadataToken (TypeBuilder.GetMethod (boxOfInt, get)) != 0x0A000007)
        {
            return 5;
        }

        // `Box<Thing>` is TypeSpec 3.
        if (module.GetFieldMetadataToken (TypeBuilder.GetField (boxOfThing, content)) != 0x0A000008)
        {
            return 6;
        }

        if (module.GetMethodMetadataToken (TypeBuilder.GetConstructor (boxOfThing, boxConstructor)) != 0x0A000009)
        {
            return 7;
        }

        if (module.GetMethodMetadataToken (TypeBuilder.GetMethod (boxOfThing, get)) != 0x0A00000A)
        {
            return 8;
        }

        if (module.GetFieldMetadataToken (TypeBuilder.GetField (boxOfInt, content)) != 0x0A000003)
        {
            return 9;
        }

        if (module.GetMethodMetadataToken (TypeBuilder.GetConstructor (boxOfInt, boxConstructor)) != 0x0A000006)
        {
            return 10;
        }

        if (module.GetTypeMetadataToken (boxOfInt) != 0x1B000002 || module.GetTypeMetadataToken (boxOfThing) != 0x1B000003)
        {
            return 11;
        }

        // static int Before () { Box<int> b = new Box<int> (); b.Content = 41; return b.Get () + 1; }, all
        // through the rows above.
        MethodBuilder before = user.DefineMethod ("Before", MethodAttributes.Public | MethodAttributes.Static, typeof (int), Type.EmptyTypes);
        ILGenerator beforeIl = before.GetILGenerator ();
        LocalBuilder b = beforeIl.DeclareLocal (boxOfInt);
        beforeIl.Emit (OpCodes.Newobj, TypeBuilder.GetConstructor (boxOfInt, boxConstructor));
        beforeIl.Emit (OpCodes.Stloc, b);
        beforeIl.Emit (OpCodes.Ldloc, b);
        beforeIl.Emit (OpCodes.Ldc_I4, 41);
        beforeIl.Emit (OpCodes.Stfld, TypeBuilder.GetField (boxOfInt, content));
        beforeIl.Emit (OpCodes.Ldloc, b);
        beforeIl.Emit (OpCodes.Call, TypeBuilder.GetMethod (boxOfInt, get));
        beforeIl.Emit (OpCodes.Ldc_I4_1);
        beforeIl.Emit (OpCodes.Add);
        beforeIl.Emit (OpCodes.Ret);

        Type createdThing = thing.CreateType ()!;
        Type createdBox = box.CreateType ()!;
        Type createdBoxOfInt = createdBox.MakeGenericType (typeof (int));
        Type createdBoxOfThing = createdBox.MakeGenericType (createdThing);

        // The created type's fields find the builders' rows, and `Box<Thing>.Version` is a new one.
        if (module.GetFieldMetadataToken (createdBoxOfInt.GetField ("Content")!) != 0x0A000003)
        {
            return 12;
        }

        if (module.GetFieldMetadataToken (createdBoxOfInt.GetField ("Partner")!) != 0x0A000004)
        {
            return 13;
        }

        if (module.GetFieldMetadataToken (createdBoxOfThing.GetField ("Content")!) != 0x0A000008)
        {
            return 14;
        }

        if (module.GetFieldMetadataToken (createdBoxOfThing.GetField ("Version")!) != 0x0A00000B)
        {
            return 15;
        }

        // A method of the module itself is named by its MethodDef, whatever it is an instantiation of.
        if (module.GetMethodMetadataToken (createdBoxOfInt.GetConstructor (Type.EmptyTypes)!) != 0x06000002)
        {
            return 16;
        }

        if (module.GetMethodMetadataToken (createdBoxOfInt.GetMethod ("Get")!) != 0x06000003)
        {
            return 17;
        }

        // static int After () { Box<Thing> c = new Box<Thing> (); c.Content = new Thing (); return c.Get () == null ? -1 : 42; }
        MethodBuilder after = user.DefineMethod ("After", MethodAttributes.Public | MethodAttributes.Static, typeof (int), Type.EmptyTypes);
        ILGenerator afterIl = after.GetILGenerator ();
        LocalBuilder c = afterIl.DeclareLocal (createdBoxOfThing);
        afterIl.Emit (OpCodes.Newobj, createdBoxOfThing.GetConstructor (Type.EmptyTypes)!);
        afterIl.Emit (OpCodes.Stloc, c);
        afterIl.Emit (OpCodes.Ldloc, c);
        afterIl.Emit (OpCodes.Newobj, createdThing.GetConstructor (Type.EmptyTypes)!);
        afterIl.Emit (OpCodes.Stfld, createdBoxOfThing.GetField ("Content")!);
        afterIl.Emit (OpCodes.Ldloc, c);
        afterIl.Emit (OpCodes.Call, createdBoxOfThing.GetMethod ("Get")!);
        Label nonNull = afterIl.DefineLabel ();
        afterIl.Emit (OpCodes.Brtrue, nonNull);
        afterIl.Emit (OpCodes.Ldc_I4_M1);
        afterIl.Emit (OpCodes.Ret);
        afterIl.MarkLabel (nonNull);
        afterIl.Emit (OpCodes.Ldc_I4, 42);
        afterIl.Emit (OpCodes.Ret);

        Type createdUser = user.CreateType ()!;

        if ((int) createdUser.GetMethod ("Before")!.Invoke (null, null)! != 42)
        {
            return 18;
        }

        if ((int) createdUser.GetMethod ("After")!.Invoke (null, null)! != 42)
        {
            return 19;
        }

        // None of the references added a TypeRef: `Random` is the third.
        if (module.GetTypeMetadataToken (typeof (Random)) != 0x01000003)
        {
            return 20;
        }

        // `Lib.Pair<T>`'s `Second` is FieldDef 2 of `DynLib`. Its managed caller passes `Dyn` as the defining
        // module, so the reference is named and typed as `Dyn`'s FieldDef 2, `Box<T>.Partner`, under the
        // TypeSpec of `Pair<!0>`, whose `Pair` is TypeRef 4.
        ModuleBuilder lib = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("DynLib"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule ("DynLib");
        TypeBuilder pair = lib.DefineType ("Lib.Pair", TypeAttributes.Public | TypeAttributes.Class);
        GenericTypeParameterBuilder[] pairParameters = pair.DefineGenericParameters ("T");
        pair.DefineField ("First", typeof (int), FieldAttributes.Public);
        FieldBuilder second = pair.DefineField ("Second", pairParameters[0], FieldAttributes.Public);

        if (second.MetadataToken != 0x04000002 || module.GetFieldMetadataToken (second) != 0x0A00000C)
        {
            return 21;
        }

        if (module.GetTypeMetadataToken (pair) != 0x01000004)
        {
            return 22;
        }

        return 0;
    }
}
