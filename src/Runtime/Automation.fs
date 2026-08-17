module RhinoViterRuntimeScripts.RuntimeAutomation

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Rhino

type BuildOutcome = { succeeded: bool; output: string }

type WatchState =
    { token: Guid
      root: string
      watcher: FileSystemWatcher
      timer: Timer
      ready: ManualResetEventSlim
      mutable dirty: bool
      mutable building: bool
      mutable lastBuildSucceeded: bool
      mutable lastAttemptedFingerprint: string
      mutable stopped: bool }

let stateGate = obj ()
let mutable currentState: WatchState option = None
let mutable showReloadMessages = false

let reload_messages_enabled () =
    lock stateGate (fun () -> showReloadMessages)

let payload_project (root: string) =
    Path.Combine(root, "runtime", "RhinoViterRuntimeScripts.Payload.fsproj")

let runtime_build_script (root: string) =
    Path.Combine(root, "scripts", "win", "build-runtime.ps1")

let add_command_script (root: string) =
    Path.Combine(root, "scripts", "win", "add-command.ps1")

let remove_command_script (root: string) =
    Path.Combine(root, "scripts", "win", "remove-command.ps1")

let valid_root (root: string) =
    Directory.Exists root
    && File.Exists(payload_project root)
    && File.Exists(runtime_build_script root)

let infer_source_root () =
    let assemblyDirectory =
        typeof<RuntimeContracts.RuntimeCommandDefinition>.Assembly.Location
        |> Path.GetDirectoryName

    let rec find (directory: DirectoryInfo) (remaining: int) =
        if isNull directory || remaining < 0 then
            None
        elif valid_root directory.FullName then
            Some directory.FullName
        else
            find directory.Parent (remaining - 1)

    find (DirectoryInfo assemblyDirectory) 10

let write_on_ui (message: string) =
    RhinoApp.InvokeOnUiThread(Action(fun () -> RhinoApp.WriteLine message))

let path_is_inside (directory: string) (path: string) =
    let prefix =
        Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)
        + string Path.DirectorySeparatorChar

    Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)

let runtime_source (root: string) (path: string) =
    let fullPath = Path.GetFullPath path
    let core = Path.Combine(root, "src", "Core")
    let commands = Path.Combine(root, "src", "Commands", "Rss")
    let commandList = Path.Combine(root, "src", "Commands", "CommandList.fs")
    let project = payload_project root

    (String.Equals(Path.GetExtension(fullPath), ".fs", StringComparison.OrdinalIgnoreCase)
     && (path_is_inside core fullPath
         || path_is_inside commands fullPath
         || String.Equals(fullPath, commandList, StringComparison.OrdinalIgnoreCase)))
    || String.Equals(fullPath, project, StringComparison.OrdinalIgnoreCase)

let runtime_files (root: string) =
    let core = Path.Combine(root, "src", "Core")
    let commands = Path.Combine(root, "src", "Commands", "Rss")
    let commandList = Path.Combine(root, "src", "Commands", "CommandList.fs")
    let project = payload_project root

    seq {
        for directory in [ core; commands ] do
            if Directory.Exists directory then
                yield! Directory.EnumerateFiles(directory, "*.fs", SearchOption.AllDirectories)

        for path in [ commandList; project ] do
            if File.Exists path then
                yield path
    }
    |> Seq.distinct
    |> Seq.sort

let source_fingerprint (root: string) =
    runtime_files root
    |> Seq.map (fun (path: string) ->
        try
            let hash = File.ReadAllBytes path |> SHA256.HashData |> Convert.ToHexString
            $"{Path.GetFullPath(path).ToUpperInvariant()}:{hash}"
        with _ ->
            $"{Path.GetFullPath(path).ToUpperInvariant()}:unavailable")
    |> String.concat "|"

