using System.Buffers;
using System.Globalization;
using System.Text;
using Alice.Runtime;

namespace Alice.Core.Text;

/// <summary>
/// Ruby `format` / `sprintf` / `"%05d" % x` without allocation: the specification is interpreted while writing into a
/// caller span (<see cref="TryFormat"/>, <see cref="TryFormatUtf8"/>); <see cref="Sprintf"/> allocates only the result
/// string. Arguments are <see cref="FormatArg"/> (no boxing). Invariant culture.
/// </summary>
/// <remarks>
/// Directives: <c>%d %i %u</c> (integers; a float is truncated, a string parsed), <c>%f</c>, <c>%e %E</c> (two-digit
/// minimum exponent, as Ruby), <c>%g %G</c>, <c>%x %X %o %b %B</c> (a negative value prints as 64-bit two's complement
/// unless a <c>+</c> or space flag asks for a sign), <c>%s</c>, <c>%p</c> (inspect), <c>%c</c>, <c>%%</c>. Flags
/// <c>- + space 0 #</c>, width and precision (digits or <c>*</c>). Infinities print as <c>Inf</c>, NaN as <c>NaN</c>.
/// An unknown directive is copied as written; too few arguments throw <see cref="FormatException"/>.
/// Named references (<c>%&lt;name&gt;s</c>, <c>%{name}</c>) are not supported yet.
/// </remarks>
public static class Format
{
    static CultureInfo Inv => CultureInfo.InvariantCulture;

    /// <summary>Formats into a new string (the only allocation).</summary>
    [Pure]
    public static string Sprintf(string format, params ReadOnlySpan<FormatArg> args)
    {
        Span<char> stack = stackalloc char[512];
        if (TryFormat(stack, out int n, format, args)) return new string(stack[..n]);
        for (int size = 2048; ; size *= 2)
        {
            var buf = ArrayPool<char>.Shared.Rent(size);
            try { if (TryFormat(buf, out n, format, args)) return new string(buf, 0, n); }
            finally { ArrayPool<char>.Shared.Return(buf); }
            if (size > 1 << 28) throw new FormatException("formatted text too long");
        }
    }

