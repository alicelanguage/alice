# Data race: shared mutable access inside parallel block
def main
  total = 0
  parallel do
    for i in 0...1000
      total += i  # ERROR: write to shared mutable var
    end
  end
  puts total
end
