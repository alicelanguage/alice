# Send values across a channel instead of sharing a mutable accumulator.
results = Channel(Int32).new
worker = spawn do
  for number in 1..10
    results.send(number * number)
  end
  results.close
end

total = 0
results.each { |square| total += square }
worker.wait
puts "sum of squares=#{total}"
