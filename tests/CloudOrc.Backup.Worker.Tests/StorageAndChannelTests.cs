using System.Text;
using System.Text.Json;
using CloudOrc.Agent.Contracts.Backup;
using CloudOrc.Backup.Worker.Engines;
using CloudOrc.Backup.Worker.Storage;

namespace CloudOrc.Backup.Worker.Tests;

public sealed class StorageTargetTests
{
    private static StorageGrant S3Grant(string provider = StorageProviderTypes.S3) => new()
    {
        GrantId = "g2",
        Provider = provider,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        Repository = new GrantRepository { RepositoryId = "repo", Engine = BackupEngineNames.Kopia, Path = "repo-1", Password = "pw" },
        S3 = new S3Grant { Endpoint = "https://s3.wasabisys.example", Region = "ap-southeast-1", Bucket = "cloudorc-dr", Prefix = "tenants/t1", AccessKeyId = "AKIAEXAMPLEKEY000000", SecretAccessKey = "very-secret", SessionToken = "sess" }
    };

    [Fact]
    public void S3_CredentialsGoOnlyIntoEnvironment()
    {
        var storage = StorageTarget.Build(S3Grant());
        var args = string.Join(' ', storage.KopiaStorageArgs);

        Assert.DoesNotContain("very-secret", args);
        Assert.DoesNotContain("AKIAEXAMPLEKEY000000", args);
        Assert.Contains("--prefix=tenants/t1/repo-1/", storage.KopiaStorageArgs);
        Assert.Contains("--bucket=cloudorc-dr", storage.KopiaStorageArgs);
        Assert.Contains("--endpoint=s3.wasabisys.example", storage.KopiaStorageArgs);
        Assert.Equal("very-secret", storage.Environment["AWS_SECRET_ACCESS_KEY"]);
        Assert.Equal("sess", storage.Environment["AWS_SESSION_TOKEN"]);
    }

    [Fact]
    public void PlainHttpEndpoint_DisablesTls_OnlyWhenTheGrantSaysHttp()
    {
        var grant = S3Grant();
        var http = new StorageGrant
        {
            GrantId = grant.GrantId, Provider = grant.Provider, ExpiresAt = grant.ExpiresAt, Repository = grant.Repository,
            S3 = new S3Grant { Endpoint = "http://10.0.0.5:8333", Bucket = "b", AccessKeyId = "k", SecretAccessKey = "s" }
        };

        Assert.Contains("--disable-tls", StorageTarget.Build(http).KopiaStorageArgs);
        Assert.DoesNotContain("--disable-tls", StorageTarget.Build(grant).KopiaStorageArgs);
    }

    [Fact]
    public void NonS3Provider_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => StorageTarget.Build(S3Grant(StorageProviderTypes.GoogleDrive)));
    }

    [Theory]
    [InlineData("a/../b")]
    [InlineData("./x")]
    public void RepositoryPath_WithRelativeSegments_IsRejected(string path) =>
        Assert.Throws<InvalidOperationException>(() => StorageTarget.NormalizeRepoPath(path));
}

public class WorkerChannelTests
{
    private static StorageGrant Grant(string id) => new()
    {
        GrantId = id,
        Provider = StorageProviderTypes.S3,
        ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
        Repository = new GrantRepository { RepositoryId = "r", Engine = "kopia", Path = "p", Password = "x" }
    };

    [Fact]
    public async Task GrantRenewal_RoundTrip_AndCancel()
    {
        var input = new BlockingTextReader();
        var output = new StringWriter();
        var channel = new WorkerChannel(input, output, Grant("g1"));
        var listen = Task.Run(channel.ListenAsync);

        var renewal = channel.RenewGrantAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.Contains("\"type\":\"grant.renew\"", output.ToString());

        input.Push(JsonSerializer.Serialize(new WorkerEnvelope { Type = WorkerMessageTypes.GrantRenewed, Grant = Grant("g2") }, WorkerEnvelope.JsonOptions));
        var renewed = await renewal;
        Assert.Equal("g2", renewed!.GrantId);
        Assert.Equal("g2", channel.CurrentGrant.GrantId);

        input.Push(JsonSerializer.Serialize(new WorkerEnvelope { Type = WorkerMessageTypes.Cancel }, WorkerEnvelope.JsonOptions));
        await Task.Delay(50);
        Assert.True(channel.Cancellation.IsCancellationRequested);

        input.Complete();
        await listen;
    }

    [Fact]
    public async Task SupervisorClosingStdin_CancelsTheJob()
    {
        var input = new BlockingTextReader();
        var channel = new WorkerChannel(input, new StringWriter(), Grant("g1"));
        var listen = Task.Run(channel.ListenAsync);
        input.Complete();
        await listen;
        Assert.True(channel.Cancellation.IsCancellationRequested);
    }

    [Fact]
    public void LogAndResultMessages_AreRedacted()
    {
        var output = new StringWriter();
        var channel = new WorkerChannel(new StringReader(""), output, Grant("g1"));
        channel.Log("info", "token=abcdef123456 leaked");
        channel.Result(new WorkerResult { State = BackupJobStates.Failed, Message = "Bearer abc.def" });
        Assert.DoesNotContain("abcdef123456", output.ToString());
        Assert.DoesNotContain("abc.def", output.ToString());
    }

    private sealed class BlockingTextReader : TextReader
    {
        private readonly System.Collections.Concurrent.BlockingCollection<string?> _lines = new();

        public void Push(string line) => _lines.Add(line);

        public void Complete() => _lines.Add(null);

        public override Task<string?> ReadLineAsync() => Task.Run(() => _lines.Take());

        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => new(Task.Run(() => _lines.Take(cancellationToken), cancellationToken));
    }
}
