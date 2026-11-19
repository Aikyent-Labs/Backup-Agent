using CloudOrc.Agent.Contracts.Backup;

namespace CloudOrc.Backup.Worker.Storage;

/// <summary>Everything Kopia needs to reach the repository for one invocation.</summary>
public sealed class EngineStorage
{
    /// <summary>Environment for the engine. Contains SECRETS (S3 keys) - env only, never argv.</summary>
    public required Dictionary<string, string?> Environment { get; init; }

    /// <summary>Kopia: "repository connect|create" storage sub-command + flags (no secrets).</summary>
    public required IReadOnlyList<string> KopiaStorageArgs { get; init; }
}

/// <summary>
/// Provider adapter on the agent side. The Backup Agent only speaks S3: CloudOrc storage
/// (AikySpace / SeaweedFS), Wasabi, Backblaze B2 and Amazon S3 are all S3-compatible, so adding a
/// provider is a backend grant change only - never an agent change. Grants are short-lived and
/// scoped to this device's repository prefix.
/// </summary>
public static class StorageTarget
{
    public static EngineStorage Build(StorageGrant grant)
    {
        var repoPath = NormalizeRepoPath(grant.Repository.Path);
        if (grant.Provider is not (StorageProviderTypes.S3 or "b2" or "wasabi"))
        {
            throw new InvalidOperationException($"Unsupported storage provider '{grant.Provider}'. The Backup Agent supports S3-compatible storage only.");
        }

        var s3 = grant.S3 ?? throw new InvalidOperationException("Grant has no S3 section.");
        var endpoint = new Uri(s3.Endpoint.Contains("://", StringComparison.Ordinal) ? s3.Endpoint : "https://" + s3.Endpoint);
        var keyPrefix = string.Join('/', new[] { s3.Prefix?.Trim('/'), repoPath }.Where(p => !string.IsNullOrEmpty(p))) + "/";

        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["AWS_ACCESS_KEY_ID"] = s3.AccessKeyId,
            ["AWS_SECRET_ACCESS_KEY"] = s3.SecretAccessKey,
            ["AWS_SESSION_TOKEN"] = s3.SessionToken,
            ["AWS_DEFAULT_REGION"] = s3.Region
        };

        var kopia = new List<string> { "s3", $"--bucket={s3.Bucket}", $"--endpoint={endpoint.Authority}", $"--prefix={keyPrefix}" };
        if (!string.IsNullOrWhiteSpace(s3.Region))
        {
            kopia.Add($"--region={s3.Region}");
        }

        if (endpoint.Scheme == Uri.UriSchemeHttp)
        {
            kopia.Add("--disable-tls");
        }

        return new EngineStorage { Environment = env, KopiaStorageArgs = kopia };
    }

    /// <summary>Repository path inside the provider: forward slashes, no leading/trailing slash, no "..".</summary>
    public static string NormalizeRepoPath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is ".." or "."))
        {
            throw new InvalidOperationException("Repository path must not contain relative segments.");
        }

        return string.Join('/', parts);
    }
}
