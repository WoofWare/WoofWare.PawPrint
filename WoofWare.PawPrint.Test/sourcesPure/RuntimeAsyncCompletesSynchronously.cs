using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

// CoreLib's runtime-async methods, each of whose awaits finds a completed task, so every call
// completes before it returns. On .NET 11 each call below enters a method flagged
// `MethodImplAttributes.Async` through its ordinary variant, the task-returning thunk the VM emits,
// which calls the async variant and wraps what it returns:
//
// * `StreamReader.ReadToEndAsync` calls `ReadToEndAsyncInternal` (`Task<string>`), which awaits
//   `ReadBufferAsync` (`ValueTask<int>`), which awaits `MemoryStream.ReadAsync`;
// * a type derived from `StreamReader` reads through `TextReader.ReadToEndAsync` (a virtual
//   `Task<string>` method), which awaits the virtual `ReadAsyncInternal`, overridden by
//   `StreamReader.ReadAsyncInternal` (`ValueTask<int>`);
// * `Stream.CopyToAsync` on a type derived from `MemoryStream` runs `Stream`'s own static local
//   function `Core` (`Task`);
// * `BufferedStream.DisposeAsync` is a virtual `ValueTask` method.
//
// .NET 10 compiles the same methods as state machines, with the same results.
public class Program
{
    private sealed class DerivedReader : StreamReader
    {
        public DerivedReader (Stream stream) : base (stream)
        {
        }
    }

    private sealed class DerivedMemoryStream : MemoryStream
    {
        public DerivedMemoryStream (byte[] buffer) : base (buffer)
        {
        }
    }

    public static int Main (string[] args)
    {
        byte[] bytes = Encoding.UTF8.GetBytes ("hello world");

        Task<string> read = new StreamReader (new MemoryStream (bytes)).ReadToEndAsync ();
        if (!read.IsCompletedSuccessfully) return 1;
        if (read.Result != "hello world") return 2;

        Task<string> derived = new DerivedReader (new MemoryStream (bytes)).ReadToEndAsync ();
        if (!derived.IsCompletedSuccessfully) return 3;
        if (derived.Result != "hello world") return 4;

        var destination = new MemoryStream ();
        Task copy = new DerivedMemoryStream (bytes).CopyToAsync (destination);
        if (!copy.IsCompletedSuccessfully) return 5;
        if (destination.Length != bytes.Length) return 6;
        if (destination.GetBuffer ()[10] != (byte) 'd') return 7;

        var underlying = new MemoryStream ();
        var buffered = new BufferedStream (underlying);
        buffered.WriteByte (42);
        ValueTask dispose = buffered.DisposeAsync ();
        if (!dispose.IsCompletedSuccessfully) return 8;
        // Disposing flushed the buffered byte into the underlying stream, and then disposed it.
        if (underlying.ToArray ().Length != 1) return 9;
        if (underlying.CanRead) return 10;

        return 0;
    }
}
