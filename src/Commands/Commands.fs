namespace RhinoViterRuntimeScripts

open System
open System.Runtime.InteropServices
open Rhino
open Rhino.Commands
open Rhino.Input
open Rhino.Input.Custom

module RuntimeScriptName =
    let normalize (value: string) =
        let trimmed = value.Trim()

        let suffix =
            if trimmed.StartsWith("Rss", StringComparison.OrdinalIgnoreCase) then
                trimmed.Substring(3)
            else
                trimmed

        if String.IsNullOrWhiteSpace suffix then
            Error "Enter a script name such as BingoManden. The Rss prefix is automatic."
        else
            let normalizedSuffix =
                string (Char.ToUpperInvariant suffix[0]) + suffix.Substring(1)

            if Text.RegularExpressions.Regex.IsMatch(normalizedSuffix, "^[A-Z][A-Za-z0-9_]{0,59}$") then
                Ok $"Rss{normalizedSuffix}"
            else
                Error "Use at most 60 letters, numbers, or underscores, starting with a letter."

module RuntimeScriptChange =
    let apply (sourceRoot: string) (change: unit -> RuntimeAutomation.BuildOutcome) =
        match RuntimeAutomation.ensure_watching sourceRoot with
        | Error message -> Error $"Could not start runtime source watching: {message}"
        | Ok() ->
            let outcome = change ()

            if not outcome.succeeded then
                Error outcome.output
            elif not (RuntimeAutomation.wait_for_build ()) then
                Error "The source changed, but its payload did not build."
            else
                match PayloadLoader.reload () with
                | Ok _ -> Ok()
                | Error message -> Error $"Runtime reload failed: {message}"

[<Guid("7441AD5D-A5D4-4DA6-BC9D-E6C777C11C93")>]
type RuntimeScriptsInitCommand() =
    inherit Command()

    override _.EnglishName = "RuntimeScriptsInit"

    override _.RunCommand(_document: RhinoDoc, _mode: RunMode) =
        let mutable sourceRoot =
            RuntimeAutomation.infer_source_root ()
            |> Option.defaultValue Environment.CurrentDirectory

        use mutable autoWatch = new OptionToggle(true, "Off", "On")
        use mutable reloadMessages = new OptionToggle(false, "Off", "On")
        let mutable finished = false
        let mutable commandResult = Result.Success

        while not finished do
            use getter = new GetOption()
            getter.SetCommandPrompt $"Initialize runtime scripts from {sourceRoot}"
            getter.AcceptNothing true
            let pathOption = getter.AddOption("Path", sourceRoot)
            getter.AddOptionToggle("AutoWatch", &autoWatch) |> ignore
            getter.AddOptionToggle("ReloadMessages", &reloadMessages) |> ignore

            match getter.Get() with
            | GetResult.Option when getter.OptionIndex() = pathOption ->
                use pathGetter = new GetString()
                pathGetter.SetCommandPrompt "Runtime script project root"
                pathGetter.SetDefaultString sourceRoot

                match pathGetter.Get() with
                | GetResult.String -> sourceRoot <- pathGetter.StringResult()
                | _ ->
                    commandResult <- pathGetter.CommandResult()
                    finished <- commandResult <> Result.Success
            | GetResult.Option -> ()
            | GetResult.Nothing -> finished <- true
            | _ ->
                commandResult <- getter.CommandResult()
                finished <- true

        if commandResult <> Result.Success then
            commandResult
        else
            match RuntimeAutomation.start sourceRoot autoWatch.CurrentValue reloadMessages.CurrentValue with
            | Ok message ->
                RhinoApp.WriteLine message
                Result.Success
            | Error message ->
                RhinoApp.WriteLine $"Runtime initialization failed: {message}"
                Result.Failure

[<Guid("1CA46F69-12D8-41A4-9D6A-70D60EA8BEC3")>]
type RuntimeScriptsRunCommand() =
    inherit Command()

    override _.EnglishName = "RuntimeScriptsRun"

    override _.RunCommand(document: RhinoDoc, mode: RunMode) =
        use getter = new GetString()
        getter.SetCommandPrompt "Runtime script name"

        match getter.Get() with
        | GetResult.String ->
            RuntimeRegistry.prepare_run ()
            RuntimeRegistry.run (getter.StringResult()) document mode
        | _ -> getter.CommandResult()

