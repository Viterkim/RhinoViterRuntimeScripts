namespace RhinoViterRuntimeScripts

open Rhino
open Rhino.PlugIns

type RhinoViterRuntimeScriptsPlugin() =
    inherit PlugIn()

    override _.LoadTime = PlugInLoadTime.AtStartup

    override _.OnLoad(_error_message: byref<string>) =
        match PayloadLoader.reload () with
        | Ok _ -> ()
        | Error message -> RhinoApp.WriteLine $"Runtime payload was not loaded: {message}"

        RuntimeRegistry.set_before_run (fun () ->
            if RuntimeAutomation.wait_for_build () then
                PayloadLoader.refresh_if_available (RuntimeAutomation.reload_messages_enabled ()))

        match RuntimeAutomation.infer_source_root () with
        | Some source_root ->
            match RuntimeAutomation.start source_root true false with
            | Ok _ -> ()
            | Error message -> RhinoApp.WriteLine $"Runtime source watching did not start: {message}"
        | None -> ()

        LoadReturnCode.Success

    override _.OnShutdown() =
        RuntimeAutomation.stop () |> ignore
        PayloadLoader.shutdown ()
