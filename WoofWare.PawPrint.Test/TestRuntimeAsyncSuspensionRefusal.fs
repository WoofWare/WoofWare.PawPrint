namespace WoofWare.PawPrint.Test

open System.IO
open FsUnitTyped
open NUnit.Framework
open WoofWare.PawPrint

/// An await in a runtime-async method that finds its task still pending suspends the method, which
/// PawPrint does not yet do (`docs/plans/2026-11-11-runtime-async.md`, stage 2b). On .NET 11 the
/// guest's `StreamReader.ReadToEndAsync` reaches `ReadBufferAsync`, whose await of the guest's
/// never-completing `ReadAsync` asks CoreLib's await helper to suspend; PawPrint stops there, by
/// name. .NET 10 compiles the same methods as state machines, which PawPrint runs, and the call
/// returns a task that has not completed, as on real .NET.
[<TestFixture>]
[<Parallelizable(ParallelScope.All)>]
module TestRuntimeAsyncSuspensionRefusal =

    let private guest : string =
        """
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public class Program
{
    private sealed class PendingStream : Stream
    {
        private readonly TaskCompletionSource<int> _never = new TaskCompletionSource<int> ();

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

        public override ValueTask<int> ReadAsync (Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new ValueTask<int> (_never.Task);
    }

    public static int Main (string[] args)
    {
        Task<string> pending = new StreamReader (new PendingStream ()).ReadToEndAsync ();
        return pending.IsCompleted ? 1 : 0;
    }
}
"""

    [<Test>]
    let ``an await of a pending task stops at the named suspension refusal`` () : unit =
        let image = Roslyn.compile [ guest ]

        match RealRuntime.executeWithRealRuntime [||] image with
        | RealRuntimeResult.NormalExit 0 -> ()
        | other -> failwith $"real .NET did not return a pending task: %O{other}"

        let _messages, loggerFactory =
            LoggerFactory.makeTestWithProperties [ "case", "RuntimeAsyncPendingAwait" ]

        use _loggerFactoryResource = loggerFactory
        use peImage = new MemoryStream (image)

        let run () =
            BoundedRun.run
                loggerFactory
                "RuntimeAsyncPendingAwait"
                None
                peImage
                (HostConfig.Default (FrameworkUnderTest.runtimeDirsFor KernelConfig.Default.UnixPlatform))
            |> ExpectRun.ended

        match FrameworkUnderTest.runtime () with
        | EmulatedRuntime.Net10 ->
            match run () with
            | RunOutcome.NormalExit (state, _, _) -> state.LatchedExitCode |> shouldEqual 0
            | other -> failwith $"expected the guest to exit, but it ended %O{other}"
        | EmulatedRuntime.Net11 ->
            let exn = Assert.Catch (fun () -> run () |> ignore<RunOutcome>)

            exn.Message |> shouldContainText "does not yet suspend a runtime-async method"
            exn.Message |> shouldContainText "AsyncHelpers.TailAwait"
