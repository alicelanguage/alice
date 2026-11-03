# The scan: files are searched in parallel, each iteration writing only its own result slot.
#
# `Par.For`, not `par for`: `par for` is for loops the compiler proves independent, and that proof allows only
# pure calls. Reading a file is I/O, and Regex/String methods are not marked pure, so `par for` rejects this loop
# (AL0209). `Par.For` runs it on all cores and still checks races (AL0202): writing `results[i]` at the loop
# index is allowed, writing a shared variable would not compile.
using System.IO
using System.Text.RegularExpressions

record Hit(line : Int32, text : String)
record FileResult(path : String, hits : Array(Hit), skipped : Bool)

module Search
  def self.regex(o : Options) : Regex
    source = o.fixed ? Regex.Escape(o.pattern) : o.pattern
    flags = o.ignore_case ? RegexOptions.IgnoreCase : RegexOptions.None
    Regex.new(source, flags | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2))   # bounded: no runaway patterns
  end

  # A file whose first 8000 bytes hold a NUL byte is treated as binary and skipped, as grep does.
  def self.binary?(path : String) : Bool
    buffer = Array(UInt8).new(8000)
    n = 0
    using stream = File.OpenRead(path)
      n = stream.Read(buffer, 0, buffer.size)
    end
    Array.IndexOf(buffer, 0_u8, 0, n) >= 0
  end

  def self.scan(path : String, re : Regex) : FileResult
    begin
      return FileResult.new(path, Array(Hit).new(0), true) if Search.binary?(path)
      hits = List(Hit).new
      number = 0
      for line in File.ReadLines(path)
        number += 1
        hits.Add(Hit.new(number, line)) if re.IsMatch(line)
      end
      FileResult.new(path, hits.to_a, false)
    rescue e : IOException
      FileResult.new(path, Array(Hit).new(0), true)
    rescue e : UnauthorizedAccessException
      FileResult.new(path, Array(Hit).new(0), true)
    end
  end

  # Results come back in the order of `files`, whatever order the cores finish in.
  def self.run(files : Array(String), re : Regex) : Array(FileResult)
    results = Array(FileResult).new(files.size)
    Par.For(files.size) { |i| results[i] = Search.scan(files[i], re) }
    results
  end

  def self.format(result : FileResult, o : Options, many : Bool) : Array(String)
    return Array(String).new(0) if result.skipped
    return (result.hits.size > 0 ? [result.path] : Array(String).new(0)) if o.files_only
    return ["#{many ? result.path + ":" : ""}#{result.hits.size}"] if o.count
    result.hits.map do |h|
      prefix = many ? "#{result.path}:" : ""
      number = o.line_numbers ? "#{h.line}:" : ""
      "#{prefix}#{number}#{h.text}"
    end
  end
end
