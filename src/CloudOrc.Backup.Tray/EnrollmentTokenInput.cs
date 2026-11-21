namespace CloudOrc.Backup.Tray;

/// <summary>
/// What the user pastes in the status icon: the enrollment token from the portal
/// ("CLB1.&lt;payload&gt;.&lt;checksum&gt;"). Only a local shape check - the agent decodes and verifies it.
/// The token is case-sensitive (base64url), so it is never upper-cased.
/// </summary>
public static class EnrollmentTokenInput
{
    public const string Prefix = "CLB1.";
    public const int MaxLength = 2048;

    public static bool TryNormalize(string? input, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        // Pasting from a mail or chat can add spaces or line breaks inside the token.
        var compact = string.Concat(input.Where(c => !char.IsWhiteSpace(c)));
        if (compact.Length > MaxLength || !compact.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parts = compact.Split('.');
        if (parts.Length != 3 || parts[1].Length == 0 || parts[2].Length == 0)
        {
            return false;
        }

        foreach (var c in parts[1] + parts[2])
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
            {
                return false;
            }
        }

        token = Prefix + parts[1] + "." + parts[2];
        return true;
    }
}
