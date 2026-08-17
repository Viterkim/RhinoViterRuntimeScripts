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
      mutable last_build_succeeded: bool
      mutable last_attempted_fingerprint: string
      mutable stopped: bool }

let state_gate = obj ()
let mutable current_state: WatchState option = None
let mutable show_reload_messages = false

let reload_messages_enabled () =
    lock state_gate (fun () -> show_reload_messages)

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
    let assembly_directory =
        typeof<RuntimeContracts.RuntimeCommandDefinition>.Assembly.Location
        |> Path.GetDirectoryName

    let rec find (directory: DirectoryInfo) (remaining: int) =
        if isNull directory || remaining < 0 then
            None
        elif valid_root directory.FullName then
            Some directory.FullName
        else
            find directory.Parent (remaining - 1)

    find (DirectoryInfo assembly_directory) 10

let write_on_ui (message: string) =
    RhinoApp.InvokeOnUiThread(Action(fun () -> RhinoApp.WriteLine message))

let path_is_inside (directory: string) (path: string) =
    let prefix =
        Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)
        + string Path.DirectorySeparatorChar

    Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)

let runtime_source (root: string) (path: string) =
    let full_path = Path.GetFullPath path
    let core = Path.Combine(root, "src", "Core")
    let commands = Path.Combine(root, "src", "Commands", "Rss")
    let command_list = Path.Combine(root, "src", "Commands", "CommandList.fs")
    let project = payload_project root

    (String.Equals(Path.GetExtension(full_path), ".fs", StringComparison.OrdinalIgnoreCase)
     && (path_is_inside core full_path
         || path_is_inside commands full_path
         || String.Equals(full_path, command_list, StringComparison.OrdinalIgnoreCase)))
    || String.Equals(full_path, project, StringComparison.OrdinalIgnoreCase)

