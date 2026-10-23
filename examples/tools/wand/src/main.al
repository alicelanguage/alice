# A tiny generator in the spirit of `rails generate`: wand <kind> <name> prints the file it would create.
# Package it with `alice pack examples/tools/wand`, install with `dotnet tool install -g --add-source bin/package wand`.
def template(kind : String, name : String) : String?
  klass = name.split("_").map { |w| w.capitalize }.join
  case kind
  when "model" then "class #{klass}\n  getter id : Int64\n  def initialize(@id : Int64)\n  end\nend\n"
  when "controller" then "module #{klass}Controller\n  def self.index : String\n    \"#{name} index\"\n  end\nend\n"
  else nil
  end
end

if ARGV.size < 2
  puts "usage: wand <model|controller> <name>"
else
  body = template(ARGV[0], ARGV[1])
  if body
    puts "# app/#{ARGV[0]}s/#{ARGV[1]}.al"
    print body
  else
    Console.Error.WriteLine("unknown generator: #{ARGV[0]}")
  end
end
