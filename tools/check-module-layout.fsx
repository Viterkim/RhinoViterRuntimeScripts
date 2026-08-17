#load "source-lexer.fsx"

open System
open System.IO
open System.Text.RegularExpressions
open SourceLexer

let source_root: string = fsi.CommandLineArgs |> Array.last

type LayoutViolation =
    { line_number: int
      namespace_name: string
      module_name: string }

let namespace_pattern =
    Regex(@"^namespace\s+(?:rec\s+)?(?<name>[A-Za-z_][\w'.]*)\s*$", RegexOptions.Compiled)

let bound_module_pattern =
    Regex(@"^module\s+(?:(?:internal|public)\s+)?(?<name>[A-Za-z_][\w']*)\s*=", RegexOptions.Compiled)

let namespace_value_pattern =
    Regex(@"^(?:type|exception|let|do)\b", RegexOptions.Compiled)

let layout_violations_in_source (source: string) =
    let lines = (code_only source).Replace("\r\n", "\n").Split '\n'

    let namespace_name =
        lines
        |> Seq.tryPick (fun (line: string) ->
            let matched = namespace_pattern.Match line

            if matched.Success then
                Some matched.Groups["name"].Value
            else
                None)

    let bound_modules =
        lines
        |> Seq.mapi (fun (index: int) (line: string) -> index + 1, line)
        |> Seq.choose (fun (line_number: int, line: string) ->
            let matched = bound_module_pattern.Match line

            if matched.Success then
                Some(line_number, matched.Groups["name"].Value)
            else
                None)
        |> Seq.toList

    let has_namespace_values = lines |> Seq.exists namespace_value_pattern.IsMatch

    match namespace_name, bound_modules, has_namespace_values with
    | Some name, [ line_number, module_name ], false ->
        [ { line_number = line_number
            namespace_name = name
            module_name = module_name } ]
    | _ -> []

let checker_self_tests =
    [ "namespace Sample\n\nopen System\n\nmodule Worker =\n    let run () = ()", true
      "module Sample.Worker\n\nopen System\n\nlet run () = ()", false
      "namespace Sample\n\ntype State = Ready\n\nmodule State =\n    let ready = State.Ready", false
      "namespace Sample\n\nmodule Helpers =\n    let value = 1\n\ntype Runner() = class end", false
      "namespace Sample\n\nmodule One =\n    let value = 1\n\nmodule Two =\n    let value = 2", false ]

for source, expects_violation in checker_self_tests do
    let has_violation = not (List.isEmpty (layout_violations_in_source source))

    if has_violation <> expects_violation then
        failwith $"Module-layout lint self-test failed for: {source}"

let violations =
    Directory.EnumerateFiles(source_root, "*.fs", SearchOption.AllDirectories)
    |> Seq.collect (fun (path: string) ->
        File.ReadAllText path
        |> layout_violations_in_source
        |> Seq.map (fun (violation: LayoutViolation) -> path, violation))
    |> Seq.toList

for path, violation in violations do
    Console.Error.WriteLine(
        $"{path}({violation.line_number}): namespace '{violation.namespace_name}' only wraps module '{violation.module_name}'; use 'module {violation.namespace_name}.{violation.module_name}'"
    )

if not (List.isEmpty violations) then
    Environment.Exit 1
