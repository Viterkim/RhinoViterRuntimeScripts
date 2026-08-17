#load "source-lexer.fsx"

open System
open System.IO
open System.Text.RegularExpressions
open SourceLexer

let source_root: string = fsi.CommandLineArgs |> Array.last

let source_files () =
    Directory.EnumerateFiles(source_root, "*", SearchOption.AllDirectories)
    |> Seq.filter (fun (path: string) ->
        let extension = Path.GetExtension path
        extension = ".fs" || extension = ".fsx")

let has_untyped_parameters (parameters: string) =
    let mutable depth = 0
    let mutable group_start = 0
    let mutable untyped = false

    for index = 0 to parameters.Length - 1 do
        match parameters[index] with
        | '(' ->
            if depth = 0 then
                group_start <- index + 1

            depth <- depth + 1
        | ')' ->
            depth <- depth - 1

            if depth = 0 then
                let group = parameters.Substring(group_start, index - group_start).Trim()

                if group <> "" && not (group.Contains ':') then
                    untyped <- true
        | character when depth = 0 && not (Char.IsWhiteSpace character) -> untyped <- true
        | _ -> ()

    untyped || depth <> 0

let lambda_parameters (source: string) =
    Regex.Matches(source, @"\bfun\b")
    |> Seq.cast<Match>
    |> Seq.choose (fun (matched: Match) ->
        let start = matched.Index + matched.Length
        let mutable index = start
        let mutable depth = 0
        let mutable ending = -1

        while index + 1 < source.Length && ending < 0 do
            match source[index] with
            | '('
            | '['
            | '{' -> depth <- depth + 1
            | ')'
            | ']'
            | '}' -> depth <- depth - 1
            | '-' when depth = 0 && source[index + 1] = '>' -> ending <- index
            | _ -> ()

            index <- index + 1

        if ending < 0 then
            None
        else
            Some(source.Substring(start, ending - start)))

let let_prefix =
    @"^\s*let\s+(?!mutable\b)(?>(?:(?:rec|inline|private|internal|public)\s+)*)(?!struct\b)"

let checks =
    [ "function",
      Regex(
          let_prefix
          + @"[A-Za-z_][\w']*(?:<[^>]+>)?\s+(?<parameters>(?:(?:\([^)]*\)|[A-Za-z_][\w']*)\s*)+)(?:\s*:\s*[^=]+)?\s*=",
          RegexOptions.Compiled
      )
      "member",
      Regex(
          @"^\s*(?:member|override)\s+[^.]+\.[A-Za-z_][\w']*(?:\s+|(?=\())(?!with\b)(?<parameters>(?:(?:\([^)]*\)|[A-Za-z_][\w']*)\s*)+)(?:\s*:\s*[^=]+)?\s*=",
          RegexOptions.Compiled
      )
      "constructor", Regex(@"^\s*type\s+[A-Za-z_][\w']*(?:<[^>]+>)?\s*(?<parameters>\([^)]*\))", RegexOptions.Compiled) ]

let violations_in_line (line: string) =
    let declarations =
        checks
        |> Seq.choose (fun (kind: string, pattern: Regex) ->
            let offending =
                pattern.Matches line
                |> Seq.cast<Match>
                |> Seq.exists (fun (matched: Match) -> has_untyped_parameters matched.Groups["parameters"].Value)

            if offending then Some kind else None)
        |> Seq.toList

    if lambda_parameters line |> Seq.exists has_untyped_parameters then
        declarations @ [ "lambda" ]
    else
        declarations

type FragmentEnd =
    | Equals
    | Arrow

type SourceFragment = { line_number: int; text: string }

let declaration_start =
    Regex(@"^\s*(?:let\b|member\b|override\b|type\s+[A-Za-z_][\w']*)", RegexOptions.Compiled)

let fragment_end (line: string) =
    if declaration_start.IsMatch line && not (line.Contains '=') then
        Some Equals
    elif Regex.IsMatch(line, @"\bfun\b") && (lambda_parameters line |> Seq.isEmpty) then
        Some Arrow
    else
        None