let runtime_files (root: string) =
    let core = Path.Combine(root, "src", "Core")
    let commands = Path.Combine(root, "src", "Commands", "Rss")
    let command_list = Path.Combine(root, "src", "Commands", "CommandList.fs")
    let project = payload_project root

    seq {
        for directory in [ core; commands ] do
            if Directory.Exists directory then
                yield! Directory.EnumerateFiles(directory, "*.fs", SearchOption.AllDirectories)

        for path in [ command_list; project ] do
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
    let start_info = ProcessStartInfo()
    start_info.FileName <- "powershell.exe"
    start_info.WorkingDirectory <- root
    start_info.UseShellExecute <- false
    start_info.CreateNoWindow <- true
    start_info.RedirectStandardOutput <- true
    start_info.RedirectStandardError <- true
    start_info.ArgumentList.Add "-NoProfile"
    start_info.ArgumentList.Add "-ExecutionPolicy"
    start_info.ArgumentList.Add "Bypass"
    start_info.ArgumentList.Add "-File"
    start_info.ArgumentList.Add script

    for argument in arguments do
        start_info.ArgumentList.Add argument

    try
        use build_process = new Process()
        build_process.StartInfo <- start_info

        if not (build_process.Start()) then
            { succeeded = false
              output = "Windows did not start the runtime build process." }
        else
            let standard_output = build_process.StandardOutput.ReadToEndAsync()
            let standard_error = build_process.StandardError.ReadToEndAsync()
            build_process.WaitForExit()
            let output = standard_output.GetAwaiter().GetResult()
            let error = standard_error.GetAwaiter().GetResult()

            let combined =
                [| output; error |]
                |> Array.filter (fun (text: string) -> not (String.IsNullOrWhiteSpace text))
                |> String.concat Environment.NewLine
                |> fun (text: string) -> text.Trim()

            { succeeded = build_process.ExitCode = 0
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
        let first_line =
            outcome.output.Split([| "\r\n"; "\n" |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryHead
            |> Option.defaultValue outcome.output
            |> fun (line: string) -> line.Trim()

        { outcome with output = first_line }

let run_add_command_script (root: string) (name: string) =
    run_powershell root (add_command_script root) [ "-Name"; name ]
    |> concise_script_error

let run_remove_command_script (root: string) (name: string) =
    run_powershell root (remove_command_script root) [ "-Name"; name ]
    |> concise_script_error

let rec begin_build (token: Guid) =
    let work =
        lock state_gate (fun () ->
            match current_state with
            | Some state when state.token = token && not state.stopped ->
                if state.building then
                    state.dirty <- true
                    None
                else
                    let fingerprint = source_fingerprint state.root

                    if String.Equals(fingerprint, state.last_attempted_fingerprint, StringComparison.Ordinal) then
                        state.dirty <- false
                        state.ready.Set()
                        None
                    else
                        state.building <- true
                        state.dirty <- false
                        state.last_attempted_fingerprint <- fingerprint
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
        lock state_gate (fun () ->
            match current_state with
            | Some state when state.token = token && not state.stopped ->
                state.building <- false
                state.last_build_succeeded <- outcome.succeeded

                if state.dirty then
                    let fingerprint = source_fingerprint state.root

                    if String.Equals(fingerprint, state.last_attempted_fingerprint, StringComparison.Ordinal) then
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
    lock state_gate (fun () ->
        match current_state with
        | Some state when state.token = token && not state.stopped ->
            state.dirty <- true
            state.ready.Reset()
            state.timer.Change(500, Timeout.Infinite) |> ignore
        | _ -> ())

let wait_for_build () =
    let pending =
        lock state_gate (fun () ->
            match current_state with
            | Some state when not state.stopped ->
                let fingerprint = source_fingerprint state.root

                let source_changed =
                    not (String.Equals(fingerprint, state.last_attempted_fingerprint, StringComparison.Ordinal))

                if state.building then
                    if source_changed then
                        state.dirty <- true

                    Some(state.token, state.ready, false)
                elif source_changed || state.dirty then
                    state.dirty <- true
                    state.ready.Reset()
                    state.timer.Change(Timeout.Infinite, Timeout.Infinite) |> ignore
                    Some(state.token, state.ready, true)
                else
                    None
            | _ -> None)

    match pending with
    | Some(token, ready, should_start) ->
        if should_start then
            begin_build token

        ready.Wait()
    | None -> ()

    lock state_gate (fun () ->
        match current_state with
        | Some state when not state.stopped -> state.last_build_succeeded
        | _ -> true)

let stop () =
    let previous =
        lock state_gate (fun () ->
            let value = current_state
            current_state <- None

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

let start (root: string) (auto_watch: bool) (reload_messages: bool) =
    stop () |> ignore
    let full_root = Path.GetFullPath root

    if not (valid_root full_root) then
        Error $"'{full_root}' is not a RhinoViterRuntimeScripts source root."
    elif not auto_watch then
        lock state_gate (fun () -> show_reload_messages <- reload_messages)
        Ok $"Initialized runtime scripts at {full_root}. Automatic watching is off."
    else
        lock state_gate (fun () -> show_reload_messages <- reload_messages)
        let token = Guid.NewGuid()
        let watched_root = Path.Combine(full_root, "src")

        let timer =
            new Timer(TimerCallback(fun (_state: obj) -> begin_build token), null, Timeout.Infinite, Timeout.Infinite)

        let ready = new ManualResetEventSlim(true)

        let watcher = new FileSystemWatcher(full_root)
        watcher.IncludeSubdirectories <- true

        watcher.NotifyFilter <- NotifyFilters.FileName ||| NotifyFilters.LastWrite ||| NotifyFilters.Size

        let changed_handler =
            FileSystemEventHandler(fun (_sender: obj) (arguments: FileSystemEventArgs) ->
                if runtime_source full_root arguments.FullPath then
                    queue_change token)

        let renamed_handler =
            RenamedEventHandler(fun (_sender: obj) (arguments: RenamedEventArgs) ->
                if
                    runtime_source full_root arguments.FullPath
                    || runtime_source full_root arguments.OldFullPath
                then
                    queue_change token)

        watcher.Changed.AddHandler changed_handler
        watcher.Created.AddHandler changed_handler
        watcher.Deleted.AddHandler changed_handler
        watcher.Renamed.AddHandler renamed_handler

        let state =
            { token = token
              root = full_root
              watcher = watcher
              timer = timer
              ready = ready
              dirty = false
              building = false
              last_build_succeeded = true
              last_attempted_fingerprint = source_fingerprint full_root
              stopped = false }

        lock state_gate (fun () -> current_state <- Some state)
        watcher.EnableRaisingEvents <- true
        Ok $"Watching runtime commands in {watched_root}. The next runtime command builds and activates saved changes."

let ensure_watching (root: string) =
    let full_root = Path.GetFullPath root

    let already_watching =
        lock state_gate (fun () ->
            match current_state with
            | Some state ->
                not state.stopped
                && String.Equals(state.root, full_root, StringComparison.OrdinalIgnoreCase)
            | None -> false)

    if already_watching then
        Ok()
    else
        start full_root true (reload_messages_enabled ()) |> Result.map ignore
