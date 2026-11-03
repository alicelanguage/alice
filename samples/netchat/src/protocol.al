# The chat protocol: one UTF-8 line per message, in both directions. Pure functions, tested without a socket.
enum Command
  Nick(name : String)
  Say(text : String)
  Who
  Quit
  Invalid(reason : String)
end

module Protocol
  MAX_TEXT = 400
  MAX_NAME = 16

  # A name is 1..16 ASCII letters, digits or '_'.
  def self.valid_name?(name : String) : Bool
    return false if name.size == 0 || name.size > MAX_NAME
    name.chars.all? { |c| (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' }
  end

  def self.parse(line : String) : Command
    text = line.strip
    if text.starts_with?("/")
      parts = text.Split(' ', 2)   # .NET String.Split(char, count): command and the rest
      case parts[0]
      when "/nick"
        name = parts.Length > 1 ? parts[1].strip : ""
        return valid_name?(name) ? Command::Nick(name) : Command::Invalid("names are 1-16 letters, digits or _")
      when "/who" then return Command::Who
      when "/quit" then return Command::Quit
      else return Command::Invalid("unknown command #{parts[0]}")
      end
    end
    return Command::Invalid("empty message") if text.size == 0
    Command::Say(text.size > MAX_TEXT ? text[0, MAX_TEXT] : text)
  end

  def self.joined(name : String) : String
    "* #{name} joined"
  end

  def self.left(name : String) : String
    "* #{name} left"
  end

  def self.said(name : String, text : String) : String
    "<#{name}> #{text}"
  end

  def self.online(names : Array(String)) : String
    "* online: #{names.sort.join(", ")}"
  end
end
