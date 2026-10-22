require "../src/timeline"

describe "Wonderland film timeline" do
  it "exports exactly thirty seconds at thirty frames per second" do
    expect(Film.frames(30)).to eq(900)
    expect(Film.time(899, 30) < 30.0).to be_true
  end
  it "visits the garden, tea party and rabbit hole in order" do
    expect(Film.chapter(0.0)).to eq(0)
    expect(Film.chapter(9.99)).to eq(0)
    expect(Film.chapter(10.0)).to eq(1)
    expect(Film.chapter(20.0)).to eq(2)
    expect(Film.chapter(29.99)).to eq(2)
  end
  it "eases camera movement with exact endpoints" do
    expect(Film.ease(0.0)).to eq(0.0)
    expect(Film.ease(1.0)).to eq(1.0)
    expect(Film.ease(0.5)).to eq(0.5)
  end
end
