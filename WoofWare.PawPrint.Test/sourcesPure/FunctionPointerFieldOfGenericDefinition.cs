using System;

// A field of a generic type definition whose type is a function pointer over the
// type's own parameter. Reflection reads a field's type through `Signature`, in
// the context of the field's declaring type: here the definition `FnHolder<>`,
// whose own parameter `T` is in scope for its field's signature. The same
// function pointer type, read as a method's parameter type, is the control.
//
// The checks stay clear of `GetFunctionPointerReturnType`,
// `GetFunctionPointerParameterTypes` and `IsUnmanagedFunctionPointer`, which ask the
// runtime about the function pointer itself (`RuntimeTypeHandle_GetArgumentTypesFromFunctionPointer`,
// `RuntimeTypeHandle::IsUnmanagedFunctionPointer`) rather than about how the field's
// type is read. `ToString` names the return and parameter types instead, and
// reference equality with the control says the field's type is the very type the
// method's signature names.
unsafe class FnHolder<T>
{
    public delegate*<T, void> F;

    public static void M(delegate*<T, void> p)
    {
    }
}

unsafe class FunctionPointerFieldOfGenericDefinition
{
    // 0 if `fnPtr` looks like `delegate*<T, void>` over `FnHolder<>`'s
    // own `T`; otherwise `offset` plus the number of the first check that failed.
    static int Check(Type fnPtr, int offset)
    {
        if (!fnPtr.IsFunctionPointer)
        {
            return offset + 1;
        }

        // The return type and the one parameter, which is the type variable `T`
        // rather than anything it could be instantiated over.
        if (fnPtr.ToString() != "System.Void(T)")
        {
            return offset + 2;
        }

        if (!fnPtr.ContainsGenericParameters)
        {
            return offset + 3;
        }

        return 0;
    }

    static int Main(string[] args)
    {
        Type param = typeof(FnHolder<>).GetMethod("M").GetParameters()[0].ParameterType;
        int control = Check(param, 10);
        if (control != 0)
        {
            return control;
        }

        Type field = typeof(FnHolder<>).GetField("F").FieldType;
        int fieldResult = Check(field, 20);
        if (fieldResult != 0)
        {
            return fieldResult;
        }

        // CoreCLR keeps one `FnPtrTypeDesc` per signature, so the field's type and the
        // method parameter's type are a single `RuntimeType`.
        if (!ReferenceEquals(field, param))
        {
            return 30;
        }

        // A closed instantiation's field is a different type: `delegate*<int, void>`.
        Type closed = typeof(FnHolder<int>).GetField("F").FieldType;
        if (closed.ToString() != "System.Void(System.Int32)" || ReferenceEquals(closed, field))
        {
            return 31;
        }

        return 0;
    }
}
