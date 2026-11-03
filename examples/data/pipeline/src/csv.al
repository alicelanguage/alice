# A small RFC 4180 reader: comma-separated fields, double quotes around fields that hold commas, quotes or line
# breaks, and "" for a quote inside a quoted field.
using System.Text

module Csv
  # Splits one record. Returns nil when a quoted field is not closed on this line (the caller joins the next).
  def self.fields(line : String) : Array(String)?
    fields = List(String).new
    field = StringBuilder.new
    quoted = false
    i = 0
    while i < line.size
      c = line[i]
      if quoted
        if c == '"' && i + 1 < line.size && line[i + 1] == '"'
          field.Append('"')
          i += 1
        elsif c == '"'
          quoted = false
        else
          field.Append(c)
        end
      elsif c == '"' && field.Length == 0
        quoted = true
      elsif c == ','
        fields.Add(field.ToString())
        field.Clear()
      else
        field.Append(c)
      end
      i += 1
    end
    return nil if quoted
    fields.Add(field.ToString())
    fields.to_a
  end

  # Every record of a document, the header first. Quoted line breaks stay inside their field.
  def self.records(lines : Array(String)) : Array(Array(String))
    records = List(Array(String)).new
    pending = ""
    for line in lines
      text = pending == "" ? line : pending + "\n" + line
      parsed = Csv.fields(text)
      if parsed.nil?
        pending = text
      else
        pending = ""
        records.Add(parsed.not_nil!) if text.strip.size > 0
      end
    end
    raise ArgumentException.new("unterminated quoted field at the end of the input") if pending != ""
    records.to_a
  end
end
