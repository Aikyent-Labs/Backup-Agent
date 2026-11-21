using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CloudOrc.Backup.Agent.Enrollment;

/// <summary>The contents of a CloudOrc enrollment token: the one-time activation code and where to redeem it.</summary>
public sealed record ActivationTokenData(string Code, string ApiBase, DateTimeOffset? ExpiresAt);

/// <summary>
/// CloudOrc enrollment token (portal → Add device): ONE string the customer copies instead of an
/// activation code plus the CloudOrc address. Format:
/// <c>CLB1.&lt;base64url(JSON {"v":1,"c":code,"a":apiBase,"e":unixSeconds})&gt;.&lt;check&gt;</c>
/// where <c>check</c> is the first 8 hex characters of SHA-256 over the payload part, so a
/// mistyped or truncated token is rejected with a clear message. The token is single-use and
/// short-lived because the activation code inside it is.
/// </summary>
public static class ActivationToken
{
    public const string Prefix = "CLB1.";

    public static bool LooksLikeToken(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Trim().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    public static bool TryParse(string? text, out ActivationTokenData? data, out string? error)
    {
        data = null;
        error = null;
        if (!LooksLikeToken(text))
        {
            error = "This is not a CloudOrc enrollment token.";
            return false;
        }

        var parts = text!.Trim()[Prefix.Length..].Split('.');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length != 8)
        {
            error = "The enrollment token is incomplete. Copy it again from the CloudOrc portal.";
            return false;
        }

        if (!string.Equals(Check(parts[0]), parts[1], StringComparison.OrdinalIgnoreCase))
        {
            error = "The enrollment token is damaged (it does not match its check). Copy it again from the CloudOrc portal.";
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(FromBase64Url(parts[0]));
            var root = doc.RootElement;
            var code = root.TryGetProperty("c", out var c) ? c.GetString() : null;
            var apiBase = root.TryGetProperty("a", out var a) ? a.GetString() : null;
            DateTimeOffset? expires = root.TryGetProperty("e", out var e) && e.TryGetInt64(out var secs)
                ? DateTimeOffset.FromUnixTimeSeconds(secs)
                : null;
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(apiBase))
            {
                error = "The enrollment token does not contain an activation code and address.";
                return false;
            }

            if (expires is { } exp && exp < DateTimeOffset.UtcNow)
            {
                error = "The enrollment token has expired. Create a new one in the CloudOrc portal (Add device).";
                return false;
            }

            data = new ActivationTokenData(code.Trim(), apiBase.Trim(), expires);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            error = "The enrollment token could not be read. Copy it again from the CloudOrc portal.";
            return false;
        }
    }

    /// <summary>Builds a token (used by tests; the CloudOrc backend builds the real ones).</summary>
    public static string Create(string code, string apiBase, DateTimeOffset? expiresAt)
    {
        var json = JsonSerializer.Serialize(new { v = 1, c = code, a = apiBase, e = expiresAt?.ToUnixTimeSeconds() });
        var payload = ToBase64Url(Encoding.UTF8.GetBytes(json));
        return $"{Prefix}{payload}.{Check(payload)}";
    }

    private static string Check(string payload) =>
        Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(payload)))[..8].ToLowerInvariant();

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return Convert.FromBase64String(s);
    }
}
