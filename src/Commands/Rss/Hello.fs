module RhinoViterRuntimeScripts.Commands.Rss.Hello

open global.RhinoViterRuntimeScripts
open Rhino
open Rhino.Commands

let run (_document: RhinoDoc) =
    RhinoApp.WriteLine "Hello from RssHello. 1"
    Result.Success
