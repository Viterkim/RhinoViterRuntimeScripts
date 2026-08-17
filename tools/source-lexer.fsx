module SourceLexer

open System
open System.Collections.Generic

type StringKind =
    | Quoted
    | Verbatim
    | Triple

type LexicalState =
    | Code
    | StringBody of StringKind * int
    | HoleCode of int * int
    | HoleFormat of int
    | Character
    | LineComment
    | BlockComment of int

let code_only (source: string) =
    let result = Text.StringBuilder(source.Length)
    let mutable state = Code
    let mutable index = 0
    let contexts = Stack<LexicalState>()

    let starts_with (text: string) =
        source.AsSpan(index).StartsWith(text.AsSpan(), StringComparison.Ordinal)

    let blank_run (count: int) =
        for offset = 0 to count - 1 do
            let character = source[index + offset]
            result.Append(if character = '\n' then '\n' else ' ') |> ignore

        index <- index + count

    let keep_run (count: int) =
        result.Append(source, index, count) |> ignore
        index <- index + count

    let enter (next: LexicalState) (count: int) =
        contexts.Push state
        blank_run count
        state <- next

    let leave (count: int) =
        blank_run count
        state <- contexts.Pop()

    while index < source.Length do
        match state with
        | (Code | HoleCode _) when starts_with "//" -> enter LineComment 2
        | (Code | HoleCode _) when starts_with "(*)" -> keep_run 3
        | (Code | HoleCode _) when starts_with "(*" -> enter (BlockComment 1) 2
        | (Code | HoleCode _) when starts_with "@$\"" -> enter (StringBody(Verbatim, 1)) 3
        | (Code | HoleCode _) when source[index] = '$' ->
            let mutable dollars = 1

            while index + dollars < source.Length && source[index + dollars] = '$' do
                dollars <- dollars + 1

            let tail = source.AsSpan(index + dollars)

            if tail.StartsWith("\"\"\"".AsSpan(), StringComparison.Ordinal) then
                enter (StringBody(Triple, dollars)) (dollars + 3)
            elif tail.StartsWith("@\"".AsSpan(), StringComparison.Ordinal) then
                enter (StringBody(Verbatim, dollars)) (dollars + 2)
            elif tail.StartsWith("\"".AsSpan(), StringComparison.Ordinal) then
                enter (StringBody(Quoted, dollars)) (dollars + 1)
            else
                keep_run dollars
        | (Code | HoleCode _) when starts_with "\"\"\"" -> enter (StringBody(Triple, 0)) 3
        | (Code | HoleCode _) when starts_with "@\"" -> enter (StringBody(Verbatim, 0)) 2
        | (Code | HoleCode _) when source[index] = '"' -> enter (StringBody(Quoted, 0)) 1
        | (Code | HoleCode _) when source[index] = '\'' ->
            let simple_character = index + 2 < source.Length && source[index + 2] = '\''

            let escaped_character =
                index + 3 < source.Length
                && source[index + 1] = '\\'
                && source[index + 3] = '\''

            if simple_character || escaped_character then
                enter Character 1
            else
                keep_run 1
        | Code -> keep_run 1
        | LineComment when source[index] = '\n' -> leave 1
        | LineComment -> blank_run 1
        | BlockComment depth when starts_with "(*" ->
            blank_run 2
            state <- BlockComment(depth + 1)
        | BlockComment depth when starts_with "*)" ->
            if depth = 1 then
                leave 2
            else
                blank_run 2
                state <- BlockComment(depth - 1)
        | BlockComment _ -> blank_run 1
        | StringBody(Triple, _) when starts_with "\"\"\"" -> leave 3
        | StringBody(Verbatim, _) when starts_with "\"\"" -> blank_run 2
        | StringBody(Verbatim, _) when source[index] = '"' -> leave 1
        | StringBody(Quoted, _) when source[index] = '\\' && index + 1 < source.Length -> blank_run 2
        | StringBody(Quoted, _) when source[index] = '"' -> leave 1
        | StringBody(_, 1) when starts_with "{{" || starts_with "}}" -> blank_run 2
        | StringBody(_, dollars) when dollars > 0 && starts_with (String('{', dollars)) ->
            enter (HoleCode(dollars, 0)) dollars
        | StringBody _ -> blank_run 1
        | HoleCode(dollars, 0) when starts_with (String('}', dollars)) -> leave dollars
        | HoleCode(dollars, 0) when source[index] = ':' || source[index] = ',' ->
            blank_run 1
            state <- HoleFormat dollars
        | HoleCode(dollars, depth) when source[index] = '(' || source[index] = '[' || source[index] = '{' ->
            keep_run 1
            state <- HoleCode(dollars, depth + 1)
        | HoleCode(dollars, depth) when source[index] = ')' || source[index] = ']' || source[index] = '}' ->
            keep_run 1
            state <- HoleCode(dollars, depth - 1)
        | HoleCode _ -> keep_run 1
        | HoleFormat dollars when starts_with (String('}', dollars)) -> leave dollars
        | HoleFormat _ -> blank_run 1
        | Character when source[index] = '\\' && index + 1 < source.Length -> blank_run 2
        | Character when source[index] = '\'' -> leave 1
        | Character -> blank_run 1

    result.ToString()
