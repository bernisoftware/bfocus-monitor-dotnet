using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bfocus.Monitor.Internal;

/// <summary>Assinatura v2 da identidade — a MESMA do widget (monitor/BRIEF.md §6).</summary>
internal static class Signing
{
    /// <summary>Recalcula a assinatura feita há mais de 6 dias.</summary>
    public const long MaxAgeSeconds = 6 * 24 * 3600;

    public static string UserHash(string secret, long ts, string userExternalId, string customerExternalId)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var t = ts.ToString(CultureInfo.InvariantCulture);
        var digest = hmac.ComputeHash(Encoding.UTF8.GetBytes("v2:" + t + ":" + userExternalId + ":" + customerExternalId));
        var sb = new StringBuilder(64);
        foreach (var b in digest) sb.Append(b.ToString("x2"));
        return "v2." + t + "." + sb;
    }

    public static long NowSeconds() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}
