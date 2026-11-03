# fuzz finding parse-crash-formatexception-parser-signedliteral-4b0804bb
# category: parse-crash:FormatException@Parser.SignedLiteral
# FormatException: The input string '0x' was not in a correct format.
#    at System.Number.ThrowFormatException[TChar](ReadOnlySpan`1 value)
#    at System.Int64.Parse(String s)
#    at Alice.Compiler.Syntax.Parser.SignedLiteral() in src/Alice.Compiler/Syntax/Parser/Parser.Items.cs:line 169
#    at Alice.Compiler.Syntax.Parser.Rule() in src/Alice.Compiler/Syntax/Parser/Parser.Items.cs:line 133
#    at Alice.Compiler.Syntax.Parser.Items(List`1 into, Boolean top) in src/Alice.Compiler/Syntax/Parser/Parser.Items.cs:line 52
#    at Alice.Compiler.Syntax.Parser.File() in src/Alice.Compiler/Syntax/Parser/Parser.Items.cs:line 12
#    at Alice.Compiler.Syntax.Parser.ParseFile(String src, String file) in src/Alice.Compiler/Syntax/Parser/Parser.cs:line 50
#    at Alice.Fuzz.Oracles.<>c__DisplayClass7_0.<CheckSyntax>b__2() in tests/Alice.Tests/Fuzz/Engine/Oracles.cs:line 60

rule paddle_stays_on_screen(y in 0x
