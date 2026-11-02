namespace Alice.Compiler.Mir;

/// <summary>Dump forms of the state-machine jump nodes (Mir/MExpr.StateMachines.cs).</summary>
public sealed partial class MirDump
{
    /// <summary>`label L`, `goto L` / `leave L`, `switch value min: L0, _, L2`.</summary>
    bool StmtStateMachines(MStmt s, string at)
    {
        switch (s)
        {
            case AirLabel l: Line("label " + l.Name + at); return true;
            case AirGoto g: Line((g.Leave ? "leave " : "goto ") + g.Label + at); return true;
            case AirGotoTable t: Line($"switch {E(t.Value)} {t.Min}: {string.Join(", ", t.Labels.Select(x => x ?? "_"))}{at}"); return true;
        }
        return false;
    }
}
