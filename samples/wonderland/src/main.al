# Text adventure: alice run            (interactive)
#                 alice run -- east "take key" west down north   (scripted)
require "./wonderland"

game = Game.new(wonderland)
puts game.look
if ARGV.size > 0
  ARGV.each do |cmd|
    puts "\n> #{cmd}"
    puts game.command(cmd)
  end
else
  while !game.won
    print "\n> "
    line = gets
    break if line.nil?
    puts game.command(line.not_nil!)
  end
end
puts "\n🫖 You made it to the tea party in #{game.moves} moves. Curiouser and curiouser!" if game.won
