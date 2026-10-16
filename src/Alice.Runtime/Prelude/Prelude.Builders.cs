using System.Collections;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Alice.Runtime;

public static partial class Prelude
{
    public static StringBuilder append(this StringBuilder sb, string s) => sb.Append(s);
}
