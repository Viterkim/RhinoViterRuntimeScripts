open System
open System.IO
open System.Text.RegularExpressions

let sourceRoot: string = fsi.CommandLineArgs |> Array.last

type LayoutViolation =
    { line_number: int
      namespace_name: string
      module_name: string }

let namespacePattern =
    Regex(@"^namespace\s+(?:rec\s+)?(?<name>[A-Za-z_][\w'.]*)\s*$", RegexOptions.Compiled)

let boundModulePattern =
    Regex(@"^module\s+(?:(?:internal|public)\s+)?(?<name>[A-Za-z_][\w']*)\s*=", RegexOptions.Compiled)

let namespaceValuePattern =
    Regex(@"^(?:type|exception|let|do)\b", RegexOptions.Compiled)

let layoutViolationsInSource (source: string) =
    let lines = source.Replace("\r\n", "\n").Split '\n'

    let namespaceName =
        lines
        |> Seq.tryPick (fun (line: string) ->
            let matched = namespacePattern.Match line

            if matched.Success then
                Some matched.Groups["name"].Value
            else
                None)

    let boundModules =
        lines
        |> Seq.mapi (fun (index: int) (line: string) -> index + 1, line)
        |> Seq.choose (fun (lineNumber: int, line: string) ->
            let matched = boundModulePattern.Match line

            if matched.Success then
                Some(lineNumber, matched.Groups["name"].Value)
            else
                None)
        |> Seq.toList

    let hasNamespaceValues = lines |> Seq.exists namespaceValuePattern.IsMatch

    match namespaceName, boundModules, hasNamespaceValues with
    | Some name, [ lineNumber, moduleName ], false ->
        [ { line_number = lineNumber
            namespace_name = name
            module_name = moduleName } ]
    | _ -> []

let checkerSelfTests =
    [ "namespace Sample\n\nopen System\n\nmodule Worker =\n    let run () = ()", true
      "module Sample.Worker\n\nopen System\n\nlet run () = ()", false
      "namespace Sample\n\ntype State = Ready\n\nmodule State =\n    let ready = State.Ready", false
      "namespace Sample\n\nmodule Helpers =\n    let value = 1\n\ntype Runner() = class end", false
      "namespace Sample\n\nmodule One =\n    let value = 1\n\nmodule Two =\n    let value = 2", false ]

for source, expectsViolation in checkerSelfTests do
    let hasViolation = not (List.isEmpty (layoutViolationsInSource source))

    if hasViolation <> expectsViolation then
        failwith $"Module-layout lint self-test failed for: {source}"

let violations =
    Directory.EnumerateFiles(sourceRoot, "*.fs", SearchOption.AllDirectories)
    |> Seq.collect (fun (path: string) ->
        File.ReadAllText path
        |> layoutViolationsInSource
        |> Seq.map (fun (violation: LayoutViolation) -> path, violation))
    |> Seq.toList

for path, violation in violations do
    Console.Error.WriteLine(
        $"{path}({violation.line_number}): namespace '{violation.namespace_name}' only wraps module '{violation.module_name}'; use 'module {violation.namespace_name}.{violation.module_name}'"
    )

if not (List.isEmpty violations) then
    Environment.Exit 1
