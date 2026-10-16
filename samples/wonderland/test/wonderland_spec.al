require "../src/wonderland"

describe "Wonderland" do
  game = Game.new(wonderland)
  before do
    game = Game.new(wonderland)
  end

  it "starts in the rabbit hole" do
    expect(game.look).to start_with("RABBIT HOLE")
    expect(game.look).to include("watch")
  end

  it "keeps the garden locked without the key" do
    expect(game.command("down")).to include("locked")
    expect(game.here).to eq("rabbit hole")
  end

  it "wins with the key" do
    ["east", "take key", "west", "down", "north"].each { |c| game.command(c) }
    expect(game.won).to be_true
    expect(game.bag).to contain_exactly("key")
  end

  it "parses commands with case/in" do
    expect(game.command("bag")).to eq("Your bag is empty.")
    expect(game.command("take unicorn")).to eq("There is no unicorn here.")
    expect(game.command("dance wildly")).to include("Cheshire")
  end
end
