# Command-line parsing: flags first or mixed with paths, `--` ends the flags.
record Options(pattern : String, paths : Array(String), ignore_case : Bool, line_numbers : Bool, count : Bool, files_only : Bool, fixed : Bool)

enum Parsed
  Run(options : Options)
  Help
  Usage(message : String)
end

module Cli
  USAGE : String = "usage: alice-grep [-i] [-n] [-c] [-l] [-F] PATTERN [FILE | DIR | GLOB ...]\n" +
          "  -i  ignore case     -n  line numbers     -c  count matches per file\n" +
          "  -l  file names only -F  PATTERN is plain text, not a regex\n" +
          "GLOB: * and ? within a directory, ** across directories, e.g. 'src/**/*.al' (quote it)"

  def self.parse(args : Array(String)) : Parsed
    ignore_case = false
    line_numbers = false
    count = false
    files_only = false
    fixed = false
    rest = List(String).new
    flags = true
    for arg in args
      if flags && arg == "--"
        flags = false
      elsif flags && (arg == "-h" || arg == "--help")
        return Parsed::Help
      elsif flags && arg.size > 1 && arg.starts_with?("-")
        for c in arg.chars.drop(1)
          case c
          when 'i' then ignore_case = true
          when 'n' then line_numbers = true
          when 'c' then count = true
          when 'l' then files_only = true
          when 'F' then fixed = true
          else return Parsed::Usage("unknown flag -#{c}")
          end
        end
      else
        rest.Add(arg)
      end
    end
    return Parsed::Usage("missing PATTERN") if rest.Count == 0
    paths = rest.Count == 1 ? ["."] : rest.to_a.drop(1)
    Parsed::Run(Options.new(rest[0], paths, ignore_case, line_numbers, count, files_only, fixed))
  end
end