let run_powershell (root: string) (script: string) (arguments: string list) =
    let startInfo = ProcessStartInfo()
    startInfo.FileName <- "powershell.exe"
    startInfo.WorkingDirectory <- root
    startInfo.UseShellExecute <- false
    startInfo.CreateNoWindow <- true
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true
    startInfo.ArgumentList.Add "-NoProfile"
    startInfo.ArgumentList.Add "-ExecutionPolicy"
    startInfo.ArgumentList.Add "Bypass"
    startInfo.ArgumentList.Add "-File"
    startInfo.ArgumentList.Add script

    for argument in arguments do
        startInfo.ArgumentList.Add argument

    try
        use buildProcess = new Process()
        buildProcess.StartInfo <- startInfo

        if not (buildProcess.Start()) then
            { succeeded = false
              output = "Windows did not start the runtime build process." }
        else
            let standardOutput = buildProcess.StandardOutput.ReadToEndAsync()
            let standardError = buildProcess.StandardError.ReadToEndAsync()
            buildProcess.WaitForExit()
            let output = standardOutput.GetAwaiter().GetResult()
            let error = standardError.GetAwaiter().GetResult()

            let combined =
                [| output; error |]
                |> Array.filter (fun (text: string) -> not (String.IsNullOrWhiteSpace text))
                |> String.concat Environment.NewLine
                |> fun (text: string) -> text.Trim()

            { succeeded = buildProcess.ExitCode = 0
              output = combined }
    with error ->
        { succeeded = false
          output = $"{error.GetType().Name}: {error.Message}" }

let run_build (root: string) =
    run_powershell root (runtime_build_script root) [ "-RhinoVersion"; string RhinoApp.Version.Major ]

