using System.Text;

namespace Alice.Runtime;

/// <summary>Ruby's `Digest::SHA256.hexdigest(s)`: lowercase hex of the UTF-8 bytes (or of a byte array).</summary>
public static class Digest
{
    public static class SHA256
    {
        public static string hexdigest(string text) => Hex(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        public static string hexdigest(byte[] bytes) => Hex(System.Security.Cryptography.SHA256.HashData(bytes));
        public static byte[] digest(string text) => System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text));
        public static byte[] digest(byte[] bytes) => System.Security.Cryptography.SHA256.HashData(bytes);
    }

    public static class SHA1
    {
        public static string hexdigest(string text) => Hex(System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(text)));
        public static string hexdigest(byte[] bytes) => Hex(System.Security.Cryptography.SHA1.HashData(bytes));
    }

    public static class MD5
    {
        public static string hexdigest(string text) => Hex(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(text)));
        public static string hexdigest(byte[] bytes) => Hex(System.Security.Cryptography.MD5.HashData(bytes));
    }

    static string Hex(byte[] hash) => Convert.ToHexStringLower(hash);
}
