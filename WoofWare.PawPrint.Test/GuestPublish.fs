namespace WoofWare.PawPrint.Test

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO

/// A guest project of this repository, published framework-dependent for a `Guest` fixture to run
/// under PawPrint and under the real-runtime oracle.
type PublishedGuest =
    {
        /// The publish directory: the guest, its `deps.json` and `runtimeconfig.json`, and the
        /// assemblies it depends on beyond the shared framework.
        Directory : string
        /// The guest's own assembly, in `Directory`.
        Dll : string
    }

/// Publishing a guest project that sits at the repository's root (`WoofWare.PawPrint.Test.FSharpPureCases`,
/// say) for a fixture to run.
[<RequireQualifiedAccess>]
module GuestPublish =

    /// The repository's root: the nearest directory above the test assembly's that holds
    /// `WoofWare.PawPrint.slnx`.
    ///
    /// Not `__SOURCE_DIRECTORY__`: when `ContinuousIntegrationBuild` is set (`Directory.Build.props`
    /// turns it on whenever `GITHUB_ACTION` is non-empty), the F# compiler remaps source paths to a
    /// deterministic `/_/` prefix that has no relationship to the runtime filesystem.
    let repoRoot : Lazy<string> =
        lazy
            let rec walk (dir : string) : string =
                if String.IsNullOrEmpty dir then
                    failwith $"Could not locate WoofWare.PawPrint.slnx by walking up from %s{AppContext.BaseDirectory}"
                elif File.Exists (Path.Combine (dir, "WoofWare.PawPrint.slnx")) then
                    dir
                else
                    walk (Path.GetDirectoryName dir)

            walk AppContext.BaseDirectory

    /// The files in `publishDir` whose names are also those of files in `frameworkDir`, compared
    /// ignoring case as `AssemblyProbe` binds. PawPrint searches `publishDir` ahead of the
    /// framework, so each such file is one it would bind in place of the framework's own.
    let frameworkFilesIn (publishDir : string) (frameworkDir : string) : string list =
        let frameworkFiles =
            HashSet<string> (
                Directory.EnumerateFiles frameworkDir |> Seq.map Path.GetFileName,
                StringComparer.OrdinalIgnoreCase
            )

        // Guards against the comparison passing vacuously on a listing that found nothing.
        if not (frameworkFiles.Contains "System.Private.CoreLib.dll") then
            failwith $"logic error: the framework directory %s{frameworkDir} holds no System.Private.CoreLib.dll"

        Directory.EnumerateFiles publishDir
        |> Seq.map Path.GetFileName
        |> Seq.filter frameworkFiles.Contains
        |> Seq.sort
        |> List.ofSeq

    /// Publishes the project `<projectName>/<projectFileName>` below the repository's root into
    /// `<projectName>/bin/Release/pawprint-test-publish`, which it empties first, and fails unless
    /// the publish holds `<assemblyName>.dll` and no file named as one in any of `frameworkDirs`.
    ///
    /// Framework-dependent, so that both PawPrint and the real-runtime oracle run the guest on the
    /// framework under test: a self-contained publish would carry a framework of its own, which is
    /// the host SDK's runtime pack whatever framework is under test, and PawPrint would bind it
    /// ahead of the framework's own. No apphost, since the oracle runs the DLL through the muxer of
    /// the framework under test, and an apphost would find a framework by its own lookup. The
    /// restore is left on, so the publish fetches whatever packages are not already restored.
    ///
    /// The paths are names joined below the repository's root, which is what
    /// `RealRuntime.captureAssemblyInPlace` asks of an assembly path: no `..`, and not in or above
    /// the temp directory.
    let publish
        (projectName : string)
        (projectFileName : string)
        (assemblyName : string)
        (frameworkDirs : string list)
        : PublishedGuest
        =
        let projectDir = Path.Combine (repoRoot.Force (), projectName)
        let projectFile = Path.Combine (projectDir, projectFileName)
        // A path we control, so that the fixture is not coupled to the project's TargetFramework.
        let publishDir =
            Path.Combine (projectDir, "bin", "Release", "pawprint-test-publish")

        let dllPath = Path.Combine (publishDir, assemblyName + ".dll")

        if not (File.Exists projectFile) then
            failwith $"Cannot publish %s{projectName}: project file %s{projectFile} does not exist."

        // `dotnet publish` adds to an existing output directory rather than replacing it, so a
        // file left by an earlier publish of another shape would survive this one.
        if Directory.Exists publishDir then
            Directory.Delete (publishDir, true)

        let psi = ProcessStartInfo "dotnet"
        psi.ArgumentList.Add "publish"
        psi.ArgumentList.Add "--configuration"
        psi.ArgumentList.Add "Release"
        psi.ArgumentList.Add "--no-self-contained"
        psi.ArgumentList.Add "-p:UseAppHost=false"
        psi.ArgumentList.Add "--output"
        psi.ArgumentList.Add publishDir
        psi.ArgumentList.Add projectFile
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false

        use proc =
            Process.Start psi
            |> Option.ofObj
            |> Option.defaultWith (fun () -> failwith "Process.Start returned null for dotnet publish")

        // Read both pipes concurrently so a chatty child can't deadlock by filling
        // the stderr buffer while we're blocked draining stdout (or vice versa).
        let stdoutTask = proc.StandardOutput.ReadToEndAsync ()
        let stderrTask = proc.StandardError.ReadToEndAsync ()
        proc.WaitForExit ()
        let stdout = stdoutTask.GetAwaiter().GetResult ()
        let stderr = stderrTask.GetAwaiter().GetResult ()

        if proc.ExitCode <> 0 then
            failwith
                $"dotnet publish failed with exit code %d{proc.ExitCode} for %s{projectFile}.\nSTDOUT:\n%s{stdout}\nSTDERR:\n%s{stderr}"

        if not (File.Exists dllPath) then
            failwith
                $"dotnet publish completed but %s{dllPath} does not exist.\nSTDOUT:\n%s{stdout}\nSTDERR:\n%s{stderr}"

        for frameworkDir in frameworkDirs do
            match frameworkFilesIn publishDir frameworkDir with
            | [] -> ()
            | files ->
                let files = String.concat ", " files

                failwith
                    $"The publish of %s{projectName} in %s{publishDir} holds files named as in the framework directory %s{frameworkDir}, which PawPrint would bind ahead of that framework's own: %s{files}. Publish framework-dependent."

        {
            Directory = publishDir
            Dll = dllPath
        }
