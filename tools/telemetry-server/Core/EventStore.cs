using Alice.Telemetry.Wire;
using Microsoft.Data.Sqlite;

namespace Alice.TelemetryServer;

/// <summary>SQLite storage. The schema has no column for IP addresses, headers or anything outside the wire format.</summary>
public sealed class EventStore : IDisposable
{
    readonly SqliteConnection db;
    readonly object gate = new();

    public EventStore(string path)
    {
        SQLitePCL.Batteries_V2.Init();
        db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        db.Open();
        Exec("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS events(
              id INTEGER PRIMARY KEY, received_day TEXT NOT NULL, schema INTEGER NOT NULL, install_id TEXT NOT NULL,
              command TEXT NOT NULL, alice_version TEXT NOT NULL, os TEXT NOT NULL, arch TEXT NOT NULL, dotnet TEXT NOT NULL,
              exit_code INTEGER NOT NULL, duration_ms INTEGER NOT NULL, crash_type TEXT, crash_stack TEXT);
            CREATE TABLE IF NOT EXISTS error_codes(event_id INTEGER NOT NULL, code TEXT NOT NULL, count INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS phases(event_id INTEGER NOT NULL, name TEXT NOT NULL, ms INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS events_day ON events(received_day);
            """);
    }

    void Exec(string sql)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    /// <summary>Stores events with the receive date only (day granularity, UTC), never a timestamp tied to a request.</summary>
    public void Insert(IEnumerable<WireEvent> events, DateTime nowUtc)
    {
        lock (gate)
        {
            using var tx = db.BeginTransaction();
            foreach (var e in events)
            {
                using var c = db.CreateCommand();
                c.CommandText = """
                    INSERT INTO events(received_day, schema, install_id, command, alice_version, os, arch, dotnet, exit_code, duration_ms, crash_type, crash_stack)
                    VALUES($d,$s,$i,$c,$v,$o,$a,$n,$x,$m,$t,$k) RETURNING id
                    """;
                c.Parameters.AddWithValue("$d", nowUtc.ToString("yyyy-MM-dd"));
                c.Parameters.AddWithValue("$s", e.Schema);
                c.Parameters.AddWithValue("$i", e.InstallId);
                c.Parameters.AddWithValue("$c", e.Command);
                c.Parameters.AddWithValue("$v", e.AliceVersion);
                c.Parameters.AddWithValue("$o", e.Os);
                c.Parameters.AddWithValue("$a", e.Arch);
                c.Parameters.AddWithValue("$n", e.Dotnet);
                c.Parameters.AddWithValue("$x", e.ExitCode);
                c.Parameters.AddWithValue("$m", e.DurationMs);
                c.Parameters.AddWithValue("$t", (object?)e.CrashType ?? DBNull.Value);
                c.Parameters.AddWithValue("$k", (object?)e.CrashStack ?? DBNull.Value);
                long id = (long)c.ExecuteScalar()!;
                foreach (var (code, n) in e.ErrorCodes) Child("INSERT INTO error_codes VALUES($e,$k,$n)", id, code, n);
                if (e.Phases != null) foreach (var (name, ms) in e.Phases) Child("INSERT INTO phases VALUES($e,$k,$n)", id, name, ms);
            }
            tx.Commit();
        }
    }

    void Child(string sql, long id, string key, long n)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        c.Parameters.AddWithValue("$e", id);
        c.Parameters.AddWithValue("$k", key);
        c.Parameters.AddWithValue("$n", n);
        c.ExecuteNonQuery();
    }

    /// <summary>Deletes everything received more than <paramref name="days"/> days ago. Returns the number of events removed.</summary>
    public int Purge(int days, DateTime nowUtc)
    {
        lock (gate)
        {
            var cutoff = nowUtc.AddDays(-days).ToString("yyyy-MM-dd");
            using var c = db.CreateCommand();
            c.CommandText = """
                DELETE FROM error_codes WHERE event_id IN (SELECT id FROM events WHERE received_day < $c);
                DELETE FROM phases WHERE event_id IN (SELECT id FROM events WHERE received_day < $c);
                DELETE FROM events WHERE received_day < $c;
                SELECT changes();
                """;
            c.Parameters.AddWithValue("$c", cutoff);
            return Convert.ToInt32(c.ExecuteScalar());
        }
    }

    /// <summary>Aggregates only: no install ids or individual events leave the store.</summary>
    public Dictionary<string, object> Stats()
    {
        lock (gate)
        {
            var durations = Column<long>("SELECT duration_ms FROM events ORDER BY duration_ms");
            return new()
            {
                ["events"] = Scalar("SELECT COUNT(*) FROM events"),
                ["installs"] = Scalar("SELECT COUNT(DISTINCT install_id) FROM events"),
                ["commands"] = Group("SELECT command, COUNT(*) FROM events GROUP BY 1 ORDER BY 2 DESC"),
                ["versions"] = Group("SELECT alice_version, COUNT(*) FROM events GROUP BY 1 ORDER BY 2 DESC"),
                ["os"] = Group("SELECT os || '-' || arch, COUNT(*) FROM events GROUP BY 1 ORDER BY 2 DESC"),
                ["dotnet"] = Group("SELECT dotnet, COUNT(*) FROM events GROUP BY 1 ORDER BY 2 DESC"),
                ["error_codes"] = Group("SELECT code, SUM(count) FROM error_codes GROUP BY 1 ORDER BY 2 DESC"),
                ["crash_signatures"] = Group(
                    "SELECT crash_type || ' @ ' || COALESCE(NULLIF(substr(crash_stack, instr(crash_stack, char(10)) + 4, 200), ''), '?'), COUNT(*) " +
                    "FROM events WHERE crash_type IS NOT NULL GROUP BY 1 ORDER BY 2 DESC LIMIT 50"),
                ["duration_ms"] = new Dictionary<string, long> { ["p50"] = Pct(durations, 0.50), ["p95"] = Pct(durations, 0.95) },
                ["phases_ms_avg"] = Group("SELECT name, CAST(AVG(ms) AS INTEGER) FROM phases GROUP BY 1"),
            };
        }
    }

    static long Pct(List<long> sorted, double p) => sorted.Count == 0 ? 0 : sorted[(int)Math.Min(sorted.Count - 1, Math.Ceiling(p * sorted.Count) - 1)];

    long Scalar(string sql)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        return Convert.ToInt64(c.ExecuteScalar());
    }

    List<T> Column<T>(string sql)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        using var r = c.ExecuteReader();
        var l = new List<T>();
        while (r.Read()) l.Add(r.GetFieldValue<T>(0));
        return l;
    }

    Dictionary<string, long> Group(string sql)
    {
        using var c = db.CreateCommand();
        c.CommandText = sql;
        using var r = c.ExecuteReader();
        var d = new Dictionary<string, long>();
        while (r.Read()) d[r.GetString(0)] = r.GetInt64(1);
        return d;
    }

    /// <summary>Every stored text value (for tests that check nothing personal is persisted).</summary>
    public IEnumerable<string> AllText()
    {
        lock (gate)
        {
            foreach (var t in new[] { "events", "error_codes", "phases" })
            {
                using var c = db.CreateCommand();
                c.CommandText = $"SELECT * FROM {t}";
                using var r = c.ExecuteReader();
                while (r.Read()) for (int i = 0; i < r.FieldCount; i++) if (!r.IsDBNull(i)) yield return Convert.ToString(r.GetValue(i))!;
            }
        }
    }

    public void Dispose() => db.Dispose();
}
