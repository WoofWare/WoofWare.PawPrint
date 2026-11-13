using System;
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;

// A dynamic module's references to members of its own created types that `ModuleBuilder_GetMemberRefFromSignature`
// adds as MemberRefs whose parent is the type's TypeDef: a call to an instantiation of a created type's generic
// method through the created type's `MethodInfo` (`GetMethodTokenInternal` finds that its module is not the
// `ModuleBuilder` and asks `GetMemberRefToken`, whose parent is the type's TypeDef), and a call, field access and
// construction through user-derived `MethodInfo`, `FieldInfo` and `ConstructorInfo`s standing for created members.
// Each is found again when asked again, and every method emitted through them runs. Last, a user-derived method of a
// type never created, which the emitter names under that type's TypeDef all the same, in a body that never runs. The emitted operands are read
// by the test's terminal-state check. Measured on .NET 10 to exit 0.
//
// Returns 0 on success, or the number of the first check that failed.

/// A user-derived `MethodInfo` standing for `inner`, which `GetMethodTokenNoLock` names by a MemberRef built from
/// its signature under its declaring type's token.
public sealed class DerivedMethod : MethodInfo
{
    readonly MethodInfo inner;

    public DerivedMethod (MethodInfo inner)
    {
        this.inner = inner;
    }

    public override string Name => inner.Name;
    public override Type DeclaringType => inner.DeclaringType;
    public override Type ReflectedType => inner.ReflectedType;
    public override Module Module => inner.Module;
    public override MethodAttributes Attributes => inner.Attributes;
    public override CallingConventions CallingConvention => inner.CallingConvention;
    public override RuntimeMethodHandle MethodHandle => throw new NotSupportedException ();
    public override ParameterInfo[] GetParameters () => inner.GetParameters ();
    public override MethodImplAttributes GetMethodImplementationFlags () => inner.GetMethodImplementationFlags ();
    public override object Invoke (object obj, BindingFlags invokeAttr, Binder binder, object[] parameters, CultureInfo culture) => throw new NotSupportedException ();
    public override Type ReturnType => inner.ReturnType;
    public override ParameterInfo ReturnParameter => inner.ReturnParameter;
    public override MethodInfo GetBaseDefinition () => this;
    public override ICustomAttributeProvider ReturnTypeCustomAttributes => inner.ReturnTypeCustomAttributes;
    public override object[] GetCustomAttributes (bool inherit) => Array.Empty<object> ();
    public override object[] GetCustomAttributes (Type attributeType, bool inherit) => Array.Empty<object> ();
    public override bool IsDefined (Type attributeType, bool inherit) => false;
}

/// A user-derived `FieldInfo` standing for `inner`, which `GetFieldTokenNoLock` names by a MemberRef built from its
/// type under its reflected type's token.
public sealed class DerivedField : FieldInfo
{
    readonly FieldInfo inner;

    public DerivedField (FieldInfo inner)
    {
        this.inner = inner;
    }

    public override string Name => inner.Name;
    public override Type DeclaringType => inner.DeclaringType;
    public override Type ReflectedType => inner.ReflectedType;
    public override Module Module => inner.Module;
    public override Type FieldType => inner.FieldType;
    public override FieldAttributes Attributes => inner.Attributes;
    public override RuntimeFieldHandle FieldHandle => throw new NotSupportedException ();
    public override object GetValue (object obj) => throw new NotSupportedException ();
    public override void SetValue (object obj, object value, BindingFlags invokeAttr, Binder binder, CultureInfo culture) => throw new NotSupportedException ();
    public override Type[] GetRequiredCustomModifiers () => Type.EmptyTypes;
    public override Type[] GetOptionalCustomModifiers () => Type.EmptyTypes;
    public override object[] GetCustomAttributes (bool inherit) => Array.Empty<object> ();
    public override object[] GetCustomAttributes (Type attributeType, bool inherit) => Array.Empty<object> ();
    public override bool IsDefined (Type attributeType, bool inherit) => false;
}

/// A user-derived `ConstructorInfo` standing for `inner`, which `GetMethodMetadataToken` names by a MemberRef built
/// from its parameters under its reflected type's token.
public sealed class DerivedConstructor : ConstructorInfo
{
    readonly ConstructorInfo inner;

    public DerivedConstructor (ConstructorInfo inner)
    {
        this.inner = inner;
    }

