# fuzz finding fmt-breaks-parse-041da8f7
# category: fmt-breaks-parse
# the program parses but `alice fmt` output does not:
# CompileException: fuzz.al:3:1: expected 'end' (found 'end of file')
#    at Alice.Compiler.Syntax.Parser.ExpectKw(String kw) in src/Alice.Compiler/Syntax/Parser/Parser.cs:line 89
#    at Alice.Compiler.Syntax.Parser.TypeItem() in src/Alice.Compiler/Syntax/Parser/Parser.Items.cs:line 299
#    at Alice.Compiler.Syntax.Parser.Items(List`1 into, Boolean top) in src/Alice.Compiler/Syntax/Parser/Parser.Items.cs:line 50
#    at Alice.Compiler.Syntax.Parser.File() in src/Alice.Compiler/Syntax/Parser/Parser.Items.cs:line 12
#    at Alice.Compiler.Syntax.Parser.ParseFile(String src, String file) in src/Alice.Compiler/Syntax/Parser/Parser.cs:line 50
#    at Alice.Fuzz.Oracles.<>c__DisplayClass7_0.<CheckSyntax>b__3() in tests/Alice.Tests/Fuzz/Engine/Oracles.cs:line 63
#    at Alice.Fuzz.Watchdog.<>c__DisplayClass1_0`1.<Run>b__0() in tests/Alice.Tests/Fuzz/Engine/FuzzCore.cs:line 142
# --- formatted:
# record P(x : Int32)
#   def f(a : Int32, &blk : Int32 -> Int32) : Int32
#   end
# 

record P(x : Int32)
def f(a : Int32, &blk : Int32 -> Int32) : Int32
end
