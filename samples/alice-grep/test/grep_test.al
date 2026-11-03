require "../src/glob"
require "../src/options"
require "../src/search"
using System.IO

# Arrays are compared joined: assert_equal compares two arrays by reference.

# A small tree in a fresh temp directory: a/one.al, a/b/two.al, notes.txt, .git/skip.al, blob.bin
def fixture : String
  root = Path.Combine(Path.GetTempPath(), "alice-grep-test-#{Guid.NewGuid()}").Replace('\\', '/')
  Directory.CreateDirectory("#{root}/a/b")
  Directory.CreateDirectory("#{root}/.git")
  File.WriteAllText("#{root}/a/one.al", "def hello\n  puts \"Hello\"\nend\n")
  File.WriteAllText("#{root}/a/b/two.al", "# nothing here\ndef bye\nend\n")
  File.WriteAllText("#{root}/notes.txt", "hello notes\n")
  File.WriteAllText("#{root}/.git/skip.al", "def hidden\n")
  File.WriteAllBytes("#{root}/blob.bin", [104_u8, 101_u8, 108_u8, 108_u8, 111_u8, 0_u8])
  root
end

def options(args : Array(String)) : Options
  case Cli.parse(args)
  when Parsed::Run(o) then o
  else raise "expected options"
  end
end

def test_glob_to_regex
  assert Glob.matches?("*.al", "main.al")
  assert !Glob.matches?("*.al", "src/main.al")
  assert Glob.matches?("**/*.al", "main.al")
  assert Glob.matches?("**/*.al", "src/deep/main.al")
  assert Glob.matches?("src/?.al", "src/a.al")
  assert Glob.matches?("[ab]*.txt", "beta.txt")
  assert !Glob.matches?("[ab]*.txt", "gamma.txt")
end

def test_expand_a_directory_skips_hidden_ones
  root = fixture
  files = Glob.expand(root).map { |f| Path.GetRelativePath(root, f).Replace('\\', '/') }
  assert_equal ["a/b/two.al", "a/one.al", "blob.bin", "notes.txt"].join("|"), (files.sort).join("|")
end

def test_expand_a_glob
  root = fixture
  assert_equal ["#{root}/a/b/two.al", "#{root}/a/one.al"].join("|"), (Glob.expand("#{root}/**/*.al")).join("|")
  assert_equal ["#{root}/a/one.al"].join("|"), (Glob.expand("#{root}/a/*.al")).join("|")
  assert_equal 0, Glob.expand("#{root}/missing/*.al").size
end

def test_parse_flags_and_defaults
  o = options(["-in", "hello", "src"])
  assert o.ignore_case
  assert o.line_numbers
  assert !o.count
  assert_equal "hello", o.pattern
  assert_equal ["src"].join("|"), (o.paths).join("|")
  assert_equal ["."].join("|"), (options(["x"]).paths).join("|")
  assert_equal "-v", options(["--", "-v"]).pattern
  assert_equal Parsed::Usage("unknown flag -z"), Cli.parse(["-z", "x"])
  assert_equal Parsed::Usage("missing PATTERN"), Cli.parse(Array(String).new(0))
  assert_equal Parsed::Help, Cli.parse(["--help"])
end

def test_search_in_parallel_keeps_file_order_and_skips_binaries
  root = fixture
  o = options(["-i", "-n", "hello", root])
  results = Search.run(Glob.expand(root), Search.regex(o))
  lines = results.flat_map { |r| Search.format(r, o, true) }.map { |l| l.Replace(root + "/", "") }
  assert_equal ["a/one.al:1:def hello", "a/one.al:2:  puts \"Hello\"", "notes.txt:1:hello notes"].join("|"), (lines).join("|")
  assert results.any? { |r| r.skipped && r.path.ends_with?("blob.bin") }
end

def test_count_files_only_and_fixed_strings
  root = fixture
  files = Glob.expand("#{root}/**/*.al")
  count = options(["-c", "def", root])
  assert_equal ["#{root}/a/b/two.al:1", "#{root}/a/one.al:1"].join("|"), (Search.run(files, Search.regex(count)).flat_map { |r| Search.format(r, count, true) }).join("|")
  only = options(["-l", "bye", root])
  assert_equal ["#{root}/a/b/two.al"].join("|"), (Search.run(files, Search.regex(only)).flat_map { |r| Search.format(r, only, true) }).join("|")
  fixed = options(["-F", "\"Hello\"", root])
  assert_equal ["  puts \"Hello\""].join("|"), (Search.run(["#{root}/a/one.al"], Search.regex(fixed)).flat_map { |r| Search.format(r, fixed, false) }).join("|")
end
