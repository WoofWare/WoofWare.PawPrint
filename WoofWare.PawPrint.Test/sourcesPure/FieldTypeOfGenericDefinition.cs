using System;
using System.Collections.Generic;

// The types of fields of a generic type definition that mention the definition's
// own parameter. Reflection reads a field's type through `Signature`, in the
// context of the field's declaring type: here the definition `Holder<>`, whose own
// parameter `T` is in scope for its fields' signatures.
//
// The function-pointer rows' control is the same function pointer type read as a
// method's parameter type. Their checks stay clear of `GetFunctionPointerReturnType`,
// `GetFunctionPointerParameterTypes` and `IsUnmanagedFunctionPointer`, which ask the
// runtime about the function pointer itself (`RuntimeTypeHandle_GetArgumentTypesFromFunctionPointer`,
// `RuntimeTypeHandle::IsUnmanagedFunctionPointer`) rather than about how the field's
// type is read. `ToString` names the return and parameter types instead, and
// reference equality with the control says the field's type is the very type the
// method's signature names.
unsafe class Holder<T>
{
    public T Plain;

    public List<T> Items;

    public delegate*<T, void> F;

    public static void M(delegate*<T, void> p)
    {
    }
}

unsafe class FieldTypeOfGenericDefinition
{
    // 0 if `fnPtr` looks like `delegate*<T, void>` over `Holder<>`'s
    // own `T`; otherwise `offset` plus the number of the first check that failed.
    static int CheckFunctionPointer(Type fnPtr, int offset)
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
        Type t = typeof(Holder<>).GetGenericArguments()[0];

        // A field whose type is the definition's parameter itself.
        Type plain = typeof(Holder<>).GetField("Plain").FieldType;
        if (!plain.IsGenericParameter)
        {
            return 1;
        }

        if (plain.GenericParameterPosition != 0)
        {
            return 2;
        }

        if (!ReferenceEquals(plain, t))
        {
            return 3;
        }

        // A field whose type is a constructed type over the definition's parameter.
        Type items = typeof(Holder<>).GetField("Items").FieldType;
        if (!items.ContainsGenericParameters)
        {
            return 4;
        }

        if (items.GetGenericTypeDefinition() != typeof(List<>))
        {
            return 5;
        }

        if (!ReferenceEquals(items.GetGenericArguments()[0], t))
        {
            return 6;
        }

        // The function pointer rows, after their control.
        Type param = typeof(Holder<>).GetMethod("M").GetParameters()[0].ParameterType;
        int control = CheckFunctionPointer(param, 10);
        if (control != 0)
        {
            return control;
        }

        Type field = typeof(Holder<>).GetField("F").FieldType;
        int fieldResult = CheckFunctionPointer(field, 20);
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
        Type closed = typeof(Holder<int>).GetField("F").FieldType;
        if (closed.ToString() != "System.Void(System.Int32)" || ReferenceEquals(closed, field))
        {
            return 31;
        }

        return 0;
    }
}
