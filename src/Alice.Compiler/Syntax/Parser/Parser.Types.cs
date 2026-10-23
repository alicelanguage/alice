namespace Alice.Compiler.Syntax;

public sealed partial class Parser
{
    /// <summary>`<` opens a balanced list of type tokens closed by `>` (or the `>>` of a nested one).</summary>
    bool AngleTypesAhead()
    {
        int k = p + 1, d = 1;
        while (k < t.Count && d > 0)
        {
            var x = t[k];
            if (x.Kind == "op" && x.Text == "<") d++;
            else if (x.Kind == "op" && x.Text == ">") d--;
            else if (x.Kind == "op" && x.Text == ">>") d -= 2;
            else if (!(x.Kind == "const" || x.Kind == "op" && x.Text is "," or "::" or "(" or ")" or "?")) return false;
            k++;
        }
        return d <= 0;
    }

    TypeRef Type()
    {
        Token tok = Cur;
        if (IsOp("{"))
        {
            Next();
            var tr = At(new TypeRef { Special = "tuple" }, tok);
            if (t[p].Kind == "id" && Peek(1).Kind == "op" && Peek(1).Text == ":")   // named: {min: Int32, max: Int32}
            {
                tr.TupleNames = [];
                do
                {
                    if (IsOp(",")) Next();
                    tr.TupleNames.Add(Next().Text);
                    Expect(":");
                    tr.Args.Add(Type());
                } while (IsOp(","));
            }
            else
            {
                tr.Args.Add(Type());
                while (IsOp(",")) { Next(); tr.Args.Add(Type()); }
            }
            Expect("}");
            return tr;
        }
        if (t[p].Kind != "const") throw Err("expected type");
        var r = At(new TypeRef(), tok);
        string name = Next().Text;
        if (name.EndsWith("?")) { r.Nullable = true; name = name[..^1]; }
        while (IsOp("::") || (IsOp(".") && Peek(1).Kind == "const"))
        {
            Next();
            string seg = Next().Text;
            if (seg.EndsWith("?")) { r.Nullable = true; seg = seg[..^1]; }
            name += "." + seg;
        }
        r.Name = name;
        if (IsOp("(") && !Cur.Space)
        {
            Next();
            r.Args.Add(Type());
            while (IsOp(",")) { Next(); r.Args.Add(Type()); }
            Expect(")");
        }
        else if (IsOp("<") && !Cur.Space && AngleTypesAhead())   // C# spelling inside generic calls: ArrayView1D<Float32, Stride1D::Dense>
        {
            Next();
            r.Args.Add(Type());
            while (IsOp(",")) { Next(); r.Args.Add(Type()); }
            ExpectCloseAngle();
        }
        if (IsOp("?") && !Cur.Space) { Next(); r.Nullable = true; }
        if (IsOp("*") && !Cur.Space) { Next(); r.Pointer = true; }
        return r;
    }

    /// <summary>Crystal proc type: `A, B -> R`, `A ->` (no result), `-> R`. Args holds params then the result (or none).</summary>
    TypeRef ProcType()
    {
        var r = At(new TypeRef { Special = "proc" }, Cur);
        var ps = new List<TypeRef>();
        if (!IsOp("->"))
        {
            ps.Add(Type());
            while (IsOp(",")) { Next(); ps.Add(Type()); }
        }
        Expect("->");
        r.Args.AddRange(ps);
        if (t[p].Kind == "const" || IsOp("{")) r.Args.Add(Type());
        else r.Args.Add(new TypeRef { Name = "Nil" });
        return r;
    }

    /// <summary>Generic suffix after a type name in expression position: `List(Int32)`.</summary>
    TypeRef TypeFromName(string name, Token tok)
    {
        var r = At(new TypeRef(), tok);
        if (name.EndsWith("?")) { r.Nullable = true; name = name[..^1]; }
        r.Name = name;
        if (IsOp("(") && !Cur.Space)
        {
            Next();
            r.Args.Add(Type());
            while (IsOp(",")) { Next(); r.Args.Add(Type()); }
            Expect(")");
        }
        return r;
    }
}
