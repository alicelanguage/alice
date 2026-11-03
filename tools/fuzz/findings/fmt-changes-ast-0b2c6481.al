# fuzz finding fmt-changes-ast-0b2c6481
# category: fmt-changes-ast
# original:  ...rowed=False, Res="", Type=AType()}, Value=LitExpr{Kind="str", Res="", Text="a\\n    end\\nb", Type=AType()}}}], Path="fuzz.al"}
# formatted: ...rowed=False, Res="", Type=AType()}, Value=LitExpr{Kind="str", Res="", Text="a\\nend\\nb", Type=AType()}}}], Path="fuzz.al"}

s = "a
    end
b"
