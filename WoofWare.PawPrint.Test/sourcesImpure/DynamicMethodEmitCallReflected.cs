using System;
using System.Reflection.Emit;

// `EmitCall` naming a reflected method rather than a `DynamicMethod`. `GetMemberRefToken` wraps
// whatever it is given in a `VarArgMethod`, here with `m_method` set and `m_dynamicMethod` null, and
// `ResolveToken` resolves the call through the wrapped method's handle. Building the wrapper's
// signature asks each parameter for its custom modifiers, so the emit itself reaches
// `Signature.GetParameterOffsetInternal` on a method signature.
//
// Returns 0 on success, 1 if the call returned the wrong value; measured to return 0 on .NET 10.

public class Program
{
    public static int AddTen(int x) => x + 10;

    public static int Main(string[] argv)
    {
        var dm = new DynamicMethod("Caller", typeof(int), new[] { typeof(int) }, typeof(Program).Module);
        var il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.EmitCall(OpCodes.Call, typeof(Program).GetMethod("AddTen"), null);
        il.Emit(OpCodes.Ret);
        var f = (Func<int, int>)dm.CreateDelegate(typeof(Func<int, int>));
        return f(1) == 11 ? 0 : 1;
    }
}
