open System
open System.IO
open System.Text.RegularExpressions

let sourceRoot: string = fsi.CommandLineArgs |> Array.last

let hasUntypedParameters (parameters: string) =
    let text = parameters.Trim()

    if String.IsNullOrWhiteSpace text then
        false
    else
        let groups = Regex.Matches(text, @"\((?<parameter>[^()]*)\)")
        let bareParameters = Regex.Replace(text, @"\([^()]*\)", "").Trim()

        if groups.Count = 0 then
            true
        elif not (String.IsNullOrWhiteSpace bareParameters) then
            true
        else
            groups
            |> Seq.cast<Match>
            |> Seq.exists (fun (group: Match) ->
                let parameter = group.Groups["parameter"].Value.Trim()
                not (String.IsNullOrWhiteSpace parameter) && not (parameter.Contains ':'))

let letPrefix =
    @"^\s*let\s+(?!mutable\b)(?>(?:(?:rec|inline|private|internal|public)\s+)*)(?!struct\b)"

let checks =
    [ "function",
      Regex(
          letPrefix
          + @"[A-Za-z_][\w']*(?:<[^>]+>)?\s+(?<parameters>(?:(?:\([^)]*\)|[A-Za-z_][\w']*)\s*)+)(?:\s*:\s*[^=]+)?\s*=",
          RegexOptions.Compiled
      )
      "member",
      Regex(
          @"^\s*(?:member|override)\s+[^.]+\.[A-Za-z_][\w']*\s+(?<parameters>(?:(?:\([^)]*\)|[A-Za-z_][\w']*)\s*)+)(?:\s*:\s*[^=]+)?\s*=",
          RegexOptions.Compiled
      )
      "constructor", Regex(@"^\s*type\s+[A-Za-z_][\w']*(?:<[^>]+>)?\s*(?<parameters>\([^)]*\))", RegexOptions.Compiled)
      "lambda", Regex(@"\bfun\s+(?<parameters>.*?)\s*->", RegexOptions.Compiled) ]

let violationsInLine (line: string) =
    checks
    |> Seq.choose (fun (kind: string, pattern: Regex) ->
        let matched = pattern.Match line

        if matched.Success && hasUntypedParameters matched.Groups["parameters"].Value then
            Some kind
        else
            None)
    |> Seq.toList

type FragmentEnd =
    | Equals
    | Arrow

type SourceFragment = { line_number: int; text: string }

let declarationStart =
    Regex(@"^\s*(?:let\b|member\b|override\b|type\s+[A-Za-z_][\w']*)", RegexOptions.Compiled)

let fragmentEnd (line: string) =
    if declarationStart.IsMatch line && not (line.Contains '=') then
        Some Equals
    elif Regex.IsMatch(line, @"\bfun\b") && not (line.Contains "->") then
        Some Arrow
    else
        None

let isComplete (ending: FragmentEnd) (text: string) =
    match ending with
    | Equals -> text.Contains '='
    | Arrow -> text.Contains "->"

let sourceFragments (source: string) =
    let lines = source.Replace("\r\n", "\n").Split '\n'
    let fragments = ResizeArray<SourceFragment>()
    let mutable index = 0

    while index < lines.Length do
        let firstLineNumber = index + 1
        let firstLine = lines[index]

        match fragmentEnd firstLine with
        | None ->
            fragments.Add
                { line_number = firstLineNumber
                  text = firstLine }

            index <- index + 1
        | Some ending ->
            let text = Text.StringBuilder(firstLine.TrimEnd())
            index <- index + 1

            while index < lines.Length && not (isComplete ending (text.ToString())) do
                text.Append(' ').Append(lines[index].Trim()) |> ignore
                index <- index + 1

            fragments.Add
                { line_number = firstLineNumber
                  text = text.ToString() }

    fragments |> Seq.toList

let violationsInSource (source: string) =
    sourceFragments source
    |> Seq.collect (fun (fragment: SourceFragment) ->
        violationsInLine fragment.text
        |> Seq.map (fun (kind: string) -> fragment.line_number, fragment.text, kind))
    |> Seq.toList

type LexicalState =
    | Code
    | String
    | VerbatimString
    | TripleString
    | Character
    | LineComment
    | BlockComment of int