let is_complete (ending: FragmentEnd) (text: string) =
    match ending with
    | Equals -> text.Contains '='
    | Arrow -> lambda_parameters text |> Seq.isEmpty |> not

let source_fragments (source: string) =
    let lines = source.Replace("\r\n", "\n").Split '\n'
    let fragments = ResizeArray<SourceFragment>()
    let mutable index = 0

    while index < lines.Length do
        let first_line_number = index + 1
        let first_line = lines[index]

        match fragment_end first_line with
        | None ->
            fragments.Add
                { line_number = first_line_number
                  text = first_line }

            index <- index + 1
        | Some ending ->
            let text = Text.StringBuilder(first_line.TrimEnd())
            index <- index + 1

            while index < lines.Length && not (is_complete ending (text.ToString())) do
                text.Append(' ').Append(lines[index].Trim()) |> ignore
                index <- index + 1

            fragments.Add
                { line_number = first_line_number
                  text = text.ToString() }

    fragments |> Seq.toList

let violations_in_source (source: string) (code: string) =
    let original_lines = source.Replace("\r\n", "\n").Split '\n'

    source_fragments code
    |> Seq.collect (fun (fragment: SourceFragment) ->
        violations_in_line fragment.text
        |> Seq.map (fun (kind: string) ->
            let index = fragment.line_number - 1

            let original =
                if index < original_lines.Length then
                    original_lines[index]
                else
                    fragment.text

            fragment.line_number, original, kind))
    |> Seq.toList

let private_keyword_pattern = Regex(@"\bprivate\b", RegexOptions.Compiled)

let literal_declaration_pattern =
    Regex(@"\[<Literal>\]\s*let\s+(?:(?:private|internal|public)\s+)*(?<name>[A-Za-z_][\w']*)", RegexOptions.Compiled)

let yelling_snake_case_pattern = Regex(@"^[A-Z][A-Z0-9_]*$", RegexOptions.Compiled)

let lower_camel_identifier_pattern =
    Regex(@"(?<!\w)_*?(?<name>[a-z][A-Za-z0-9']*[A-Z][A-Za-z0-9']*)\b", RegexOptions.Compiled)

let allowed_lower_camel_identifiers =
    set
        [ "defaultArg"
          "defaultValueArg"
          "invalidArg"
          "invalidOp"
          "isNull"
          "isNullV"
          "nonNull"
          "nullArg"
          "nullV"
          "withNull"
          "withNullV" ]

let allowed_lower_camel_qualifiers =
    set
        [ "Array"
          "Array2D"
          "Array3D"
          "Array4D"
          "Async"
          "LanguagePrimitives"
          "List"
          "Map"
          "NativePtr"
          "Operators"
          "Option"
          "Result"
          "Seq"
          "Set"
          "Unchecked"
          "ValueOption" ]

let qualifier_before (source: string) (identifier_index: int) =
    if identifier_index = 0 || source[identifier_index - 1] <> '.' then
        None
    else
        let mutable start = identifier_index - 2

        while start >= 0
              && (Char.IsLetterOrDigit source[start]
                  || source[start] = '_'
                  || source[start] = '\'') do
            start <- start - 1

        let qualifier = source.Substring(start + 1, identifier_index - start - 2)
        if qualifier = "" then None else Some qualifier

let lower_camel_identifier_is_allowed (source: string) (matched: Match) =
    let name = matched.Groups["name"].Value

    allowed_lower_camel_identifiers.Contains name
    || match qualifier_before source matched.Groups["name"].Index with
       | Some qualifier -> allowed_lower_camel_qualifiers.Contains qualifier
       | None -> false

