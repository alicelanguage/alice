using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Alice.Cli.Lsp;

/// <summary>LSP base protocol: `Content-Length: N\r\n\r\n` + UTF-8 JSON body.</summary>
public static class JsonRpc
{
    /// <summary>Reads one message; null at end of stream.</summary>
    public static JsonObject? Read(Stream input)
    {
        int length = -1;
        var line = new StringBuilder();
        while (true)
        {
            int b = input.ReadByte();
            if (b < 0) return null;
            if (b == '\n')
            {
                var header = line.ToString().TrimEnd('\r');
                line.Clear();
                if (header.Length == 0)
                {
                    if (length < 0) continue;
                    break;
                }
                if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    length = ParseLength(header.AsSpan("Content-Length:".Length).Trim());
            }
            else { if (line.Length >= 8192) throw new InvalidDataException("LSP header too large"); line.Append((char)b); }
        }
        var body = new byte[length];
        int read = 0;
        while (read < length)
        {
            int n = input.Read(body, read, length - read);
            if (n <= 0) return null;
            read += n;
        }
        return JsonNode.Parse(body) as JsonObject;
    }

    static int ParseLength(ReadOnlySpan<char> value) =>
        int.TryParse(value, out int length) && length >= 0 && length <= 32 * 1024 * 1024
            ? length : throw new InvalidDataException("Invalid LSP Content-Length");

    static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly object WriteLock = new();

    public static void Write(Stream output, JsonObject message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString(JsonOptions));
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (WriteLock)
        {
            output.Write(header);
            output.Write(body);
            output.Flush();
        }
    }
}
