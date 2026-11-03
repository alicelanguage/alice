namespace Alice.Check;

/// <summary>
/// An LRAT proof checker (RUP steps only, as CaDiCaL's `--lrat` writes them). Each added clause names the clauses
/// (hints) that, with the clause's literals assumed false, become unit one after the other until one is falsified.
/// The proof refutes the CNF when it derives the empty clause. Anything else (a hint that is not unit, a RAT step, an
/// unknown clause id) rejects the proof.
/// </summary>
public static class Lrat
{
    /// <summary>Null when <paramref name="proof"/> derives the empty clause from <paramref name="dimacs"/>, else why not.</summary>
    public static string? Check(string dimacs, string proof)
    {
        var db = new Dictionary<long, int[]>();
        int vars = 0;
        long id = 0;
        foreach (var line in dimacs.Split('\n'))
        {
            if (line.Length == 0 || line[0] == 'c') continue;
            if (line.StartsWith("p cnf ")) { vars = int.Parse(line.Split(' ')[2]); continue; }
            var lits = Numbers(line);
            if (lits.Count == 0 || lits[^1] != 0) return $"CNF clause `{line}` does not end in 0";
            db[++id] = lits.Take(lits.Count - 1).Select(x => (int)x).ToArray();
            if (db[id].Any(l => l == 0 || Math.Abs(l) > vars)) return $"CNF clause {id} uses a variable beyond {vars}";
        }
        var stamp = new int[vars + 1];
        var value = new bool[vars + 1];
        int round = 0;
        int n = 0;
        foreach (var line in proof.Split('\n'))
        {
            n++;
            if (line.Length == 0 || line[0] == 'c') continue;
            bool deletion = line.Contains(" d ");
            var xs = Numbers(deletion ? line.Replace(" d ", " ") : line);   // `id d ids 0` or `id lits 0 hints 0`
            int z = xs.Count > 1 ? xs.IndexOf(0, 1) : -1;
            if (z < 0 || xs[^1] != 0 || (!deletion && z == xs.Count - 1)) return $"proof line {n} is malformed";
            long cid = xs[0];
            if (deletion)
            {
                foreach (var del in xs.Skip(1).SkipLast(1)) db.Remove(del);
                continue;
            }
            if (db.ContainsKey(cid) || cid <= id) return $"proof line {n}: clause id {cid} is not new";
            id = cid;
            var clause = xs.Skip(1).Take(z - 1).Select(x => (int)x).ToArray();
            var hints = xs.Skip(z + 1).SkipLast(1).ToList();
            if (clause.Any(l => Math.Abs(l) > vars)) return $"proof line {n} introduces a new variable";
            if (hints.Any(h => h < 0)) return $"proof line {n} is a RAT step; only RUP steps are accepted";
            round++;
            bool conflict = false;
            foreach (var l in clause)
            {
                int v = Math.Abs(l);
                if (stamp[v] == round && value[v] == (l > 0)) { conflict = true; break; }   // a tautology
                stamp[v] = round;
                value[v] = l < 0;   // assume the clause false
            }
            foreach (var h in hints)
            {
                if (conflict) break;
                if (!db.TryGetValue(h, out var hc)) return $"proof line {n}: hint {h} is not a live clause";
                int open = 0, unit = 0;
                foreach (var l in hc)
                {
                    int v = Math.Abs(l);
                    if (stamp[v] != round) { open++; unit = l; }
                    else if (value[v] == (l > 0)) return $"proof line {n}: hint {h} is already satisfied";
                }
                if (open == 0) conflict = true;
                else if (open == 1) { stamp[Math.Abs(unit)] = round; value[Math.Abs(unit)] = unit > 0; }
                else return $"proof line {n}: hint {h} is not unit";
            }
            if (!conflict) return $"proof line {n}: the hints do not refute the clause's negation";
            if (clause.Length == 0) return null;   // the empty clause: the CNF is unsatisfiable
            db[cid] = clause;
        }
        return "the proof does not derive the empty clause";
    }

    static List<long> Numbers(string line)
    {
        var r = new List<long>();
        foreach (var p in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            r.Add(long.TryParse(p, out var x) ? x : throw new FormatException($"`{p}` is not a number"));
        return r;
    }
}
