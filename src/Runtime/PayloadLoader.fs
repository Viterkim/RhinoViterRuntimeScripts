module RhinoViterRuntimeScripts.PayloadLoader

open System
open System.IO
open System.Reflection
open System.Runtime.Loader
open RhinoViterRuntimeScripts.RuntimeContracts

let payloadFileName = "RhinoViterRuntimeScripts.Payload.dll"
let contractAssemblyName = typeof<RuntimeCommandDefinition>.Assembly.GetName().Name
let slotA = "A"
let slotB = "B"

type PayloadLoadContext(payloadPath: string) =
    inherit AssemblyLoadContext($"RhinoViterRuntimeScripts:{Path.GetFileName(Path.GetDirectoryName payloadPath)}", true)

    let resolver = AssemblyDependencyResolver(payloadPath)

    override this.Load(name: AssemblyName) =
        if
            String.Equals(name.Name, contractAssemblyName, StringComparison.OrdinalIgnoreCase)
            || String.Equals(name.Name, "RhinoCommon", StringComparison.OrdinalIgnoreCase)
            || String.Equals(name.Name, "FSharp.Core", StringComparison.OrdinalIgnoreCase)
        then
            null
        else
            let resolvedPath = resolver.ResolveAssemblyToPath name

            if isNull resolvedPath then
                null
            else
                this.LoadFromAssemblyPath resolvedPath

let mutable currentContext: PayloadLoadContext option = None
let mutable currentSlot: string option = None

let runtime_root () =
    let hostDirectory =
        Path.GetDirectoryName typeof<RuntimeCommandDefinition>.Assembly.Location

    Path.Combine(hostDirectory, "runtime")

let slot_directory (slot: string) = Path.Combine(runtime_root (), slot)

let slot_payload (slot: string) =
    Path.Combine(slot_directory slot, payloadFileName)

let marker_path () =
    Path.Combine(runtime_root (), "active-slot.txt")

let valid_slot (slot: string) = File.Exists(slot_payload slot)

let other_slot (slot: string) =
    if String.Equals(slot, slotA, StringComparison.OrdinalIgnoreCase) then
        slotB
    else
        slotA

let remove_slot (slot: string) =
    let directory = slot_directory slot

    if Directory.Exists directory then
        Directory.Delete(directory, true)

let normalize_start_slot () =
    let aDirectory = slot_directory slotA
    let bDirectory = slot_directory slotB

    if valid_slot slotA then
        Ok slotA
    elif valid_slot slotB then
        try
            if Directory.Exists aDirectory then
                Directory.Delete(aDirectory, true)

            Directory.Move(bDirectory, aDirectory)
            Ok slotA
        with error ->
            Error $"Could not prepare the runtime payload: {error.Message}"
    else
        Error "No runtime payload is installed. Run build-and-install.ps1 again."

let next_slot () =
    match currentSlot with
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

let load_from_stream (context: PayloadLoadContext) (payloadPath: string) =
    let sharing = FileShare.ReadWrite ||| FileShare.Delete

    use assemblyStream = File.Open(payloadPath, FileMode.Open, FileAccess.Read, sharing)

    let symbolsPath = Path.ChangeExtension(payloadPath, ".pdb")

    if File.Exists symbolsPath then
        use symbolsStream = File.Open(symbolsPath, FileMode.Open, FileAccess.Read, sharing)

        context.LoadFromStream(assemblyStream, symbolsStream)
    else
        context.LoadFromStream assemblyStream

let activate (slot: string) =
    let payloadPath = slot_payload slot
    let nextContext = PayloadLoadContext(payloadPath)

    try
        let assembly = load_from_stream nextContext payloadPath

        match definitions_from assembly with
        | Error message ->
            nextContext.Unload()
            Error message
        | Ok definitions ->
            match RuntimeRegistry.replace definitions with
            | Error message ->
                nextContext.Unload()
                Error message
            | Ok _ ->
                let previousContext = currentContext
                let previousSlot = currentSlot

                currentContext <- Some nextContext
                currentSlot <- Some slot
                Directory.CreateDirectory(runtime_root ()) |> ignore
                File.WriteAllText(marker_path (), slot)

                match previousContext with
                | Some context -> context.Unload()
                | None -> ()

                let obsoleteSlot = previousSlot |> Option.defaultValue (other_slot slot)

                if not (String.Equals(obsoleteSlot, slot, StringComparison.OrdinalIgnoreCase)) then
                    try
                        remove_slot obsoleteSlot
                    with error ->
                        Rhino.RhinoApp.WriteLine $"Runtime cleanup failed: {error.Message}"

                Ok "Runtime scripts refreshed."
    with error ->
        nextContext.Unload()
        Error(error_message error)

let reload () =
    match next_slot () with
    | Ok slot -> activate slot
    | Error message when currentSlot.IsSome -> Ok message
    | Error message -> Error message

let refresh_if_available (showMessage: bool) =
    match currentSlot with
    | Some active ->
        let candidate = other_slot active

        if valid_slot candidate then
            match activate candidate with
            | Ok message when showMessage -> Rhino.RhinoApp.WriteLine message
            | Ok _ -> ()
            | Error message -> Rhino.RhinoApp.WriteLine $"Runtime refresh failed: {message}"
    | None -> ()

let shutdown () =
    RuntimeRegistry.clear ()

    match currentContext with
    | Some context -> context.Unload()
    | None -> ()

    currentContext <- None
    currentSlot <- None
