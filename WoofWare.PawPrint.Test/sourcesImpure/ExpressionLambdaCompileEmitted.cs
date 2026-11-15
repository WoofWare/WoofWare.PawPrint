using System;
using System.Linq.Expressions;
using System.Reflection.Emit;

// `Expression.Lambda<Func<int>>(Expression.Constant(3)).Compile()` with dynamic code supported, so
// that `LambdaCompiler` emits the lambda as an ownerless `DynamicMethod` built with
// `restrictedSkipVisibility: true`, whose `CreateDelegate` calls `RuntimeHelpers.CompileMethod`.
// `ExpressionLambdaCompile.cs` covers the same call with dynamic code unsupported, which interprets
// the lambda instead.
//
// Returns the delegate's result, 3, or 100 if the delegate is not over a `DynamicMethod`, which
// would mean the lambda was interpreted and nothing here was exercised. Measured on real .NET
// 10.0.14 and 11.0.0-rc.3.

public static class Program
{
    public static int Main()
    {
        Func<int> f = Expression.Lambda<Func<int>>(Expression.Constant(3)).Compile();
        if (!(f.Method is DynamicMethod))
        {
            return 100;
        }

        return f();
    }
}