let concise_script_error (outcome: BuildOutcome) =
    if outcome.succeeded || String.IsNullOrWhiteSpace outcome.output then
        outcome
    else
        let firstLine =
            outcome.output.Split([| "\r\n"; "\n" |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryHead
            |> Option.defaultValue outcome.output
            |> fun (line: string) -> line.Trim()

        { outcome with output = firstLine }

let run_add_command_script (root: string) (name: string) =
    run_powershell root (add_command_script root) [ "-Name"; name ]
    |> concise_script_error

let run_remove_command_script (root: string) (name: string) =
    run_powershell root (remove_command_script root) [ "-Name"; name ]
    |> concise_script_error

let rec begin_build (token: Guid) =
    let work =
        lock stateGate (fun () ->
            match currentState with
            | Some state when state.token = token && not state.stopped ->
                if state.building then
                    state.dirty <- true
                    None
                else
                    let fingerprint = source_fingerprint state.root

                    if String.Equals(fingerprint, state.lastAttemptedFingerprint, StringComparison.Ordinal) then
                        state.dirty <- false
                        state.ready.Set()
                        None
                    else
                        state.building <- true
                        state.dirty <- false
                        state.lastAttemptedFingerprint <- fingerprint
                        Some state.root
            | _ -> None)

    match work with
    | None -> ()
    | Some root ->
        Task.Run(
            Action(fun () ->
                let outcome = run_build root
                complete_build token outcome)
        )
        |> ignore

and complete_build (token: Guid) (outcome: BuildOutcome) =
    let active =
        lock stateGate (fun () ->
            match currentState with
            | Some state when state.token = token && not state.stopped ->
                state.building <- false
                state.lastBuildSucceeded <- outcome.succeeded

                if state.dirty then
                    let fingerprint = source_fingerprint state.root

                    if String.Equals(fingerprint, state.lastAttemptedFingerprint, StringComparison.Ordinal) then
                        state.dirty <- false
                        state.ready.Set()
                    else
                        state.timer.Change(350, Timeout.Infinite) |> ignore
                else
                    state.ready.Set()

                true
            | _ -> false)

    if active then
        if not outcome.succeeded then
            let detail =
                if String.IsNullOrWhiteSpace outcome.output then
                    "The runtime build failed without output."
                else
                    outcome.output

            write_on_ui $"Runtime build failed:{Environment.NewLine}{detail}"

let queue_change (token: Guid) =
    lock stateGate (fun () ->
        match currentState with
        | Some state when state.token = token && not state.stopped ->
            state.dirty <- true
            state.ready.Reset()
            state.timer.Change(500, Timeout.Infinite) |> ignore
        | _ -> ())

let wait_for_build () =
    let pending =
        lock stateGate (fun () ->
            match currentState with
            | Some state when not state.stopped ->
                let fingerprint = source_fingerprint state.root

                let sourceChanged =
                    not (String.Equals(fingerprint, state.lastAttemptedFingerprint, StringComparison.Ordinal))

                if state.building then
                    if sourceChanged then
                        state.dirty <- true

                    Some(state.token, state.ready, false)
                elif sourceChanged || state.dirty then
                    state.dirty <- true
                    state.ready.Reset()
                    state.timer.Change(Timeout.Infinite, Timeout.Infinite) |> ignore
                    Some(state.token, state.ready, true)
                else
                    None
            | _ -> None)

    match pending with
    | Some(token, ready, shouldStart) ->
        if shouldStart then
            begin_build token

        ready.Wait()
    | None -> ()

    lock stateGate (fun () ->
        match currentState with
        | Some state when not state.stopped -> state.lastBuildSucceeded
        | _ -> true)

let stop () =
    let previous =
        lock stateGate (fun () ->
            let value = currentState
            currentState <- None

            match value with
            | Some state ->
                state.stopped <- true
                state.ready.Set()
            | None -> ()

            value)

    match previous with
    | Some state ->
        state.watcher.EnableRaisingEvents <- false
        state.watcher.Dispose()
        state.timer.Dispose()
        true
    | None -> false

let start (root: string) (autoWatch: bool) (reloadMessages: bool) =
    stop () |> ignore
    let fullRoot = Path.GetFullPath root

    if not (valid_root fullRoot) then
        Error $"'{fullRoot}' is not a RhinoViterRuntimeScripts source root."
    elif not autoWatch then
        lock stateGate (fun () -> showReloadMessages <- reloadMessages)
        Ok $"Initialized runtime scripts at {fullRoot}. Automatic watching is off."
    else
        lock stateGate (fun () -> showReloadMessages <- reloadMessages)
        let token = Guid.NewGuid()
        let watchedRoot = Path.Combine(fullRoot, "src")

        let timer =
            new Timer(TimerCallback(fun (_state: obj) -> begin_build token), null, Timeout.Infinite, Timeout.Infinite)

        let ready = new ManualResetEventSlim(true)

        let watcher = new FileSystemWatcher(fullRoot)
        watcher.IncludeSubdirectories <- true

        watcher.NotifyFilter <- NotifyFilters.FileName ||| NotifyFilters.LastWrite ||| NotifyFilters.Size

        let changedHandler =
            FileSystemEventHandler(fun (_sender: obj) (arguments: FileSystemEventArgs) ->
                if runtime_source fullRoot arguments.FullPath then
                    queue_change token)

        let renamedHandler =
            RenamedEventHandler(fun (_sender: obj) (arguments: RenamedEventArgs) ->
                if
                    runtime_source fullRoot arguments.FullPath
                    || runtime_source fullRoot arguments.OldFullPath
                then
                    queue_change token)

        watcher.Changed.AddHandler changedHandler
        watcher.Created.AddHandler changedHandler
        watcher.Deleted.AddHandler changedHandler
        watcher.Renamed.AddHandler renamedHandler

        let state =
            { token = token
              root = fullRoot
              watcher = watcher
              timer = timer
              ready = ready
              dirty = false
              building = false
              lastBuildSucceeded = true
              lastAttemptedFingerprint = source_fingerprint fullRoot
              stopped = false }

        lock stateGate (fun () -> currentState <- Some state)
        watcher.EnableRaisingEvents <- true
        Ok $"Watching runtime commands in {watchedRoot}. The next runtime command builds and activates saved changes."

let ensure_watching (root: string) =
    let fullRoot = Path.GetFullPath root

    let alreadyWatching =
        lock stateGate (fun () ->
            match currentState with
            | Some state ->
                not state.stopped
                && String.Equals(state.root, fullRoot, StringComparison.OrdinalIgnoreCase)
            | None -> false)

    if alreadyWatching then
        Ok()
    else
        start fullRoot true (reload_messages_enabled ()) |> Result.map ignore
