module RhinoViterRuntimeScripts.CommandList

open System
open Rhino
open Rhino.Commands
open RhinoViterRuntimeScripts.RuntimeContracts

let command (name: string) (id: string) (run: RhinoDoc -> Result) =
    { name = name
      id = Guid id
      run = fun (document: RhinoDoc) (_mode: RunMode) -> run document }

let definitions =
    [| command "RssHello" "9FD7537B-B88E-41D4-92DF-E80D89A8231D" Commands.Rss.Hello.run |]
