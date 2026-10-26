using Alice.Compiler;

namespace Alice.Tests.Runtime.Tensors;

[Collection("console")]
public class AliceBindingTests
{
    [Fact]
    public void Alice_code_calls_tensor_kernels()
    {
        var src = "using Alice.Runtime\nw = Array(Float32).new(8)\nx = Array(Float32).new(4)\ny = Array(Float32).new(2)\nNn.Fill(w, 1)\nNn.Fill(x, 2)\nNn.Gemv(w, x, y, 2, 4)\nputs y[0]\nNn.Softmax(y)\nputs y[1]\nputs Nn.Dot(x, x)";
        Assert.Equal("8.0\n0.5\n16.0", AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n"));
    }
}
