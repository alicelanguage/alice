require "../src/render"

describe "Vec" do
  it "does vector math" do
    v = Vec.new(1.0, 2.0, 2.0)
    expect(v.length).to eq(3.0)
    expect((v + Vec.new(1.0, 0.0, 0.0)).x).to eq(2.0)
    expect(v.dot(Vec.new(0.0, 1.0, 0.0))).to eq(2.0)
    expect(v.unit.length).to be_within(1e-12).of(1.0)
  end

  it "reflects off a surface" do
    r = Vec.new(1.0, -1.0, 0.0).reflect(Vec.new(0.0, 1.0, 0.0))
    expect(r.y).to eq(1.0)
    expect(r.x).to eq(1.0)
  end
end

describe "Sphere" do
  s = Sphere.new(Vec.new(0.0, 0.0, 5.0), 1.0, IVORY, 0.0)

  it "is hit in front at the near surface" do
    expect(s.hit(Ray.new(Vec.new(0.0, 0.0, 0.0), Vec.new(0.0, 0.0, 1.0)))).to be_within(1e-9).of(4.0)
  end

  it "is missed by rays that pass by" do
    expect(s.hit(Ray.new(Vec.new(0.0, 2.0, 0.0), Vec.new(0.0, 0.0, 1.0)))).to eq(-1.0)
  end
end

describe "Render" do
  it "is deterministic and fills the frame" do
    cam = Camera.new(Vec.new(0.0, 2.4, -6.5), Vec.new(0.0, 0.8, 0.0))
    a = Render.image(Scene.wonderland, cam, 64, 36)
    b = Render.image(Scene.wonderland, cam, 64, 36)
    expect(a.size).to eq(64 * 36 * 3)
    expect(a.sum { |v| v.to_i }).to eq(b.sum { |v| v.to_i })
    expect(a.sum { |v| v.to_i }).to be_gt(0)
  end
end
