# A GPU kernel written in Alice, compiled by ILGPU (CUDA, OpenCL or its CPU accelerator). The kernel is an ordinary
# module function; `->Kernels.saxpy` passes the method itself, which is what ILGPU compiles. Run:
#   alice run examples/gpu/ilgpu          (uses a GPU when one is present, else the CPU accelerator)
using ILGPU
using ILGPU.Runtime

module Kernels
  # y[i] = a * x[i] + y[i] for every index of the launch, one GPU thread each
  def self.saxpy(i : Index1D, a : Float32, x : ArrayView1D<Float32, Stride1D::Dense>, y : ArrayView1D<Float32, Stride1D::Dense>)
    y[i] = a * x[i] + y[i]
  end
end

using ctx = Context.CreateDefault
  using acc = ctx.GetPreferredDevice(false).CreateAccelerator(ctx)
    n = 1 << 20
    xs = Array(Float32).new(n) { |k| k.to_f32 }
    ys = Array(Float32).new(n, 1.0_f32)
    using dx = acc.Allocate1D(xs)
      using dy = acc.Allocate1D(ys)
        kernel = acc.LoadAutoGroupedStreamKernel<Index1D, Float32, ArrayView1D<Float32, Stride1D::Dense>, ArrayView1D<Float32, Stride1D::Dense>>(->Kernels.saxpy)
        kernel.Invoke(dx.IntExtent, 2.0_f32, dx.View, dy.View)
        acc.Synchronize
        out = dy.GetAsArray1D
        puts "device: #{acc.AcceleratorType}"
        puts "y[0]=#{out[0]} y[10]=#{out[10]} y[1023]=#{out[1023]}"
      end
    end
  end
end

# The same computation without writing a kernel: `gpu for` turns the loop body into one (arrays of numbers and
# number scalars only; each iteration writes its own element). Arrays are uploaded, and the written ones copied back.
def saxpy(a : Float32, xs : Array(Float32), ys : Array(Float32))
  gpu for i in 0...xs.size
    ys[i] = a * xs[i] + ys[i]
  end
end

xs2 = Array(Float32).new(4096) { |k| k.to_f32 }
ys2 = Array(Float32).new(4096, 1.0_f32)
saxpy(2.0_f32, xs2, ys2)
puts "gpu for: y[0]=#{ys2[0]} y[10]=#{ys2[10]} y[4095]=#{ys2[4095]}"

# A reduction: `t += ...` from every GPU thread becomes an atomic add into one device value.
def total(xs : Array(Int64)) : Int64
  t = 0_i64
  gpu for i in 0...xs.size
    t += xs[i] * 2_i64
  end
  t
end

puts "gpu sum: #{total(Array(Int64).new(100_000) { |k| k.to_i64 })}"
