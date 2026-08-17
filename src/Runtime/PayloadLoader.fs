module RhinoViterRuntimeScripts.PayloadLoader

open System
open System.IO
open System.Reflection
open System.Runtime.Loader
open RhinoViterRuntimeScripts.RuntimeContracts

let payload_file_name = "RhinoViterRuntimeScripts.Payload.dll"

let contract_assembly_name =
    typeof<RuntimeCommandDefinition>.Assembly.GetName().Name

let slot_a = "A"
let slot_b = "B"

type PayloadLoadContext(payload_path: string) =
    inherit
        AssemblyLoadContext($"RhinoViterRuntimeScripts:{Path.GetFileName(Path.GetDirectoryName payload_path)}", true)

    let resolver = AssemblyDependencyResolver(payload_path)

    override this.Load(name: AssemblyName) =
        if
            String.Equals(name.Name, contract_assembly_name, StringComparison.OrdinalIgnoreCase)
            || String.Equals(name.Name, "RhinoCommon", StringComparison.OrdinalIgnoreCase)
            || String.Equals(name.Name, "FSharp.Core", StringComparison.OrdinalIgnoreCase)
        then
            null
        else
            let resolved_path = resolver.ResolveAssemblyToPath name

            if isNull resolved_path then
                null
            else
                this.LoadFromAssemblyPath resolved_path

let mutable current_context: PayloadLoadContext option = None
let mutable current_slot: string option = None

let runtime_root () =
    let host_directory =
        Path.GetDirectoryName typeof<RuntimeCommandDefinition>.Assembly.Location

    Path.Combine(host_directory, "runtime")

let slot_directory (slot: string) = Path.Combine(runtime_root (), slot)

let slot_payload (slot: string) =
    Path.Combine(slot_directory slot, payload_file_name)

let marker_path () =
    Path.Combine(runtime_root (), "active-slot.txt")

let valid_slot (slot: string) = File.Exists(slot_payload slot)

let other_slot (slot: string) =
    if String.Equals(slot, slot_a, StringComparison.OrdinalIgnoreCase) then
        slot_b
    else
        slot_a

let remove_slot (slot: string) =
    let directory = slot_directory slot

    if Directory.Exists directory then
        Directory.Delete(directory, true)

let normalize_start_slot () =
    let a_directory = slot_directory slot_a
    let b_directory = slot_directory slot_b

    if valid_slot slot_a then
        Ok slot_a
    elif valid_slot slot_b then
        try
            if Directory.Exists a_directory then
                Directory.Delete(a_directory, true)

            Directory.Move(b_directory, a_directory)
            Ok slot_a
        with error ->
            Error $"Could not prepare the runtime payload: {error.Message}"
    else
        Error "No runtime payload is installed. Run build-and-install.ps1 again."

let next_slot () =
    match current_slot with
    | None -> normalize_start_slot ()
    | Some active ->
        let candidate = other_slot active

        if valid_slot candidate then
            Ok candidate
        else
            Error "Runtime scripts are current."

let definitions_from (assembly: Assembly) =
    let properties =
        assembly.GetTypes()
        |> Array.collect (fun (candidate: Type) -> candidate.GetProperties(BindingFlags.Public ||| BindingFlags.Static))
        |> Array.filter (fun (property: PropertyInfo) ->
            property.Name = "definitions"
            && property.PropertyType = typeof<RuntimeCommandDefinition array>)

    match properties with
    | [| property |] -> Ok(property.GetValue null :?> RuntimeCommandDefinition array)
    | [||] -> Error $"No runtime command list was found in {assembly.GetName().Name}."
    | _ -> Error $"More than one runtime command list was found in {assembly.GetName().Name}."

let error_message (error: exn) =
    let actual =
        match error with
        | :? TargetInvocationException as invocation when not (isNull invocation.InnerException) ->
            invocation.InnerException
        | _ -> error

    $"{actual.GetType().Name}: {actual.Message}"

let load_from_stream (context: PayloadLoadContext) (payload_path: string) =
    let sharing = FileShare.ReadWrite ||| FileShare.Delete

    use assembly_stream =
        File.Open(payload_path, FileMode.Open, FileAccess.Read, sharing)

    let symbols_path = Path.ChangeExtension(payload_path, ".pdb")

    if File.Exists symbols_path then
        use symbols_stream =
            File.Open(symbols_path, FileMode.Open, FileAccess.Read, sharing)

        context.LoadFromStream(assembly_stream, symbols_stream)
    else
        context.LoadFromStream assembly_stream

let activate (slot: string) =
    let payload_path = slot_payload slot
    let next_context = PayloadLoadContext(payload_path)

    try
        let assembly = load_from_stream next_context payload_path

        match definitions_from assembly with
        | Error message ->
            next_context.Unload()
            Error message
        | Ok definitions ->
            match RuntimeRegistry.replace definitions with
            | Error message ->
                next_context.Unload()
                Error message
            | Ok _ ->
                let previous_context = current_context
                let previous_slot = current_slot

                current_context <- Some next_context
                current_slot <- Some slot
                Directory.CreateDirectory(runtime_root ()) |> ignore
                File.WriteAllText(marker_path (), slot)

                match previous_context with
                | Some context -> context.Unload()
                | None -> ()

                let obsolete_slot = previous_slot |> Option.defaultValue (other_slot slot)

                if not (String.Equals(obsolete_slot, slot, StringComparison.OrdinalIgnoreCase)) then
                    try
                        remove_slot obsolete_slot
                    with error ->
                        Rhino.RhinoApp.WriteLine $"Runtime cleanup failed: {error.Message}"

                Ok "Runtime scripts refreshed."
    with error ->
        next_context.Unload()
        Error(error_message error)

let reload () =
    match next_slot () with
    | Ok slot -> activate slot
    | Error message when current_slot.IsSome -> Ok message
    | Error message -> Error message

let refresh_if_available (show_message: bool) =
    match current_slot with
    | Some active ->
        let candidate = other_slot active

        if valid_slot candidate then
            match activate candidate with
            | Ok message when show_message -> Rhino.RhinoApp.WriteLine message
            | Ok _ -> ()
            | Error message -> Rhino.RhinoApp.WriteLine $"Runtime refresh failed: {message}"
    | None -> ()

let shutdown () =
    RuntimeRegistry.clear ()

    match current_context with
    | Some context -> context.Unload()
    | None -> ()

    current_context <- None
    current_slot <- None
