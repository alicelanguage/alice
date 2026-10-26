namespace Alice.Cli.Telemetry;

/// <summary>Local JSON-lines queue, capped in size: when full, the oldest events are dropped.</summary>
public sealed class TelemetryQueue(string dir, long maxBytes = 256 * 1024)
{
    public string Path { get; } = System.IO.Path.Combine(dir, "telemetry-queue.jsonl");

    public void Append(string json)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var lines = File.Exists(Path) ? File.ReadAllLines(Path).ToList() : [];
            lines.Add(json);
            long size = lines.Sum(l => (long)l.Length + 1);
            int drop = 0;
            while (size > maxBytes && drop < lines.Count) size -= lines[drop++].Length + 1;
            File.WriteAllLines(Path, lines.Skip(drop));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Takes every queued event and empties the queue.</summary>
    public List<string> Drain()
    {
        try
        {
            if (!File.Exists(Path)) return [];
            var lines = File.ReadAllLines(Path).Where(l => l.Length > 0).ToList();
            File.Delete(Path);
            return lines;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}
