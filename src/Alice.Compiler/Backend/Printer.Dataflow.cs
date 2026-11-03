using System.Globalization;
using System.Text;
using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend;

/// <summary>
/// C# for `dataflow do ... end` (Mir/MExpr.Dataflow.cs, ADR 0038/0040 section 4). The use site is a call of the
/// runner; <see cref="DataflowMembers"/> prints the runner and one static method per node into the enclosing type.
/// The runner is `unsafe` only to take `&amp;method`: the runtime's function-pointer overloads (`Graph.Source`,
/// `Pipe.Stage`, `Pipe.Replicate`, `Pipe.Sink`), so no delegate is created and every batch is a direct call.
/// </summary>
public sealed partial class Printer
{
    const string Df = "global::Alice.Runtime";

    static string? EDataflow(MExpr e) => e is AirDataflow d ? d.Runner + "(" + Join(d.Args) + ")" : null;

    static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>The runner and node methods of <paramref name="d"/>, as members of the enclosing type.</summary>
    public static string DataflowMembers(AirDataflow d)
    {
        var sb = new StringBuilder();
        foreach (var st in d.Stages) sb.Append(StageMethod(st));
        sb.Append(Runner(d));
        return sb.ToString();
    }

    static string Runner(AirDataflow d)
    {
        var sb = new StringBuilder();
        string ps = string.Join(", ", d.Params.Select(p => p.Type.ToCs() + " " + p.Name));
        sb.Append($"private static unsafe {d.ResultCs} {d.Runner}({ps})\n{{\n");
        var gargs = new List<string>();
        if (d.Capacity > 0) gargs.Add("capacity: " + I(d.Capacity));
        if (d.Batch > 0) gargs.Add("batch: " + I(d.Batch));
        sb.Append($"    var __df_g = new {Df}.Graph({string.Join(", ", gargs)});\n");
        for (int k = 0; k < d.Stages.Count; k++)
        {
            var st = d.Stages[k];
            string cap = st.Output >= 0 && d.Rings[st.Output].Capacity > 0 ? ", " + I(d.Rings[st.Output].Capacity) : "";
            string init = st.State.Count switch
            {
                0 => "(byte)0",
                1 => E(st.State[0].Init),
                _ => "(" + string.Join(", ", st.State.Select(f => E(f.Init))) + ")",
            };
            string from = st.Input >= 0 ? $"__df_e{I(Producer(d, st.Input))}" : "__df_g";
            switch (st.Kind)
            {
                case AirStageKind.Source:
                    {
                        string it = $"global::System.Collections.Generic.IEnumerator<{st.Out!.ToCs()}>";
                        string args = string.Join(", ", st.State.Select(f => E(f.Init)));
                        sb.Append($"    var __df_e{I(k)} = __df_g.Source<{it}, {st.Out!.ToCs()}>({st.Method}({args}), &{st.Pump}{cap});\n");
                        break;
                    }
                case AirStageKind.Stage:
                    sb.Append($"    var __df_e{I(k)} = {from}.Stage<{st.StateCs}, {st.Out!.ToCs()}>({init}, &{st.Method}{cap});\n");
                    break;
                case AirStageKind.Replicate:
                    sb.Append($"    var __df_e{I(k)} = {from}.Replicate<{st.StateCs}, {st.Out!.ToCs()}>({init}, &{st.Method}, {I(st.Workers)}, {(st.Ordered ? "true" : "false")}, {I(st.Batch)}{(cap.Length > 0 ? cap : ", 0")});\n");
                    break;
                case AirStageKind.Sink:
                    sb.Append($"    var __df_e{I(k)} = {from}.Sink<{st.StateCs}>({init}, &{st.Method});\n");
                    break;
            }
        }
        sb.Append("    __df_g.Run();\n");
        if (d.Results.Count > 0)
        {
            var parts = d.Results.Select(r => d.Stages[r.Stage].FieldPath($"__df_e{I(r.Stage)}.State", r.Field)).ToList();
            sb.Append("    return ").Append(parts.Count == 1 ? parts[0] : "(" + string.Join(", ", parts) + ")").Append(";\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    static int Producer(AirDataflow d, int ring) => d.Stages.FindIndex(s => s.Output == ring);

    static string StageMethod(AirStage st)
    {
        var sb = new StringBuilder();
        if (st.Kind == AirStageKind.Source)
        {
            string elem = st.Out!.ToCs();
            string it = $"global::System.Collections.Generic.IEnumerator<{elem}>";
            string ps = string.Join(", ", st.State.Select(f => f.Type.ToCs() + " " + f.Name));
            var body = new Printer("", 1);
            body.Stmts(st.Body);
            sb.Append($"private static {it} {st.Method}({ps})\n{{\n{body}    yield break;\n}}\n");
            sb.Append($"private static bool {st.Pump}(ref {it} __df_s, ref {Df}.RingWriter<{elem}> __df_w)\n{{\n");
            sb.Append("    while (__df_w.HasRoom)\n    {\n");
            sb.Append("        if (!__df_s.MoveNext())\n        {\n            __df_s.Dispose();\n            return false;\n        }\n");
            sb.Append("        __df_w.Write(__df_s.Current);\n    }\n    return true;\n}\n");
            return sb.ToString();
        }
        var p = new Printer("", 2);
        p.Stmts(st.Body);
        string sig = $"ref {st.StateCs} __df_s, global::System.ReadOnlySpan<{st.In!.ToCs()}> __df_in";
        if (st.Kind != AirStageKind.Sink) sig += $", ref {Df}.RingWriter<{st.Out!.ToCs()}> {st.Writer}";
        sb.Append($"private static void {st.Method}({sig})\n{{\n");
        for (int k = 0; k < st.State.Count; k++)
            sb.Append($"    ref {st.State[k].Type.ToCs()} {st.State[k].Name} = ref {st.FieldPath("__df_s", k)};\n");
        sb.Append("    for (int __df_i = 0; __df_i < __df_in.Length; __df_i++)\n    {\n");
        sb.Append($"        var {st.Item} = __df_in[__df_i];\n");
        sb.Append(p.ToString());
        sb.Append("    }\n}\n");
        return sb.ToString();
    }
}
