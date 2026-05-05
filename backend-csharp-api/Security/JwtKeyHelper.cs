using System.Security.Cryptography;
using System.Text;

namespace LuxusDemandas.Api.Security;

public static class JwtKeyHelper
{
    public static byte[] NormalizeSecretToKey(string? secret)
    {
        var raw = string.IsNullOrWhiteSpace(secret) ? "luxus-secret-change-me" : secret;
        var bytes = Encoding.UTF8.GetBytes(raw);
        if (bytes.Length >= 32)
        {
            return bytes;
        }

        return SHA256.HashData(bytes);
    }
}
