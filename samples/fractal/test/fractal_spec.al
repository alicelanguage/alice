require "../src/fractal"

describe "Fractal" do
  it "keeps the origin inside the set" do
    expect(Fractal.escape(0.0, 0.0, 100)).to eq(100.0)
  end

  it "lets far points escape quickly" do
    expect(Fractal.escape(2.0, 2.0, 100)).to be_lt(3.0)
  end

  it "renders deterministic frames" do
    a = Fractal.render(-0.5, 0.0, 0.05, 40, 30, 64)
    b = Fractal.render(-0.5, 0.0, 0.05, 40, 30, 64)
    expect(a.size).to eq(40 * 30 * 3)
    expect(a.SequenceEqual(b)).to be_true
  end

  it "maps the palette into 0..1" do
    r, g, b = Fractal.palette(0.7)
    expect([r, g, b]).to all(satisfy(->(v : Object?) { v.as(Float64) >= 0.0 && v.as(Float64) <= 1.0 }))
  end
end
