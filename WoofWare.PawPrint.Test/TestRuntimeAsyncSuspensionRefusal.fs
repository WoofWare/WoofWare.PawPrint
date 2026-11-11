namespace WoofWare.PawPrint.Test

open System.IO
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// What PawPrint still refuses of suspending and resuming .NET 11's runtime-async methods
/// (`docs/plans/2026-11-11-runtime-async.md`), each by name: suspending a frame whose await lies in
/// a `try` region (decision 11), and an exception crossing a suspended frame's continuation, whose
/// `DiagnosticIP` CoreLib hands to the `AsyncHelpers_AddContinuationToExInternal` QCall (decision
/// 5). Stage 3 lifts both. .NET 10 compiles the same library methods as state machines, which
/// PawPrint runs, so each guest's ending there is real .NET's.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestRuntimeAsyncSuspensionRefusal =

    /// A stream whose every `ReadAsync` returns a task the guest completes, faults or leaves
    /// pending as `Main` says, after the reader has asked for it.
    let private pendingStream : string =
        """
    private sealed class PendingStream : Stream
    {
        private readonly SemaphoreSlim _requested = new SemaphoreSlim (0);
        private TaskCompletionSource<int> _pending;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException ();

        public override long Position
        {
            get => throw new NotSupportedException ();
            set => throw new NotSupportedException ();
        }

        public override void Flush ()
        {
        }

        public override int Read (byte[] buffer, int offset, int count) => throw new NotSupportedException ();
        public override long Seek (long offset, SeekOrigin origin) => throw new NotSupportedException ();
        public override void SetLength (long value) => throw new NotSupportedException ();
        public override void Write (byte[] buffer, int offset, int count) => throw new NotSupportedException ();

        public override ValueTask<int> ReadAsync (Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var pending = new TaskCompletionSource<int> ();
            Volatile.Write (ref _pending, pending);
            _requested.Release ();
            return new ValueTask<int> (pending.Task);
        }

        public void Fault (Exception e)
        {
            _requested.Wait ();
            Volatile.Read (ref _pending).SetException (e);
        }
    }
"""

    let private guestReadingToEnd : string =
        $"""
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public class Program
{{
%s{pendingStream}

    public static int Main (string[] args)
    {{
        Task<string> pending = new StreamReader (new PendingStream ()).ReadToEndAsync ();
        return pending.IsCompleted ? 1 : 0;
    }}
}}
"""

    let private guestFaultingRead : string =
        $"""
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public class Program
{{
%s{pendingStream}

    public static int Main (string[] args)
    {{
        var stream = new PendingStream ();
        char[] chars = new char[16];
        ValueTask<int> read = new StreamReader (stream).ReadAsync (chars.AsMemory ());
        if (read.IsCompleted) return 1;

        stream.Fault (new IOException ("the read failed"));

        try
        {{
            read.AsTask ().Wait ();
            return 2;
        }}
        catch (AggregateException e) when (e.InnerException is IOException inner && inner.Message == "the read failed")
        {{
            return 0;
        }}
    }}
}}
"""

    let private run (case : string) (source : string) : RunOutcome =
        let image = Roslyn.compile [ source ]

        match RealRuntime.executeWithRealRuntime [||] image with
        | RealRuntimeResult.NormalExit 0 -> ()
        | other -> failwith $"real .NET did not exit 0 from %s{case}: %O{other}"

        let _messages, loggerFactory = LoggerFactory.makeTestWithProperties [ "case", case ]

        use _loggerFactoryResource = loggerFactory
        use peImage = new MemoryStream (image)

        BoundedRun.run
            loggerFactory
            case
            None
            peImage
            (HostConfig.Default (FrameworkUnderTest.runtimeDirsFor KernelConfig.Default.UnixPlatform))
        |> ExpectRun.ended

    let private expectRefusal (case : string) (source : string) (expectedTexts : string list) : unit =
        match FrameworkUnderTest.runtime () with
        | EmulatedRuntime.Net10 ->
            match run case source with
            | RunOutcome.NormalExit (state, _, _) -> state.LatchedExitCode |> shouldEqual 0
            | other -> failwith $"expected the guest to exit, but it ended %O{other}"
        | EmulatedRuntime.Net11 ->
            let exn = Assert.Catch (fun () -> run case source |> ignore<RunOutcome>)

            for expected in expectedTexts do
                exn.Message |> shouldContainText expected

    /// `StreamReader.ReadToEndAsync` reaches `ReadToEndAsyncInternal`, whose await of
    /// `ReadBufferAsync` lies in the `try` region of a `using`. `ReadBufferAsync` suspends at its
    /// await of the guest's never-completing read, so `ReadToEndAsyncInternal`'s await finds a
    /// pending task, and suspending there is refused.
    [<Test>]
    let ``suspending at an await in a try region stops at the named refusal`` () : unit =
        expectRefusal "RuntimeAsyncAwaitInTry" guestReadingToEnd [ "ReadToEndAsyncInternal" ; "lies in a try region" ]

    /// `StreamReader.ReadAsync` reaches `ReadBufferAsync`, which suspends at its await of the
    /// guest's read. The guest faults the read, so resuming the continuation CoreLib made for that
    /// read throws, and CoreLib's dispatcher, unwinding to a continuation that can handle it, hands
    /// the `DiagnosticIP` of `ReadBufferAsync`'s continuation to the QCall that appends the frame to
    /// the exception's trace.
    [<Test>]
    let ``an exception unwinding through a suspended frame stops at the named QCall refusal`` () : unit =
        expectRefusal
            "RuntimeAsyncFaultAfterSuspension"
            guestFaultingRead
            [ "AsyncHelpers_AddContinuationToExInternal" ; "ReadBufferAsync" ]