let codeOnly (source: string) =
    let result = Text.StringBuilder(source.Length)
    let mutable state = Code
    let mutable index = 0

    let blank (character: char) =
        result.Append(if character = '\n' then '\n' else ' ') |> ignore

    let blankPair () =
        blank source[index]
        blank source[index + 1]
        index <- index + 2

    let blankTriple () =
        blank source[index]
        blank source[index + 1]
        blank source[index + 2]
        index <- index + 3

    while index < source.Length do
        match state with
        | Code when source.AsSpan(index).StartsWith("//".AsSpan(), StringComparison.Ordinal) ->
            blankPair ()
            state <- LineComment
        | Code when source.AsSpan(index).StartsWith("(*".AsSpan(), StringComparison.Ordinal) ->
            blankPair ()
            state <- BlockComment 1
        | Code when source.AsSpan(index).StartsWith("\"\"\"".AsSpan(), StringComparison.Ordinal) ->
            blankTriple ()
            state <- TripleString
        | Code when source.AsSpan(index).StartsWith("@\"".AsSpan(), StringComparison.Ordinal) ->
            blankPair ()
            state <- VerbatimString
        | Code when source[index] = '"' ->
            blank source[index]
            index <- index + 1
            state <- String
        | Code when source[index] = '\'' ->
            let simpleCharacter = index + 2 < source.Length && source[index + 2] = '\''

            let escapedCharacter =
                index + 3 < source.Length
                && source[index + 1] = '\\'
                && source[index + 3] = '\''

            if simpleCharacter || escapedCharacter then
                blank source[index]
                index <- index + 1
                state <- Character
            else
                result.Append(source[index]) |> ignore
                index <- index + 1
        | Code ->
            result.Append(source[index]) |> ignore
            index <- index + 1
        | LineComment when source[index] = '\n' ->
            blank source[index]
            index <- index + 1
            state <- Code
        | LineComment ->
            blank source[index]
            index <- index + 1
        | BlockComment depth when source.AsSpan(index).StartsWith("(*".AsSpan(), StringComparison.Ordinal) ->
            blankPair ()
            state <- BlockComment(depth + 1)
        | BlockComment depth when source.AsSpan(index).StartsWith("*)".AsSpan(), StringComparison.Ordinal) ->
            blankPair ()
            state <- if depth = 1 then Code else BlockComment(depth - 1)
        | BlockComment _ ->
            blank source[index]
            index <- index + 1
        | String when source[index] = '\\' && index + 1 < source.Length -> blankPair ()
        | String when source[index] = '"' ->
            blank source[index]
            index <- index + 1
            state <- Code
        | String ->
            blank source[index]
            index <- index + 1
        | VerbatimString when source.AsSpan(index).StartsWith("\"\"".AsSpan(), StringComparison.Ordinal) -> blankPair ()
        | VerbatimString when source[index] = '"' ->
            blank source[index]
            index <- index + 1
            state <- Code
        | VerbatimString ->
            blank source[index]
            index <- index + 1
        | TripleString when source.AsSpan(index).StartsWith("\"\"\"".AsSpan(), StringComparison.Ordinal) ->
            blankTriple ()
            state <- Code
        | TripleString ->
            blank source[index]
            index <- index + 1
        | Character when source[index] = '\\' && index + 1 < source.Length -> blankPair ()
        | Character when source[index] = '\'' ->
            blank source[index]
            index <- index + 1
            state <- Code
        | Character ->
            blank source[index]
            index <- index + 1

    result.ToString()

let privateKeywordPattern = Regex(@"\bprivate\b", RegexOptions.Compiled)

let literalDeclarationPattern =
    Regex(@"\[<Literal>\]\s*let\s+(?:(?:private|internal|public)\s+)*(?<name>[A-Za-z_][\w']*)", RegexOptions.Compiled)

let yellingSnakeCasePattern = Regex(@"^[A-Z][A-Z0-9_]*$", RegexOptions.Compiled)

let checkerSelfTests =
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
      "let run =\n    fun\n        (value: int)\n        -> value", false
      "let run =\n    fun\n        value\n        -> value", true ]

for source, expectsViolation in checkerSelfTests do
    let hasViolation = not (List.isEmpty (violationsInSource source))

    if hasViolation <> expectsViolation then
        failwith $"Explicit-input lint self-test failed for: {source}"

let privateKeywordSelfTests =
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

for source, expectsViolation in privateKeywordSelfTests do
    if privateKeywordPattern.IsMatch(codeOnly source) <> expectsViolation then
        failwith $"No-private lint self-test failed for: {source}"

let literalNameSelfTests =
    [ "CURRENT_VERSION", true
      "WM_RBUTTONDOWN", true
      "BUTTON_4_UP", true
      "current_version", false
      "CurrentVersion", false
      "4_BUTTON", false ]

for name, expected in literalNameSelfTests do
    if yellingSnakeCasePattern.IsMatch(name) <> expected then
        failwith $"Literal-name lint self-test failed for: {name}"

let violations =
    Directory.EnumerateFiles(sourceRoot, "*.fs", SearchOption.AllDirectories)
    |> Seq.collect (fun (path: string) ->
        File.ReadAllText path
        |> violationsInSource
        |> Seq.map (fun (lineNumber: int, line: string, kind: string) ->
            $"{path}({lineNumber}): {kind} input is missing an explicit type: {line.Trim()}"))
    |> Seq.toList

let privateKeywordViolations =
    Directory.EnumerateFiles(sourceRoot, "*.fs", SearchOption.AllDirectories)
    |> Seq.collect (fun (path: string) ->
        let source = File.ReadAllText path
        let code = codeOnly source
        let sourceLines = source.Replace("\r\n", "\n").Split '\n'

        privateKeywordPattern.Matches code
        |> Seq.cast<Match>
        |> Seq.map (fun (matched: Match) ->
            let lineNumber = code.AsSpan(0, matched.Index).Count '\n' + 1

            $"{path}({lineNumber}): the private keyword is not used in project source: {sourceLines[lineNumber - 1].Trim()}"))
    |> Seq.toList

let literalNameViolations =
    Directory.EnumerateFiles(sourceRoot, "*.fs", SearchOption.AllDirectories)
    |> Seq.collect (fun (path: string) ->
        let source = File.ReadAllText path
        let code = codeOnly source

        literalDeclarationPattern.Matches code
        |> Seq.cast<Match>
        |> Seq.choose (fun (matched: Match) ->
            let name = matched.Groups["name"].Value

            if yellingSnakeCasePattern.IsMatch name then
                None
            else
                let lineNumber =
                    source.Substring(0, matched.Index)
                    |> Seq.filter (fun (character: char) -> character = '\n')
                    |> Seq.length
                    |> (+) 1

                Some $"{path}({lineNumber}): literal name must use YELLING_SNAKE_CASE: {name}"))
    |> Seq.toList

for violation in violations do
    Console.Error.WriteLine violation

for violation in privateKeywordViolations do
    Console.Error.WriteLine violation

for violation in literalNameViolations do
    Console.Error.WriteLine violation

if
    not (List.isEmpty violations)
    || not (List.isEmpty privateKeywordViolations)
    || not (List.isEmpty literalNameViolations)
then
    Environment.Exit 1
