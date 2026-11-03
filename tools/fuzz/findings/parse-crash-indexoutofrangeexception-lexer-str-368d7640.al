# fuzz finding parse-crash-indexoutofrangeexception-lexer-str-368d7640
# category: parse-crash:IndexOutOfRangeException@Lexer.Str
# IndexOutOfRangeException: Index was outside the bounds of the array.
#    at System.String.get_Chars(Int32 index)
#    at Alice.Compiler.Lexer.Str(Int32 l, Int32 c) in src/Alice.Compiler/Syntax/Lexer.cs:line 406
#    at Alice.Compiler.Lexer.Lex() in src/Alice.Compiler/Syntax/Lexer.cs:line 85
#    at Alice.Compiler.Syntax.Parser..ctor(String src, String file, Int32 line) in src/Alice.Compiler/Syntax/Parser/Parser.cs:line 35
#    at Alice.Compiler.Syntax.Parser.ParseFile(String src, String file) in src/Alice.Compiler/Syntax/Parser/Parser.cs:line 50
#    at Alice.Fuzz.Oracles.<>c__DisplayClass7_0.<CheckSyntax>b__2() in tests/Alice.Tests/Fuzz/Engine/Oracles.cs:line 58
#    at Alice.Fuzz.Watchdog.<>c__DisplayClass1_0`1.<Run>b__0() in tests/Alice.Tests/Fuzz/Engine/FuzzCore.cs:line 142

x = "\