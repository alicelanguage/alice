# Shell-style globs without a package: `*` any characters but `/`, `**` any directories, `?` one character,
# `[abc]` a set. `expand` walks from the part of the pattern before the first wildcard.
using System.IO
using System.Text
using System.Text.RegularExpressions

module Glob
  def self.wildcard?(text : String) : Bool
    text.includes?("*") || text.includes?("?") || text.includes?("[")
  end

  # The glob as an anchored regular expression over '/'-separated relative paths.
  def self.to_regex(glob : String) : String
    sb = StringBuilder.new("^")
    i = 0
    while i < glob.size
      c = glob[i]
      if c == '*' && i + 1 < glob.size && glob[i + 1] == '*'
        i += 1
        if i + 1 < glob.size && glob[i + 1] == '/'
          i += 1
          sb.Append("(?:.*/)?")      # `**/` matches zero or more directories
        else
          sb.Append(".*")
        end
      elsif c == '*'
        sb.Append("[^/]*")
      elsif c == '?'
        sb.Append("[^/]")
      elsif c == '['
        close = glob.IndexOf(']', i + 1)
        if close < 0
          sb.Append("\\[")
        else
          sb.Append("[").Append(glob.Substring(i + 1, close - i - 1).Replace("\\", "\\\\")).Append("]")
          i = close
        end
      else
        sb.Append(Regex.Escape(c.to_s))
      end
      i += 1
    end
    sb.Append("$").ToString()
  end

  def self.matches?(glob : String, path : String) : Bool
    Regex.IsMatch(path.Replace('\\', '/'), to_regex(glob))
  end

  # Files named by one command-line argument: a file, a directory (every file below it), or a glob.
  def self.expand(arg : String) : Array(String)
    if File.Exists(arg)
      return [arg]
    end
    if Directory.Exists(arg)
      return Glob.walk(arg).sort
    end
    return Array(String).new(0) if !Glob.wildcard?(arg)
    pattern = arg.Replace('\\', '/')
    first = pattern.IndexOfAny(['*', '?', '['])
    slash = pattern.LastIndexOf('/', first)          # the directory part ends before the first wildcard
    root = slash < 0 ? "." : (slash == 0 ? "/" : pattern.Substring(0, slash))
    return Array(String).new(0) if !Directory.Exists(root)
    re = Regex.new(Glob.to_regex(pattern.Substring(slash + 1)))
    found = List(String).new
    for file in Glob.walk(root)
      relative = Path.GetRelativePath(root, file).Replace('\\', '/')
      found.Add(slash < 0 ? relative : file) if re.IsMatch(relative)
    end
    found.to_a.sort
  end

  # Every file below `dir`, skipping hidden directories such as .git.
  def self.walk(dir : String) : Array(String)
    found = List(String).new
    pending = Stack(String).new
    pending.Push(dir)
    while pending.Count > 0
      current = pending.Pop()
      for file in Directory.GetFiles(current)
        found.Add(file.Replace('\\', '/'))
      end
      for sub in Directory.GetDirectories(current)
        pending.Push(sub) if !Path.GetFileName(sub).starts_with?(".")
      end
    end
    found.to_a
  end
end