let checker_self_tests =
    [ "let private sample_rate = 120L", false
      "let internal sample_value: int = 1", false
      "let rec private loop (value: int) = loop value", false
      "let inline private convert (value: int) = value", false
      "let mutable state = 0", false
      "let struct (left, right) = pair", false
      "let struct (left, right) =\n    pair", false
      "let private run () = ()", false
      "let private run value = value", true
      "let run left right = left + right", true
      "let run (left: int) right = left + right", true
      "let run (left: int) (right: int) = left + right", false
      "let value: int = 1", false
      "let rec private loop (value) = loop value", true
      "let inline public convert value = value", true
      "let run\n    (value: int)\n    = value", false
      "let run\n    value\n    = value", true
      "override _.Run\n    (value: int)\n    = value", false
      "override _.Run\n    (value)\n    = value", true
      "member _.Run value = value", true
      "member _.Run left right = left + right", true
      "member _.Run (left: int) (right: int) = left + right", false
      "member _.Run(value) = value", true
      "override _.RunCommand(document, mode) = run document mode", true
      "override _.RunCommand(document: RhinoDoc, mode: RunMode) = run document mode", false
      "member _.Run = value", false
      "override _.EnglishName = value", false
      "member _.Name with get () = value", false
      "let run =\n    fun\n        (value: int)\n        -> value", false
      "let run = fun (callback: int -> int) -> callback 1", false
      "let run = fun\n    (callback: int -> int)\n    -> callback 1", false
      "let pair = (fun (value: int) -> value), (fun value -> value)", true
      "let run =\n    fun\n        value\n        -> value", true ]

for source, expects_violation in checker_self_tests do
    let has_violation =
        not (List.isEmpty (violations_in_source source (code_only source)))

    if has_violation <> expects_violation then
        failwith $"Explicit-input lint self-test failed for: {source}"

let private_keyword_self_tests =
    [ "let private value = 1", true
      "let mutable private value = 1", true
      "let rec private loop (value: int) = loop value", true
      "type private State = Ready", true
      "type State = private | Ready", true
      "let privateValue = 1", false
      "let value = 1", false
      "// let private value = 1", false
      "let text = \"private\"", false
      "(* private *) let value = 1", false ]

for source, expects_violation in private_keyword_self_tests do
    if private_keyword_pattern.IsMatch(code_only source) <> expects_violation then
        failwith $"No-private lint self-test failed for: {source}"

let literal_name_self_tests =
    [ "CURRENT_VERSION", true
      "WM_RBUTTONDOWN", true
      "BUTTON_4_UP", true
      "current_version", false
      "CurrentVersion", false
      "4_BUTTON", false ]

for name, expected in literal_name_self_tests do
    if yelling_snake_case_pattern.IsMatch(name) <> expected then
        failwith $"Literal-name lint self-test failed for: {name}"

let lower_camel_identifier_self_tests =
    [ "let cameraLocation = viewport.CameraLocation", [ "cameraLocation" ]
      "let camera_location = viewport.CameraLocation", []
      "let struct (currentX, currentY) = unpack pair", [ "currentX"; "currentY" ]
      "let run (minimumCapacity: uint32) = minimumCapacity", [ "minimumCapacity"; "minimumCapacity" ]
      "let _cameraLocation = viewport.CameraLocation", [ "cameraLocation" ]
      "let value = if isNull view then invalidOp text else value", []
      "let value = defaultValueArg candidate fallback", []
      "let value = nonNull (withNull view)", []
      "let value = Option.defaultValue fallback value", []
      "let value = Project.headerSize", [ "headerSize" ]
      "let total = Array.map2 (*) left right\nlet cameraLocation = viewport.CameraLocation", [ "cameraLocation" ]
      "// let cameraLocation = value", []
      "let text = \"cameraLocation\"", []
      "let text = $\"Camera {cameraLocation:g4} at {headingAngle,10}.\"", [ "cameraLocation"; "headingAngle" ]
      "let text = $\"{stamp:yyyyMMdd}\"", []
      "let text = $\"{values[keyName].CameraLocation}\"", [ "keyName" ]
      "let text = $\"{{cameraLocation}}\"", []
      "let text = $\"\"\"{join \"cameraLocation\" items}\"\"\"", []
      "let text = $\"{items |> List.map (fun (item: Item) -> item.tagName)}\"", [ "tagName" ]
      "let text = $\"\"\"{cameraLocation}\"\"\"", [ "cameraLocation" ]
      "let text = $@\"{cameraLocation}\"", [ "cameraLocation" ]
      "let text = $$\"\"\"{cameraLocation}\"\"\"", []
      "let text = $$\"\"\"{{cameraLocation}}\"\"\"", [ "cameraLocation" ]
      "let text = $\"\"\"{(* ignore cameraLocation *) otherValue}\"\"\"", [ "otherValue" ]
      "let text = $\"\"\"{ // cameraLocation\n otherValue}\"\"\"", [ "otherValue" ]
      "let text = $\"\"\"{$\"{cameraLocation}\"}\"\"\"", [ "cameraLocation" ]
      "let text = \"{cameraLocation}\"", [] ]

