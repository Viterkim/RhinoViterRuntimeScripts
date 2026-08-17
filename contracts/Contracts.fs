module RhinoViterRuntimeScripts.RuntimeContracts

open Rhino
open Rhino.Commands

type RuntimeCommandDefinition =
    { name: string
      id: System.Guid
      run: RhinoDoc -> RunMode -> Result }