    public override string Name => inner.Name;
    public override Type DeclaringType => inner.DeclaringType;
    public override Type ReflectedType => inner.ReflectedType;
    public override Module Module => inner.Module;
    public override MethodAttributes Attributes => inner.Attributes;
    public override CallingConventions CallingConvention => inner.CallingConvention;
    public override RuntimeMethodHandle MethodHandle => throw new NotSupportedException ();
    public override ParameterInfo[] GetParameters () => inner.GetParameters ();
    public override MethodImplAttributes GetMethodImplementationFlags () => inner.GetMethodImplementationFlags ();
    public override object Invoke (object obj, BindingFlags invokeAttr, Binder binder, object[] parameters, CultureInfo culture) => throw new NotSupportedException ();
    public override object Invoke (BindingFlags invokeAttr, Binder binder, object[] parameters, CultureInfo culture) => throw new NotSupportedException ();
    public override object[] GetCustomAttributes (bool inherit) => Array.Empty<object> ();
    public override object[] GetCustomAttributes (Type attributeType, bool inherit) => Array.Empty<object> ();
    public override bool IsDefined (Type attributeType, bool inherit) => false;
}

/// A user-derived `MethodInfo` naming a static method `M` of `type`, taking nothing and returning `int`, which
/// `GetMethodTokenNoLock` names by a MemberRef under `type`'s token whatever `type` holds.
public sealed class UncreatedMethod : MethodInfo
{
    readonly Type type;

    public UncreatedMethod (Type type)
    {
        this.type = type;
    }

    public override string Name => "M";
    public override Type DeclaringType => type;
    public override Type ReflectedType => type;
    public override Module Module => type.Module;
    public override MethodAttributes Attributes => MethodAttributes.Public | MethodAttributes.Static;
    public override CallingConventions CallingConvention => CallingConventions.Standard;
    public override RuntimeMethodHandle MethodHandle => throw new NotSupportedException ();
    public override ParameterInfo[] GetParameters () => Array.Empty<ParameterInfo> ();
    public override MethodImplAttributes GetMethodImplementationFlags () => MethodImplAttributes.IL;
    public override object Invoke (object obj, BindingFlags invokeAttr, Binder binder, object[] parameters, CultureInfo culture) => throw new NotSupportedException ();
    public override Type ReturnType => typeof (int);
    // `GetMethodTokenNoLock` then makes the signature from the return type and parameters alone.
    public override ParameterInfo ReturnParameter => throw new NotImplementedException ();
    public override MethodInfo GetBaseDefinition () => this;
    public override ICustomAttributeProvider ReturnTypeCustomAttributes => throw new NotSupportedException ();
    public override object[] GetCustomAttributes (bool inherit) => Array.Empty<object> ();
    public override object[] GetCustomAttributes (Type attributeType, bool inherit) => Array.Empty<object> ();
    public override bool IsDefined (Type attributeType, bool inherit) => false;
}

public static class Program
{
    static MethodBuilder Static (TypeBuilder type, string name, Type returnType, params Type[] parameters) =>
        type.DefineMethod (name, MethodAttributes.Public | MethodAttributes.Static, returnType, parameters);