[<Guid("9DF5866F-7A88-4E8B-AD13-AE51384E7174")>]
type RuntimeScriptsStopCommand() =
    inherit Command()

    override _.EnglishName = "RuntimeScriptsStop"

    override _.RunCommand(_document: RhinoDoc, _mode: RunMode) =
        if RuntimeAutomation.stop () then
            RhinoApp.WriteLine "Runtime source watching stopped. The current payload remains loaded."
        else
            RhinoApp.WriteLine "Runtime source watching was already stopped."

        Result.Success

[<Guid("8C4BD2D2-4702-45B2-927B-650D64F6E098")>]
type RuntimeScriptsReloadCommand() =
    inherit Command()

    override _.EnglishName = "RuntimeScriptsReload"

    override _.RunCommand(_document: RhinoDoc, _mode: RunMode) =
        if not (RuntimeAutomation.wait_for_build ()) then
            RhinoApp.WriteLine "Runtime reload stopped because the payload build failed."
            Result.Failure
        else
            match PayloadLoader.reload () with
            | Ok message ->
                RhinoApp.WriteLine message
                Result.Success
            | Error message ->
                RhinoApp.WriteLine $"Runtime reload failed: {message}"
                Result.Failure

[<Guid("DAC12A6B-C7E2-4196-9189-58402E123A49")>]
type RuntimeScriptsAddCommandCommand() =
    inherit Command()

    override _.EnglishName = "RuntimeScriptsAddCommand"

    override _.RunCommand(_document: RhinoDoc, _mode: RunMode) =
        match RuntimeAutomation.infer_source_root () with
        | None ->
            RhinoApp.WriteLine "The RhinoViterRuntimeScripts source repo could not be found."
            Result.Failure
        | Some sourceRoot ->
            use getter = new GetString()
            getter.SetCommandPrompt "New runtime script name (Rss prefix is automatic)"

            match getter.Get() with
            | GetResult.String ->
                match RuntimeScriptName.normalize (getter.StringResult()) with
                | Error message ->
                    RhinoApp.WriteLine message
                    Result.Failure
                | Ok name ->
                    let change () =
                        RuntimeAutomation.run_add_command_script sourceRoot name

                    match RuntimeScriptChange.apply sourceRoot change with
                    | Error message ->
                        RhinoApp.WriteLine $"Could not add {name}:{Environment.NewLine}{message}"
                        Result.Failure
                    | Ok() ->
                        let scriptName = name.Substring(3)
                        RhinoApp.WriteLine $"Created src/Commands/Rss/{scriptName}.fs and added {name}."
                        Result.Success
            | _ -> getter.CommandResult()

[<Guid("2C19FC39-495C-4781-B874-9BFD359F3EE1")>]
type RuntimeScriptsRemoveCommandCommand() =
    inherit Command()

    override _.EnglishName = "RuntimeScriptsRemoveCommand"

    override _.RunCommand(_document: RhinoDoc, _mode: RunMode) =
        match RuntimeAutomation.infer_source_root () with
        | None ->
            RhinoApp.WriteLine "The RhinoViterRuntimeScripts source repo could not be found."
            Result.Failure
        | Some sourceRoot ->
            use getter = new GetString()
            getter.SetCommandPrompt "Runtime script to remove (Rss prefix is automatic)"

            match getter.Get() with
            | GetResult.String ->
                match RuntimeScriptName.normalize (getter.StringResult()) with
                | Error message ->
                    RhinoApp.WriteLine message
                    Result.Failure
                | Ok name ->
                    let change () =
                        RuntimeAutomation.run_remove_command_script sourceRoot name

                    match RuntimeScriptChange.apply sourceRoot change with
                    | Error message ->
                        RhinoApp.WriteLine $"Could not remove {name}:{Environment.NewLine}{message}"
                        Result.Failure
                    | Ok() ->
                        let scriptName = name.Substring(3)

                        RhinoApp.WriteLine $"Removed src/Commands/Rss/{scriptName}.fs and removed {name}."

                        Result.Success
            | _ -> getter.CommandResult()
