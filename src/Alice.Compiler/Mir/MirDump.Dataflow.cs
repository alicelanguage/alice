namespace Alice.Compiler.Mir;

/// <summary>Dump form of the dataflow nodes (Mir/MExpr.Dataflow.cs): the graph facts, one stage per s-expression.</summary>
public sealed partial class MirDump
{
    string? CoreDataflow(MExpr e)
    {
        if (e is not AirDataflow d) return null;
        var parts = new List<string> { "dataflow " + d.Runner + (d.Capacity > 0 ? " capacity " + d.Capacity : "") + (d.Batch > 0 ? " batch " + d.Batch : "") };
        foreach (var r in d.Rings) parts.Add($"(ring {r.Elem.ToCs()}{(r.Capacity > 0 ? " " + r.Capacity : "")})");
        foreach (var s in d.Stages)
        {
            string kind = s.Kind.ToString().ToLowerInvariant();
            string io = (s.Input >= 0 ? $" in {s.Input}:{s.In!.ToCs()}" : "") + (s.Output >= 0 ? $" out {s.Output}:{s.Out!.ToCs()}" : "");
            string flags = (s.Parallel ? " par" + (s.Workers > 0 ? " workers " + s.Workers : "") + (s.Batch > 0 ? " batch " + s.Batch : "") : "") + (s.Kind == AirStageKind.Replicate && !s.Ordered ? " unordered" : "") + (s.MapValue != null ? " map" : "");
            string state = s.State.Count == 0 ? "" : " (state " + string.Join(" ", s.State.Select(f => f.Name + ":" + f.Type.ToCs())) + ")";
            parts.Add($"(stage {kind} {s.Method}{io}{flags}{state})");
        }
        foreach (var r in d.Results) parts.Add($"(result {r.Stage}.{r.Field}:{r.Type.ToCs()})");
        parts.AddRange(d.Args.Select(E));
        return "(" + string.Join(" ", parts) + ")";
    }
}
