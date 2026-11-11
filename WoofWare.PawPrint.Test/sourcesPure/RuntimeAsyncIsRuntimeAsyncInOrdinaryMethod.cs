using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

// `RuntimeHelpers.IsRuntimeAsync()` answers whether the method calling it has runtime async's
// calling convention, which the JIT folds at the call (`compIsAsync`). `StreamWriter.FlushAsync`,
// whose ordinary variant runs here, asks it, and keeps the flush's task in `_asyncWriteTask` only
// when the answer is false; the async version of the same IL, which the JIT compiles only for a
// runtime-async caller's await, does not.
//
// .NET 10's `FlushAsync` keeps the task without asking.
public class Program
{
    public static int Main (string[] args)
    {
        var writer = new StreamWriter (new MemoryStream ());
        writer.Write ('x');

        Task flush = writer.FlushAsync ();
        if (!flush.IsCompletedSuccessfully) return 1;

        object kept = typeof (StreamWriter)
            .GetField ("_asyncWriteTask", BindingFlags.NonPublic | BindingFlags.Instance)
            .GetValue (writer);

        if (!ReferenceEquals (kept, flush)) return 2;

        return 0;
    }
}
