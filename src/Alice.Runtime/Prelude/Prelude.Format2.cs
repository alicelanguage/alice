using System.Text;
using System.Text.RegularExpressions;

namespace Alice.Runtime;

/// <summary>sprintf-style `format`: %d %i %s %f %e %x %X %o %b %c %%, flags `-0+ `, width, precision.</summary>
public static partial class Prelude
{
    // source-generated: a static `new Regex(..., Compiled)` here ran Reflection.Emit on every program's startup
    [GeneratedRegex(@"%([-0+ ]*)(\d+)?(?:\.(\d+))?([disfeExXobc%])")]
    private static partial Regex FormatSpecRegex();

    public static string format(string fmt, params object?[] args)
    {
        int ai = 0;
        return FormatSpecRegex().Replace(fmt, m =>
        {
            char conv = m.Groups[4].Value[0];
            if (conv == '%') return "%";
            string flags = m.Groups[1].Value;
            int width = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, Inv) : 0;
            int? prec = m.Groups[3].Success ? int.Parse(m.Groups[3].Value, Inv) : null;
            object? a = ai < args.Length ? args[ai++] : throw new FormatException("too few arguments for format");
            string body = conv switch
            {
                'd' or 'i' => Convert.ToInt64(a, Inv).ToString(Inv),
                'f' => Convert.ToDouble(a, Inv).ToString("F" + (prec ?? 6), Inv),
                'e' or 'E' => Convert.ToDouble(a, Inv).ToString((conv == 'e' ? "e" : "E") + (prec ?? 6), Inv),
                'x' => Convert.ToInt64(a, Inv).ToString("x", Inv),
                'X' => Convert.ToInt64(a, Inv).ToString("X", Inv),
                'o' => Convert.ToString(Convert.ToInt64(a, Inv), 8),
                'b' => Convert.ToString(Convert.ToInt64(a, Inv), 2),
                'c' => a is char ch ? ch.ToString() : ((char)Convert.ToInt32(a, Inv)).ToString(),
                _ => prec is int p && str(a).Length > p ? str(a)[..p] : str(a),
            };
            bool numeric = conv is not ('s' or 'c');
            if (numeric && flags.Contains('+') && !body.StartsWith('-')) body = "+" + body;
            else if (numeric && flags.Contains(' ') && !body.StartsWith('-')) body = " " + body;
            if (body.Length >= width) return body;
            if (flags.Contains('-')) return body.PadRight(width);
            if (numeric && flags.Contains('0'))
            {
                int sign = body.Length > 0 && body[0] is '-' or '+' or ' ' ? 1 : 0;
                return body[..sign] + new string('0', width - body.Length) + body[sign..];
            }
            return body.PadLeft(width);
        });
    }
}
