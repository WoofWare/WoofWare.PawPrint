using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// A runtime-async method returning `Task` suspending at each of several awaits in a row, and
// resuming after each. On .NET 11, `TextWriter.WriteLineAsync(StringBuilder)` returns the
// task-returning thunk of its local function `WriteLineAsyncCore`, which awaits the guest's
// `WriteAsync(ReadOnlyMemory<char>)` once for each of the builder's chunks and once more for the
// newline, outside any `try` region. Every write the guest is handed stays pending until the guest
// completes it, so the method suspends at each await.
//
// .NET 10 compiles the same method as a state machine, with the same results.
//
// The exit code is the index of the first check that failed; 0 means all passed.
public class Program
{
    private sealed class PendingWriter : TextWriter
    {
        private readonly SemaphoreSlim _requested = new SemaphoreSlim (0);
        private readonly object _gate = new object ();
        private TaskCompletionSource _pending;

        public readonly List<string> Written = new List<string> ();

        public override Encoding Encoding => Encoding.UTF8;

        public override Task WriteAsync (ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            var pending = new TaskCompletionSource ();
            lock (_gate)
            {
                Written.Add (buffer.ToString ());
                _pending = pending;
            }

            _requested.Release ();
            return pending.Task;
        }

        // Wait until the method has asked to write, then complete that write.
        public void CompleteNextWrite ()
        {
            _requested.Wait ();
            TaskCompletionSource pending;
            lock (_gate)
            {
                pending = _pending;
                _pending = null;
            }

            pending.SetResult ();
        }
    }

    public static int Main (string[] args)
    {
        var writer = new PendingWriter ();
        writer.NewLine = "|";

        var builder = new StringBuilder (4);
        builder.Append ("ab");
        builder.Append ("cdefgh");
        int chunks = 0;
        foreach (ReadOnlyMemory<char> _ in builder.GetChunks ())
        {
            chunks++;
        }

        if (chunks < 2) return 1;

        Task written = writer.WriteLineAsync (builder);
        if (written.IsCompleted) return 2;

        for (int i = 0; i < chunks; i++)
        {
            writer.CompleteNextWrite ();
            if (written.IsCompleted) return 3;
        }

        writer.CompleteNextWrite ();
        written.Wait ();
        if (!written.IsCompletedSuccessfully) return 4;

        if (writer.Written.Count != chunks + 1) return 5;
        if (string.Concat (writer.Written.GetRange (0, chunks)) != "abcdefgh") return 6;
        if (writer.Written[chunks] != "|") return 7;

        return 0;
    }
}