    /// <summary>Formats into <paramref name="destination"/>; false (and <paramref name="charsWritten"/> 0) when it does
    /// not fit. 0 bytes allocated for numeric, char, Boolean and string arguments.</summary>
    public static bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, params ReadOnlySpan<FormatArg> args)
    {
        var w = new Sink(destination);
        int ai = 0;
        for (int i = 0; i < format.Length; i++)
        {
            char c = format[i];
            if (c != '%' || i + 1 >= format.Length) { w.Put(c); continue; }
            if (format[i + 1] == '%') { w.Put('%'); i++; continue; }
            int j = i + 1;
            Spec sp = default;
            for (; j < format.Length; j++)
            {
                char f = format[j];
                if (f == '-') sp.Left = true;
                else if (f == '+') sp.Plus = true;
                else if (f == ' ') sp.Space = true;
                else if (f == '0') sp.Zero = true;
                else if (f == '#') sp.Alt = true;
                else break;
            }
            if (j < format.Length && format[j] == '*')
            {
                sp.Width = (int)IntOf(Next(args, ref ai));
                if (sp.Width < 0) { sp.Left = true; sp.Width = -sp.Width; }
                j++;
            }
            else while (j < format.Length && char.IsAsciiDigit(format[j])) sp.Width = sp.Width * 10 + (format[j++] - '0');
            sp.Prec = -1;
            if (j < format.Length && format[j] == '.')
            {
                j++;
                sp.Prec = 0;
                if (j < format.Length && format[j] == '*') { sp.Prec = Math.Max(0, (int)IntOf(Next(args, ref ai))); j++; }
                else while (j < format.Length && char.IsAsciiDigit(format[j])) sp.Prec = sp.Prec * 10 + (format[j++] - '0');
            }
            if (j >= format.Length) { w.Put(format[i..]); break; }
            sp.Conv = format[j];
            switch (sp.Conv)
            {
                case 'd' or 'i' or 'u': Integer(ref w, sp, Next(args, ref ai), 10, false); break;
                case 'x': Integer(ref w, sp, Next(args, ref ai), 16, false); break;
                case 'X': Integer(ref w, sp, Next(args, ref ai), 16, true); break;
                case 'o': Integer(ref w, sp, Next(args, ref ai), 8, false); break;
                case 'b' or 'B': Integer(ref w, sp, Next(args, ref ai), 2, sp.Conv == 'B'); break;
                case 'f' or 'e' or 'E' or 'g' or 'G': Float(ref w, sp, FloatOf(Next(args, ref ai))); break;
                case 's' or 'p': Text(ref w, sp, Next(args, ref ai), inspect: sp.Conv == 'p'); break;
                case 'c': Char(ref w, sp, Next(args, ref ai)); break;
                default: w.Put(format[i..(j + 1)]); break;   // unknown directive: kept as written
            }
            i = j;
        }
        charsWritten = w.Overflow ? 0 : w.Pos;
        return !w.Overflow;
    }

    /// <summary>Formats as UTF-8 into <paramref name="destination"/> (0 bytes allocated for texts up to 1024 chars,
    /// pooled scratch above).</summary>
    public static bool TryFormatUtf8(Span<byte> destination, out int bytesWritten, ReadOnlySpan<char> format, params ReadOnlySpan<FormatArg> args)
    {
        Span<char> stack = stackalloc char[1024];
        if (TryFormat(stack, out int n, format, args)) return Encoding.UTF8.TryGetBytes(stack[..n], destination, out bytesWritten);
        var buf = ArrayPool<char>.Shared.Rent(Math.Max(4096, destination.Length));
        try
        {
            if (TryFormat(buf, out n, format, args)) return Encoding.UTF8.TryGetBytes(buf.AsSpan(0, n), destination, out bytesWritten);
            bytesWritten = 0;
            return false;
        }
        finally { ArrayPool<char>.Shared.Return(buf); }
    }

    /// <summary>Appends the formatted text to <paramref name="sb"/>.</summary>
    public static StringBuilder Append(StringBuilder sb, string format, params ReadOnlySpan<FormatArg> args)
    {
        Span<char> stack = stackalloc char[512];
        if (TryFormat(stack, out int n, format, args)) return sb.Append(stack[..n]);
        return sb.Append(Sprintf(format, args));
    }

    struct Spec
    {
        public bool Left, Plus, Space, Zero, Alt;
        public int Width, Prec;
        public char Conv;
    }

    ref struct Sink(Span<char> d)
    {
        readonly Span<char> d = d;
        public int Pos;
        public bool Overflow;

        public void Put(char c)
        {
            if (Pos < d.Length) d[Pos++] = c; else Overflow = true;
        }

        public void Put(scoped ReadOnlySpan<char> s)
        {
            if (s.TryCopyTo(d[Pos..])) Pos += s.Length; else Overflow = true;
        }

        public void Fill(char c, int n)
        {
            if (n <= 0) return;
            if (Pos + n > d.Length) { Overflow = true; return; }
            d.Slice(Pos, n).Fill(c);
            Pos += n;
        }
    }

    static FormatArg Next(ReadOnlySpan<FormatArg> args, ref int ai)
        => ai < args.Length ? args[ai++] : throw new FormatException("too few arguments for format");

    static long IntOf(FormatArg a) => a.Kind switch
    {
        FormatArgKind.Int => a.Int,
        FormatArgKind.UInt => (long)a.UInt,
        FormatArgKind.Float => double.IsFinite(a.Float) ? (long)Math.Truncate(a.Float) : throw new FormatException("cannot format a non-finite float as an integer"),
        FormatArgKind.Char => a.Char,
        FormatArgKind.String => long.Parse(a.Str!.Replace("_", ""), NumberStyles.Integer, Inv),
        FormatArgKind.Utf8 => long.Parse(a.Utf8Bytes!, NumberStyles.Integer, Inv),
        FormatArgKind.Null => throw new FormatException("can't convert nil into Integer"),
        FormatArgKind.Bool => throw new FormatException("can't convert a Boolean into Integer"),
        _ => Convert.ToInt64(a.Obj, Inv),
    };

    static double FloatOf(FormatArg a) => a.Kind switch
    {
        FormatArgKind.Float => a.Float,
        FormatArgKind.Int => a.Int,
        FormatArgKind.UInt => a.UInt,
        FormatArgKind.Char => a.Char,
        FormatArgKind.String => double.Parse(a.Str!.Replace("_", ""), NumberStyles.Float, Inv),
        FormatArgKind.Utf8 => double.Parse(a.Utf8Bytes!, NumberStyles.Float, Inv),
        FormatArgKind.Null => throw new FormatException("can't convert nil into Float"),
        FormatArgKind.Bool => throw new FormatException("can't convert a Boolean into Float"),
        _ => Convert.ToDouble(a.Obj, Inv),
    };

    static void Integer(ref Sink w, Spec sp, FormatArg a, int radix, bool upper)
    {
        Span<char> digits = stackalloc char[66];
        bool neg;
        ulong mag;
        if (a.Kind == FormatArgKind.UInt) { neg = false; mag = a.UInt; }
        else
        {
            long v = IntOf(a);
            neg = v < 0;
            // Ruby prints a sign for radix conversions only on request; otherwise two's complement (64-bit)
            bool signed = radix == 10 || sp.Plus || sp.Space;
            mag = neg && signed ? (ulong)(-(v + 1)) + 1 : (ulong)v;
            if (!signed) neg = false;
        }
        int n = Digits(mag, radix, upper, digits);
        ReadOnlySpan<char> body = digits[(66 - n)..];
        ReadOnlySpan<char> prefix = !sp.Alt || mag == 0 ? default : radix switch
        {
            16 => upper ? "0X" : "0x",
            8 => "0",
            2 => sp.Conv == 'B' ? "0B" : "0b",
            _ => default,
        };
        char sign = neg ? '-' : sp.Plus ? '+' : sp.Space ? ' ' : '\0';
        int zeros = sp.Prec >= 0 ? Math.Max(0, sp.Prec - body.Length) : 0;
        if (sp.Prec == 0 && mag == 0) body = default;
        int len = (sign != '\0' ? 1 : 0) + prefix.Length + zeros + body.Length;
        if (sp.Prec < 0 && sp.Zero && !sp.Left && sp.Width > len) { zeros += sp.Width - len; len = sp.Width; }
        Emit(ref w, sp, len, sign, prefix, zeros, body);
    }

    static int Digits(ulong v, int radix, bool upper, Span<char> into)
    {
        int p = into.Length;
        if (v == 0) { into[--p] = '0'; return 1; }
        if (radix == 10)
        {
            v.TryFormat(into, out int k, default, Inv);
            into[..k].CopyTo(into[(into.Length - k)..]);
            return k;
        }
        int shift = radix == 16 ? 4 : radix == 8 ? 3 : 1;
        ulong mask = (ulong)radix - 1;
        while (v != 0)
        {
            int d = (int)(v & mask);
            into[--p] = (char)(d < 10 ? '0' + d : (upper ? 'A' : 'a') + d - 10);
            v >>= shift;
        }
        return into.Length - p;
    }

    static void Emit(ref Sink w, Spec sp, int len, char sign, scoped ReadOnlySpan<char> prefix, int zeros, scoped ReadOnlySpan<char> body)
    {
        int pad = sp.Width - len;
        if (!sp.Left) w.Fill(' ', pad);
        if (sign != '\0') w.Put(sign);
        w.Put(prefix);
        w.Fill('0', zeros);
        w.Put(body);
        if (sp.Left) w.Fill(' ', pad);
    }

    static void Float(ref Sink w, Spec sp, double v)
    {
        bool neg = double.IsNegative(v) && !double.IsNaN(v);
        char sign = neg ? '-' : sp.Plus ? '+' : sp.Space ? ' ' : '\0';
        if (!double.IsFinite(v))
        {
            ReadOnlySpan<char> t = double.IsNaN(v) ? "NaN" : "Inf";
            Emit(ref w, sp, t.Length + (sign != '\0' ? 1 : 0), sign, default, 0, t);
            return;
        }
        double a = Math.Abs(v);
        Span<char> buf = stackalloc char[400];
        int prec = sp.Prec < 0 ? 6 : sp.Prec;
        int n;
        switch (sp.Conv)
        {
            case 'f': n = Fixed(a, prec, buf, sp.Alt); break;
            case 'e' or 'E': n = Exp(a, prec, buf, sp.Conv == 'E', sp.Alt); break;
            default:
                {
                    int p = prec == 0 ? 1 : prec;
                    int x = a == 0 ? 0 : ExponentOf(a, p);
                    n = p > x && x >= -4 ? Fixed(a, p - 1 - x, buf, sp.Alt) : Exp(a, p - 1, buf, sp.Conv == 'G', sp.Alt);
                    if (!sp.Alt) n = StripZeros(buf, n);
                    break;
                }
        }
        ReadOnlySpan<char> body = buf[..n];
        int len = (sign != '\0' ? 1 : 0) + n, zeros = 0;
        if (sp.Zero && !sp.Left && sp.Width > len) { zeros = sp.Width - len; len = sp.Width; }
        Emit(ref w, sp, len, sign, default, zeros, body);
    }

    static int Fixed(double a, int prec, Span<char> buf, bool alt)
    {
        Span<char> f = stackalloc char[4];
        if (!a.TryFormat(buf, out int n, NumFormat('F', prec, f), Inv)) throw new FormatException("precision too large");
        if (alt && prec == 0) buf[n++] = '.';
        return n;
    }

    static int Exp(double a, int prec, Span<char> buf, bool upper, bool alt)
    {
        Span<char> f = stackalloc char[4];
        if (!a.TryFormat(buf, out int n, NumFormat(upper ? 'E' : 'e', prec, f), Inv)) throw new FormatException("precision too large");
        // .NET writes e+006; Ruby (C) writes at least two exponent digits: e+06
        int e = buf[..n].IndexOfAny('e', 'E');
        if (alt && prec == 0) { buf[e..n].CopyTo(buf[(e + 1)..]); buf[e] = '.'; e++; n++; }
        int d = e + 2;
        int lead = 0;
        while (d + lead < n - 2 && buf[d + lead] == '0') lead++;
        if (lead > 0) { buf[(d + lead)..n].CopyTo(buf[d..]); n -= lead; }
        return n;
    }

    static int ExponentOf(double a, int p)
    {
        Span<char> buf = stackalloc char[40];
        Span<char> f = stackalloc char[4];
        a.TryFormat(buf, out int n, NumFormat('E', p - 1, f), Inv);
        int e = buf[..n].IndexOf('E');
        return int.Parse(buf[(e + 1)..n], NumberStyles.AllowLeadingSign, Inv);
    }

    static int StripZeros(Span<char> buf, int n)
    {
        int dot = buf[..n].IndexOf('.');
        if (dot < 0) return n;
        int e = buf[..n].IndexOfAny('e', 'E');
        int end = e < 0 ? n : e, k = end;
        while (k > dot + 1 && buf[k - 1] == '0') k--;
        if (k == dot + 1) k = dot;
        if (k == end) return n;
        buf[end..n].CopyTo(buf[k..]);
        return n - (end - k);
    }

    static ReadOnlySpan<char> NumFormat(char kind, int prec, Span<char> f)
    {
        if (prec > 99) throw new FormatException("precision above 99 is not supported");
        f[0] = kind;
        if (prec < 10) { f[1] = (char)('0' + prec); return f[..2]; }
        f[1] = (char)('0' + prec / 10);
        f[2] = (char)('0' + prec % 10);
        return f[..3];
    }

    static void Text(ref Sink w, Spec sp, FormatArg a, bool inspect)
    {
        Span<char> num = stackalloc char[32];
        scoped ReadOnlySpan<char> s;
        byte[]? utf8 = null;
        switch (a.Kind)
        {
            case FormatArgKind.String when !inspect: s = a.Str; break;
            case FormatArgKind.Int when a.Int.TryFormat(num, out int k, default, Inv): s = num[..k]; break;
            case FormatArgKind.UInt when a.UInt.TryFormat(num, out int k2, default, Inv): s = num[..k2]; break;
            case FormatArgKind.Char when !inspect: num[0] = a.Char; s = num[..1]; break;
            case FormatArgKind.Bool: s = a.Bool ? "true" : "false"; break;
            case FormatArgKind.Null: s = inspect ? "nil" : ""; break;
            case FormatArgKind.Utf8 when !inspect: utf8 = a.Utf8Bytes; s = default; break;
            case FormatArgKind.Float: s = Prelude.str(a.Float); break;
            default:
                s = inspect ? Prelude.inspect<object?>(a.Kind switch
                {
                    FormatArgKind.String => a.Str,
                    FormatArgKind.Char => a.Char.ToString(),
                    FormatArgKind.Utf8 => Encoding.UTF8.GetString(a.Utf8Bytes!),
                    _ => a.Obj,
                }) : Prelude.str(a.Obj);
                break;
        }
        if (utf8 is not null)
        {
            char[]? rented = null;
            Span<char> tmp = utf8.Length <= 256 ? stackalloc char[256] : (rented = ArrayPool<char>.Shared.Rent(utf8.Length));
            int k = Encoding.UTF8.GetChars(utf8, tmp);
            TextBody(ref w, sp, tmp[..k]);
            if (rented != null) ArrayPool<char>.Shared.Return(rented);
            return;
        }
        TextBody(ref w, sp, s);
    }

    static void TextBody(ref Sink w, Spec sp, scoped ReadOnlySpan<char> s)
    {
        if (sp.Prec >= 0 && s.Length > sp.Prec) s = s[..sp.Prec];
        Emit(ref w, sp, s.Length, '\0', default, 0, s);
    }

    static void Char(ref Sink w, Spec sp, FormatArg a)
    {
        Span<char> c = stackalloc char[2];
        int n;
        switch (a.Kind)
        {
            case FormatArgKind.Char: c[0] = a.Char; n = 1; break;
            case FormatArgKind.String when a.Str!.Length > 0: n = char.IsHighSurrogate(a.Str[0]) && a.Str.Length > 1 ? 2 : 1; a.Str.AsSpan(0, n).CopyTo(c); break;
            default: n = new Rune(checked((int)IntOf(a))).EncodeToUtf16(c); break;
        }
        Emit(ref w, sp, n, '\0', default, 0, c[..n]);
    }
}
