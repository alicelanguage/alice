require "../src/life"

describe "Life" do
  it "keeps a block still" do
    life = Life.parse(["....", ".##.", ".##.", "...."])
    life.step
    expect(life.population).to eq(4)
    expect(life.alive?(1, 1)).to be_true
  end

  it "flips a blinker" do
    life = Life.parse([".....", "..#..", "..#..", "..#..", "....."])
    life.step
    expect(life.alive?(1, 2) && life.alive?(2, 2) && life.alive?(3, 2)).to be_true
    expect(life.alive?(2, 1)).to be_false
    life.step
    expect(life.alive?(2, 1)).to be_true
  end

  it "wraps around the edges" do
    life = Life.parse(["#...", "....", "....", "...."])
    expect(life.alive?(-4, 4)).to be_true
    expect(life.neighbours(3, 3)).to eq(1)
  end

  it "moves a glider one cell diagonally every 4 generations" do
    life = Life.parse([".#......", "..#.....", "###.....", "........", "........", "........"])
    4.times { life.step }
    expect(life.population).to eq(5)
    expect(life.alive?(3, 2)).to be_true
    expect(life.generation).to eq(4)
  end
end
