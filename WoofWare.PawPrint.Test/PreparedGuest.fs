namespace WoofWare.PawPrint.Test

open Microsoft.Extensions.Logging
open WoofWare.PawPrint

/// For a test that drives a prepared guest from inside its `Main`.
[<RequireQualifiedAccess>]
module PreparedGuest =

    /// `prepared`, stepped until its entry thread is running `Main`.
    ///
    /// A prepared program is about to make its call of `Main`. On .NET 10 that call is `Main`
    /// itself, so this returns `prepared` as it is; on .NET 11 it is CoreLib's
    /// `Environment.CallEntryPoint`, whose instructions up to its `calli` of `Main` this steps
    /// through.
    let enterMain (loggerFactory : ILoggerFactory) (prepared : Program.PreparedProgram) : Program.PreparedProgram =
        let logger = loggerFactory.CreateLogger "PreparedGuest"

        // `CallEntryPoint` makes no call before its `calli` of `Main`, so a few dozen steps are
        // ample; the bound is so that a change to that says so rather than looping.
        let rec go (remaining : int) (prepared : Program.PreparedProgram) : Program.PreparedProgram =
            let entry = prepared.State.ThreadState.[prepared.EntryThread]

            if not (StackFrameCapture.isEntryPointCaller entry.MethodState) then
                prepared
            elif remaining = 0 then
                failwith
                    "PreparedGuest.enterMain: the entry thread was still in Environment.CallEntryPoint after 64 steps, without having called Main"
            else
                match Program.stepPrepared loggerFactory logger prepared with
                | Program.ProgramStepOutcome.InstructionStepped (prepared, _, _, _) -> go (remaining - 1) prepared
                | other ->
                    failwith
                        $"PreparedGuest.enterMain: stepping Environment.CallEntryPoint towards Main ended the step as %O{other}"

        go 64 prepared
