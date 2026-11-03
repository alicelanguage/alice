# alice-grep: search files for a regular expression, one file per core.
#   alice run -- -n 'def \w+' 'src/**/*.al'
# Exit status as grep: 0 = a line matched, 1 = no match, 2 = usage or pattern error.
require "./glob"
require "./options"
require "./search"

def main(args : Array(String)) : Int32
  parsed = Cli.parse(args)
  case parsed
  when Parsed::Help
    puts Cli::USAGE
    return 0
  when Parsed::Usage(message)
    STDERR.puts "alice-grep: #{message}"
    STDERR.puts Cli::USAGE
    return 2
  when Parsed::Run(o)
    re = Search.regex(o)
    files = List(String).new
    for path in o.paths
      found = Glob.expand(path)
      STDERR.puts "alice-grep: #{path}: no such file or directory" if found.size == 0
      files.AddRange(found)
    end
    many = files.Count > 1 || o.paths.any? { |p| !File.Exists(p) }
    matched = false
    for result in Search.run(files.to_a, re)
      matched = true if result.hits.size > 0
      Search.format(result, o, many).each { |line| puts line }
    end
    return matched ? 0 : 1
  end
  2
end

begin
  exit(main(ARGV))
rescue e : ArgumentException
  STDERR.puts "alice-grep: bad pattern: #{e.Message}"
  exit(2)
rescue e : RegexMatchTimeoutException
  STDERR.puts "alice-grep: pattern took too long on one line"
  exit(2)
end
