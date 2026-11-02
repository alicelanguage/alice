namespace Alice.Compiler.Mir;

/// <summary>Dump forms of the parallel and ownership nodes (Mir/MExpr.Parallel.cs).</summary>
public sealed partial class MirDump
{
    string? CoreParallel(MExpr e)
    {
        switch (e)
        {
            case AirPar p:
                return $"(par {(p.Kind == AirParKind.Range ? "range" : "reduce " + p.ResultCs)} {E(p.Count)} {E(p.Kernel)}{(p.Combine != null ? " " + E(p.Combine) : "")}{(p.Grain is int g ? " grain " + g : "")})";
            case AirParGuard pg:
                return $"(par-guard {E(pg.Count)} {pg.Min}{L(pg.Distinct.Cast<MExpr>().ToList())})";
            case AirReferenceEqualsGuard r:
                return $"(distinct {E(r.W)} {E(r.R)})";
            case AirParProbe pr:
                return pr.Kind switch
                {
                    AirProbeKind.Timestamp => "(probe timestamp)",
                    AirProbeKind.Second => $"(probe second {E(pr.I!)} {E(pr.Start!)})",
                    _ => $"(probe offload {E(pr.I!)} {E(pr.Start!)} {E(pr.T0!)} {E(pr.Count!)})",
                };
            case AirForkJoin f:
                return $"(fork{L(f.Branches)})";
            case AirForkable:
                return "(forkable)";
            case AirRent r:
                return $"(rent {r.PoolCs}{L(r.Args)})";
            case AirRelease r:
                return $"(release {r.PoolCs} {E(r.Target)})";
            case AirLength l:
                return $"(length {l.Member} {E(l.Target)})";
            case AirTripCount t:
                return $"(trip-count {E(t.Start)} {E(t.End)}{(t.Inclusive ? " inclusive" : "")})";
            case AirOffset o:
                return $"(offset {E(o.Base)} {E(o.Offset)})";
            case AirPresizeCount c:
                return $"(presize-count {E(c.From)} {E(c.To)}{(c.Inclusive ? " inclusive" : "")})";
        }
        return null;
    }
}
