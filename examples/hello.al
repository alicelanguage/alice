using Alice.Runtime

class Greeter
  getter name : String
  def initialize(@name : String)
  end

  def greet : String
    "Hello, #{name}!"
  end
end

puts Greeter.new("Alice").greet
xs = [1.0, 2.0, 3.0, 4.0]
puts "simd dot = #{Simd.Dot(xs, xs)}"
squares = (1..10).to_a.par_map { |i| i * i }
puts "squares = #{squares}"
