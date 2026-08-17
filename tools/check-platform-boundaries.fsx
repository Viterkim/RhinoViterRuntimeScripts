#load "source-lexer.fsx"

open System
open System.IO
open System.Text.RegularExpressions
open SourceLexer

let source_root = fsi.CommandLineArgs |> Array.last |> Path.GetFullPath
let platform_root = Path.Combine(source_root, "Platform") |> Path.GetFullPath

type Rule = { name: string; pattern: Regex }

let rule (name: string) (pattern: string) =
    { name = name
      pattern = Regex(pattern, RegexOptions.Compiled) }

let forbidden =
    [ rule "System.Drawing" @"\bSystem\.Drawing\b"
      rule "System.Windows.Forms" @"\bSystem\.Windows\.Forms\b"
      rule "DllImport" @"\bDllImport\b"
      rule "Platform.Win" @"\bPlatform\.Win\b"
      rule "Win32" @"\bWin32(?:Native)?\b"
      rule "nativeint" @"\bnativeint\b"
      rule "unativeint" @"\bunativeint\b"
      rule "window handle" @"\.Handle\b"
      rule "screen rectangle" @"\.ScreenRectangle\b" ]

let is_inside (directory: string) (path: string) =
    let prefix =
        directory.TrimEnd(Path.DirectorySeparatorChar)
        + string Path.DirectorySeparatorChar

    path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)

let violations =
    Directory.EnumerateFiles(source_root, "*.fs", SearchOption.AllDirectories)
    |> Seq.filter (is_inside platform_root >> not)
    |> Seq.collect (fun (path: string) ->
        code_only (File.ReadAllText path)
        |> fun (code: string) -> code.Replace("\r\n", "\n").Split '\n'
        |> Seq.mapi (fun (index: int) (line: string) -> index + 1, line)
        |> Seq.collect (fun (line_number: int, line: string) ->
            forbidden
            |> Seq.choose (fun (rule: Rule) ->
                if rule.pattern.IsMatch line then
                    Some $"{path}({line_number}): platform implementation detail '{rule.name}' escaped src/Platform"
                else
                    None)))
    |> Seq.toList

for violation in violations do
    Console.Error.WriteLine violation

if not (List.isEmpty violations) then
    Environment.Exit 1
