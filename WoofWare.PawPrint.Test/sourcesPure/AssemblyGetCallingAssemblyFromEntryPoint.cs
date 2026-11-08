using System;
using System.Reflection;

// `Assembly.GetCallingAssembly()` asks the same QCall as `GetExecutingAssembly` for the caller's
// *caller*, by handing it a `LookForMyCallersCaller` stack-crawl mark instead of a
// `LookForMyCaller` one. Called straight from the entry point, the caller's caller is whatever
// called `Main`, which differs between runtimes:
//
//   * On .NET 10 the VM calls `Main` from native code, so the walk runs out of managed frames.
//     CoreCLR's crawl keeps whichever candidate it last recorded rather than giving up, so the
//     answer degrades to the caller: this guest's own assembly. (That fallback is exactly why
//     `GetCallingAssembly` is documented as not guaranteed to be right.)
//   * On .NET 11 the VM calls `Main` through CoreLib's `Environment.CallEntryPoint`, a managed
//     frame the crawl does not skip, so the answer is CoreLib.
//
// The cross-assembly file pins the case where the walk finds a caller's caller in guest code.
public class Program
{
    public static int Main (string[] args)
    {
        Assembly calling = Assembly.GetCallingAssembly ();

        if (calling == null)
            return 1;

        Assembly expected = Environment.Version.Major == 10 ? typeof (Program).Assembly : typeof (object).Assembly;

        if (!ReferenceEquals (calling, expected))
            return 2;

        return 0;
    }
}
