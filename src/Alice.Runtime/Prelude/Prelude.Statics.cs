using System.Text;

namespace Alice.Runtime;

/// <summary>
/// Class-level methods on enums and String (`Color.values`, `Color.parse("Red")`, `String.build { |io| }`) as C# 14
/// static extension members: the generated `Color.parse(...)` binds here, no compiler lowering needed.
/// </summary>
public static class StaticExtensions
{
    extension<T>(T) where T : struct, Enum
    {
        // properties: Alice writes `Color.values` without parentheses
        /// <summary>`Color.values`: every member in value order.</summary>
        public static T[] values => Enum.GetValues<T>();

        /// <summary>`Color.names`: the member names in value order.</summary>
        public static string[] names => Enum.GetNames<T>();

        /// <summary>`Color.parse("red")`: by name, case-insensitive; ArgumentException when unknown.</summary>
        public static T parse(string name) => Prelude.enum_parse<T>(name);

        /// <summary>`Color.parse?("x")`: nil when unknown.</summary>
        public static T? parse_q(string name) => Prelude.enum_parse_q<T>(name);

        /// <summary>`Color.from_value(2)`: the member with that value; ArgumentException when none.</summary>
        public static T from_value(long value) => Prelude.enum_from_value<T>(value);

        /// <summary>`Color.from_value?(9)`: nil when no member has that value.</summary>
        public static T? from_value_q(long value) => Prelude.enum_from_value_q<T>(value);
    }

    extension(string)
    {
        /// <summary>`String.build { |io| io << "a" << 1 }`: the builder's text.</summary>
        public static string build(Action<StringBuilder> f) => Prelude.string_build(f);
    }
}
