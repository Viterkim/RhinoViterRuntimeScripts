module RhinoViterRuntimeScripts.RuntimeRegistry

open System
open System.Collections.Generic
open Rhino
open Rhino.ApplicationSettings
open Rhino.Commands
open RhinoViterRuntimeScripts.RuntimeContracts

let gate = obj ()

let mutable definitions =
    Dictionary<string, RuntimeCommandDefinition>(StringComparer.OrdinalIgnoreCase)

let mutable beforeRun = fun () -> ()
let aliasMacroPrefix = "! _-RuntimeScriptsRun "

let set_before_run (callback: unit -> unit) =
    lock gate (fun () -> beforeRun <- callback)

let prepare_run () =
    let callback = lock gate (fun () -> beforeRun)
    callback ()

let run (name: string) (document: RhinoDoc) (mode: RunMode) =
    let definition =
        lock gate (fun () ->
            match definitions.TryGetValue name with
            | true, value -> Some value
            | false, _ -> None)

    match definition with
    | Some value ->
        try
            value.run document mode
        with error ->
            RhinoApp.WriteLine $"Runtime command {name} failed: {error.Message}"
            Result.Failure
    | None ->
        RhinoApp.WriteLine $"{name} has no loaded runtime implementation."
        Result.Failure

let validate (incoming: RuntimeCommandDefinition array) =
    if isNull incoming then
        Error "The runtime provider returned no command collection."
    else
        let names = HashSet<string>(StringComparer.OrdinalIgnoreCase)
        let ids = HashSet<Guid>()
        let mutable failure = None

        for definition in incoming do
            if failure.IsNone then
                if isNull (box definition) then
                    failure <- Some "The runtime provider returned an empty command definition."
                elif not (Command.IsValidCommandName definition.name) then
                    failure <- Some $"'{definition.name}' is not a valid Rhino command name."
                elif definition.id = Guid.Empty then
                    failure <- Some $"Runtime command {definition.name} has an empty GUID."
                elif not (names.Add definition.name) then
                    failure <- Some $"Runtime command {definition.name} is defined more than once."
                elif not (ids.Add definition.id) then
                    failure <- Some $"Runtime command GUID {definition.id} is defined more than once."

        match failure with
        | Some message -> Error message
        | None -> Ok incoming

let alias_macro (name: string) = $"{aliasMacroPrefix}{name}"

let owned_alias (name: string) =
    CommandAliasList.IsAlias name
    && CommandAliasList.GetMacro(name).StartsWith(aliasMacroPrefix, StringComparison.OrdinalIgnoreCase)

let validate_aliases (incoming: RuntimeCommandDefinition array) =
    incoming
    |> Array.tryPick (fun (definition: RuntimeCommandDefinition) ->
        if CommandAliasList.IsAlias definition.name then
            if owned_alias definition.name then
                None
            else
                Some $"Rhino already has an unrelated alias named {definition.name}."
        else
            let commandId = Command.LookupCommandId(definition.name, true)

            if commandId = Guid.Empty then
                None
            else
                Some $"Rhino already has a command named {definition.name} with GUID {commandId}.")

let sync_aliases (incoming: RuntimeCommandDefinition array) =
    match validate_aliases incoming with
    | Some message -> Error message
    | None ->
        let desired = HashSet<string>(StringComparer.OrdinalIgnoreCase)

        for definition in incoming do
            desired.Add definition.name |> ignore
            let macro = alias_macro definition.name

            if CommandAliasList.IsAlias definition.name then
                if not (String.Equals(CommandAliasList.GetMacro definition.name, macro, StringComparison.Ordinal)) then
                    CommandAliasList.SetMacro(definition.name, macro) |> ignore
            elif not (CommandAliasList.Add(definition.name, macro)) then
                invalidOp $"Rhino refused to add runtime alias {definition.name}."

        for name in CommandAliasList.GetNames() do
            if owned_alias name && not (desired.Contains name) then
                CommandAliasList.Delete name |> ignore

        Ok()

let replace (incoming: RuntimeCommandDefinition array) =
    match validate incoming with
    | Error message -> Error message
    | Ok validDefinitions ->
        try
            match sync_aliases validDefinitions with
            | Error message -> Error message
            | Ok() ->
                lock gate (fun () ->
                    let replacement =
                        Dictionary<string, RuntimeCommandDefinition>(StringComparer.OrdinalIgnoreCase)

                    for definition in validDefinitions do
                        replacement.Add(definition.name, definition)

                    definitions <- replacement)

                Ok validDefinitions.Length
        with error ->
            Error $"{error.GetType().Name}: {error.Message}"

let clear () =
    for name in CommandAliasList.GetNames() do
        if owned_alias name then
            CommandAliasList.Delete name |> ignore

    lock gate (fun () ->
        beforeRun <- fun () -> ()
        definitions <- Dictionary<string, RuntimeCommandDefinition>(StringComparer.OrdinalIgnoreCase))