let lower_camel_identifiers (source: string) =
    let code = code_only source

    lower_camel_identifier_pattern.Matches code
    |> Seq.cast<Match>
    |> Seq.filter (lower_camel_identifier_is_allowed code >> not)
    |> Seq.map (fun (matched: Match) -> matched.Groups["name"].Value)
    |> Seq.toList

for source, expected in lower_camel_identifier_self_tests do
    let actual = lower_camel_identifiers source

    if actual <> expected then
        failwith $"Snake-case value lint self-test failed for: {source}; expected {expected}; got {actual}"

let violations =
    source_files ()
    |> Seq.collect (fun (path: string) ->
        let source = File.ReadAllText path

        violations_in_source source (code_only source)
        |> Seq.map (fun (line_number: int, line: string, kind: string) ->
            $"{path}({line_number}): {kind} input is missing an explicit type: {line.Trim()}"))
    |> Seq.toList

let private_keyword_violations =
    source_files ()
    |> Seq.collect (fun (path: string) ->
        let source = File.ReadAllText path
        let code = code_only source
        let source_lines = source.Replace("\r\n", "\n").Split '\n'

        private_keyword_pattern.Matches code
        |> Seq.cast<Match>
        |> Seq.map (fun (matched: Match) ->
            let line_number = code.AsSpan(0, matched.Index).Count '\n' + 1

            $"{path}({line_number}): the private keyword is not used in project source: {source_lines[line_number - 1].Trim()}"))
    |> Seq.toList

let literal_name_violations =
    source_files ()
    |> Seq.collect (fun (path: string) ->
        let source = File.ReadAllText path
        let code = code_only source

        literal_declaration_pattern.Matches code
        |> Seq.cast<Match>
        |> Seq.choose (fun (matched: Match) ->
            let name = matched.Groups["name"].Value

            if yelling_snake_case_pattern.IsMatch name then
                None
            else
                let line_number =
                    source.Substring(0, matched.Index)
                    |> Seq.filter (fun (character: char) -> character = '\n')
                    |> Seq.length
                    |> (+) 1

                Some $"{path}({line_number}): literal name must use YELLING_SNAKE_CASE: {name}"))
    |> Seq.toList

let lower_camel_identifier_violations =
    source_files ()
    |> Seq.collect (fun (path: string) ->
        let source = File.ReadAllText path
        let code = code_only source
        let source_lines = source.Replace("\r\n", "\n").Split '\n'

        lower_camel_identifier_pattern.Matches code
        |> Seq.cast<Match>
        |> Seq.filter (lower_camel_identifier_is_allowed code >> not)
        |> Seq.distinctBy (fun (matched: Match) -> matched.Groups["name"].Value)
        |> Seq.map (fun (matched: Match) ->
            let name = matched.Groups["name"].Value
            let line_number = code.AsSpan(0, matched.Index).Count '\n' + 1

            $"{path}({line_number}): value and parameter names must use snake_case: {name}: {source_lines[line_number - 1].Trim()}"))
    |> Seq.toList

for violation in violations do
    Console.Error.WriteLine violation

for violation in private_keyword_violations do
    Console.Error.WriteLine violation

for violation in literal_name_violations do
    Console.Error.WriteLine violation

for violation in lower_camel_identifier_violations do
    Console.Error.WriteLine violation

if
    not (List.isEmpty violations)
    || not (List.isEmpty private_keyword_violations)
    || not (List.isEmpty literal_name_violations)
    || not (List.isEmpty lower_camel_identifier_violations)
then
    Environment.Exit 1
