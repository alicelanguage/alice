# Fixed: use parallel reduce instead of shared mutable
def main
  total = (0...1000).parallel.sum
  puts "total = #{total}"
end