    public static int Main ()
    {
        AssemblyBuilder assembly = AssemblyBuilder.DefineDynamicAssembly (new AssemblyName ("Dyn"), AssemblyBuilderAccess.Run);
        ModuleBuilder module = assembly.DefineDynamicModule ("Dyn");

        // TypeDef 2: `static class D` with `static X G<X> (X x) => x` (MethodDef 1), `static int N (int n) => n + 1`
        // (MethodDef 2), `static int H<Y> (D d, Y y) => 7` (MethodDef 3) and `static int F` (FieldDef 1).
        TypeBuilder d = module.DefineType ("D", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        MethodBuilder g = d.DefineMethod ("G", MethodAttributes.Public | MethodAttributes.Static);
        GenericTypeParameterBuilder x = g.DefineGenericParameters ("X")[0];
        g.SetReturnType (x);
        g.SetParameters (x);
        ILGenerator gil = g.GetILGenerator ();
        gil.Emit (OpCodes.Ldarg_0);
        gil.Emit (OpCodes.Ret);
        MethodBuilder n = Static (d, "N", typeof (int), typeof (int));
        ILGenerator nil = n.GetILGenerator ();
        nil.Emit (OpCodes.Ldarg_0);
        nil.Emit (OpCodes.Ldc_I4_1);
        nil.Emit (OpCodes.Add);
        nil.Emit (OpCodes.Ret);
        MethodBuilder h = d.DefineMethod ("H", MethodAttributes.Public | MethodAttributes.Static);
        GenericTypeParameterBuilder y = h.DefineGenericParameters ("Y")[0];
        h.SetReturnType (typeof (int));
        h.SetParameters (d, y);
        ILGenerator hil = h.GetILGenerator ();
        hil.Emit (OpCodes.Ldc_I4, 7);
        hil.Emit (OpCodes.Ret);
        FieldBuilder f = d.DefineField ("F", typeof (int), FieldAttributes.Public | FieldAttributes.Static);
        Type created = d.CreateType ()!;

        // TypeDef 3: `class K` with a default constructor (MethodDef 4), whose call to `object..ctor` is MemberRef 1.
        TypeBuilder k = module.DefineType ("K", TypeAttributes.Public);
        ConstructorBuilder kConstructor = k.DefineDefaultConstructor (MethodAttributes.Public);
        Type createdK = k.CreateType ()!;

        if (d.MetadataToken != 0x02000002 || k.MetadataToken != 0x02000003)
        {
            return 1;
        }

        if (g.MetadataToken != 0x06000001 || n.MetadataToken != 0x06000002 || h.MetadataToken != 0x06000003
            || kConstructor.MetadataToken != 0x06000004 || f.MetadataToken != 0x04000001)
        {
            return 2;
        }

        MethodInfo createdG = created.GetMethod ("G")!;
        MethodInfo createdN = created.GetMethod ("N")!;
        MethodInfo createdH = created.GetMethod ("H")!;
        FieldInfo createdF = created.GetField ("F")!;
        ConstructorInfo createdKConstructor = createdK.GetConstructor (Type.EmptyTypes)!;

        // TypeDef 4: `static class E`, emitting through the created types' members.
        TypeBuilder e = module.DefineType ("E", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);

        // `G<int>` is MemberRef 2 under TypeDef 2, and MethodSpec 1 under it, found again for the second call. `N`
        // is its MethodDef. `H<string>` is MemberRef 3 and MethodSpec 2; `G<long>` MethodSpec 3 under MemberRef 2;
        // and `G<int>` through the `MethodBuilder` MethodSpec 4, under MethodDef 1.
        MethodBuilder calls = Static (e, "Calls", typeof (int));
        ILGenerator callsIl = calls.GetILGenerator ();
        callsIl.Emit (OpCodes.Ldc_I4, 42);
        callsIl.Emit (OpCodes.Call, createdG.MakeGenericMethod (typeof (int)));
        callsIl.Emit (OpCodes.Ldc_I4, 1);
        callsIl.Emit (OpCodes.Call, createdG.MakeGenericMethod (typeof (int)));
        callsIl.Emit (OpCodes.Add);
        callsIl.Emit (OpCodes.Ldc_I4, 5);
        callsIl.Emit (OpCodes.Call, createdN);
        callsIl.Emit (OpCodes.Add);
        callsIl.Emit (OpCodes.Ldnull);
        callsIl.Emit (OpCodes.Ldstr, "s");
        callsIl.Emit (OpCodes.Call, createdH.MakeGenericMethod (typeof (string)));
        callsIl.Emit (OpCodes.Add);
        callsIl.Emit (OpCodes.Ldc_I8, 3L);
        callsIl.Emit (OpCodes.Call, createdG.MakeGenericMethod (typeof (long)));
        callsIl.Emit (OpCodes.Conv_I4);
        callsIl.Emit (OpCodes.Add);
        callsIl.Emit (OpCodes.Ldc_I4, 0);
        callsIl.Emit (OpCodes.Call, g.MakeGenericMethod (typeof (int)));
        callsIl.Emit (OpCodes.Add);
        callsIl.Emit (OpCodes.Ret);

        // `N` through a user-derived `MethodInfo`: MemberRef 4 under TypeDef 2, found again.
        MethodBuilder throughDerivedMethod = Static (e, "ThroughDerivedMethod", typeof (int));
        ILGenerator derivedMethodIl = throughDerivedMethod.GetILGenerator ();
        derivedMethodIl.Emit (OpCodes.Ldc_I4, 10);
        derivedMethodIl.Emit (OpCodes.Call, new DerivedMethod (createdN));
        derivedMethodIl.Emit (OpCodes.Ldc_I4, 20);
        derivedMethodIl.Emit (OpCodes.Call, new DerivedMethod (createdN));
        derivedMethodIl.Emit (OpCodes.Add);
        derivedMethodIl.Emit (OpCodes.Ret);

        // `F` through a user-derived `FieldInfo`: MemberRef 5 under TypeDef 2, found again; `K..ctor` through a
        // user-derived `ConstructorInfo`: MemberRef 6 under TypeDef 3.
        MethodBuilder throughDerivedFieldAndConstructor = Static (e, "ThroughDerivedFieldAndConstructor", typeof (int));
        ILGenerator derivedFieldIl = throughDerivedFieldAndConstructor.GetILGenerator ();
        derivedFieldIl.Emit (OpCodes.Ldc_I4, 11);
        derivedFieldIl.Emit (OpCodes.Stsfld, new DerivedField (createdF));
        derivedFieldIl.Emit (OpCodes.Newobj, new DerivedConstructor (createdKConstructor));
        derivedFieldIl.Emit (OpCodes.Pop);
        derivedFieldIl.Emit (OpCodes.Ldsfld, new DerivedField (createdF));
        derivedFieldIl.Emit (OpCodes.Ret);

        // The public token path hands back MethodDefs and adds nothing, unlike `ILGenerator`'s.
        if (module.GetMethodMetadataToken (createdG) != 0x06000001
            || module.GetMethodMetadataToken (createdG.MakeGenericMethod (typeof (int))) != 0x06000001
            || module.GetMethodMetadataToken (createdN) != 0x06000002)
        {
            return 3;
        }

        // `G<int>` again in another method: MethodSpec 1.
        MethodBuilder againInAnother = Static (e, "AgainInAnother", typeof (int));
        ILGenerator againIl = againInAnother.GetILGenerator ();
        againIl.Emit (OpCodes.Ldc_I4, 1);
        againIl.Emit (OpCodes.Call, createdG.MakeGenericMethod (typeof (int)));
        againIl.Emit (OpCodes.Ret);

        // `ldftn G<int>`: MethodSpec 1.
        MethodBuilder functionPointer = Static (e, "FunctionPointer", typeof (IntPtr));
        ILGenerator functionPointerIl = functionPointer.GetILGenerator ();
        functionPointerIl.Emit (OpCodes.Ldftn, createdG.MakeGenericMethod (typeof (int)));
        functionPointerIl.Emit (OpCodes.Ret);

        // `ldtoken G<string>`: MethodSpec 5, under MemberRef 2. `RuntimeMethodHandle` is TypeRef 2.
        MethodBuilder tokenOfInstantiation = Static (e, "TokenOfInstantiation", typeof (RuntimeMethodHandle));
        ILGenerator tokenIl = tokenOfInstantiation.GetILGenerator ();
        tokenIl.Emit (OpCodes.Ldtoken, createdG.MakeGenericMethod (typeof (string)));
        tokenIl.Emit (OpCodes.Ret);

        Type createdE = e.CreateType ()!;

        if ((int) createdE.GetMethod ("Calls")!.Invoke (null, null)! != 42 + 1 + 6 + 7 + 3 + 0)
        {
            return 4;
        }

        if ((int) createdE.GetMethod ("ThroughDerivedMethod")!.Invoke (null, null)! != 32)
        {
            return 5;
        }

        if ((int) createdE.GetMethod ("ThroughDerivedFieldAndConstructor")!.Invoke (null, null)! != 11)
        {
            return 6;
        }

        if ((int) createdE.GetMethod ("AgainInAnother")!.Invoke (null, null)! != 1)
        {
            return 7;
        }

        if ((IntPtr) createdE.GetMethod ("FunctionPointer")!.Invoke (null, null)! == IntPtr.Zero)
        {
            return 8;
        }

        RuntimeMethodHandle handle = (RuntimeMethodHandle) createdE.GetMethod ("TokenOfInstantiation")!.Invoke (null, null)!;

        if (MethodBase.GetMethodFromHandle (handle) != createdG.MakeGenericMethod (typeof (string)))
        {
            return 9;
        }

        // Each method row, resolved by its number. Resolution cannot tell a TypeDef parent from a TypeRef one, which
        // the terminal-state check reads, as it reads MemberRef 5, `F`.
        Module resolving = created.Module;

        if (resolving.ResolveType (0x01000001) != typeof (object) || resolving.ResolveType (0x01000002) != typeof (RuntimeMethodHandle))
        {
            return 10;
        }

        if (resolving.ResolveMethod (0x0A000001) != typeof (object).GetConstructor (Type.EmptyTypes)
            || resolving.ResolveMethod (0x0A000002) != createdG
            || resolving.ResolveMethod (0x0A000003) != createdH
            || resolving.ResolveMethod (0x0A000004) != createdN
            || resolving.ResolveMethod (0x0A000006) != createdKConstructor)
        {
            return 11;
        }

        if (resolving.ResolveMethod (0x2B000001) != createdG.MakeGenericMethod (typeof (int))
            || resolving.ResolveMethod (0x2B000002) != createdH.MakeGenericMethod (typeof (string))
            || resolving.ResolveMethod (0x2B000003) != createdG.MakeGenericMethod (typeof (long))
            || resolving.ResolveMethod (0x2B000004) != createdG.MakeGenericMethod (typeof (int))
            || resolving.ResolveMethod (0x2B000005) != createdG.MakeGenericMethod (typeof (string)))
        {
            return 12;
        }

        // TypeDef 5: `U`, never created. A user-derived method of it, emitted into a method of `U` that never runs,
        // is MemberRef 7 under `U`'s TypeDef, `00 00 08`: the emitter reads nothing of the type.
        TypeBuilder u = module.DefineType ("U", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
        ILGenerator uncreatedIl = Static (u, "NeverRun", typeof (int)).GetILGenerator ();
        uncreatedIl.Emit (OpCodes.Call, new UncreatedMethod (u));
        uncreatedIl.Emit (OpCodes.Ret);

        if (u.MetadataToken != 0x02000005)
        {
            return 13;
        }

        return 0;
    }
}
