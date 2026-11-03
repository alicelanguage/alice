using System.Text;
using Alice.Compiler.Lowering;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;
using Binder = Alice.Compiler.Semantics.Binder;

namespace Alice.Compiler.Backend.Wasm;

/// <summary>Development probe: the lowered bodies the wasm backend receives, as MirDump text.</summary>
public static class WasmProbe
{
    public static string Dump(IReadOnlyList<SourceFile> files)
    {
        var asts = files.Select(f => Parser.ParseFile(f.Text, f.Path)).ToList();
        var binder = new Binder(asts, new ClrTypes(null));
        binder.Bind();
        var lowerer = new Lowerer(binder) { Sequential = true };
        var sb = new StringBuilder();
        foreach (var ast in asts)
        {
            foreach (var it in ast.Items)
                if (it is MethodDecl d && d.HasBody)
                {
                    var m = (MethodSym)d.Sym!;
                    lowerer.File = ast.Path; lowerer.Type = binder.Program; lowerer.Method = m;
                    var body = lowerer.LowerBody(d.Body, m.Ret.IsVoid ? Tail.None : Tail.Return, d.HoistedLocals, d.Line);
                    Optimizer.Pipeline.Run(body);
                    Air.Passes.IlLowering.Run(body);
                    sb.Append("def ").Append(m.CsName).Append('(').Append(string.Join(", ", m.Params.Select(p => p.Type.Cs + " " + p.CsName))).Append(") : ").Append(m.Ret.Cs).Append('\n');
                    sb.Append(MirDump.Of(body)).Append('\n');
                }
                else if (it is TypeDecl td)
                {
                    sb.Append("type ").Append(td.Name).Append(' ').Append(td.Kind).Append('\n');
                    foreach (var mm in td.Members.OfType<MethodDecl>().Where(x => x.HasBody))
                    {
                        var m = (MethodSym)mm.Sym!;
                        lowerer.File = ast.Path; lowerer.Type = (TypeSym)td.Sym!; lowerer.Method = m;
                        var body = lowerer.LowerBody(mm.Body, m.Ret.IsVoid || m.IsCtor ? Tail.None : Tail.Return, mm.HoistedLocals, mm.Line);
                        Optimizer.Pipeline.Run(body);
                        Air.Passes.IlLowering.Run(body);
                        sb.Append("  def ").Append(m.CsName).Append('(').Append(string.Join(", ", m.Params.Select(p => p.Type.Cs + " " + p.CsName))).Append(") : ").Append(m.Ret.Cs).Append('\n');
                        sb.Append(MirDump.Of(body)).Append('\n');
                    }
                }
            var stmts = ast.Items.OfType<StmtItem>().Select(s => s.Stmt).ToList();
            if (stmts.Count > 0)
            {
                lowerer.File = ast.Path; lowerer.Type = binder.Program; lowerer.Method = null;
                var body = lowerer.LowerBody(stmts, Tail.None, ast.HoistedLocals, stmts[0].Line);
                Optimizer.Pipeline.Run(body);
                Air.Passes.IlLowering.Run(body);
                sb.Append("main\n").Append(MirDump.Of(body)).Append('\n');
            }
            sb.Append("extra: ").Append(string.Join("\n", lowerer.Extra)).Append('\n');
        }
        return sb.ToString();
    }
}
