using CloudOrc.Backup.Agent.Enrollment;

namespace CloudOrc.Backup.Agent.Tests.Enrollment;

public class ActivationTokenTests
{
    [Fact]
    public void Create_then_parse_returns_code_and_address()
    {
        var token = ActivationToken.Create("ZL8C-CAVL", "https://api.example.test/", DateTimeOffset.UtcNow.AddMinutes(15));

        Assert.StartsWith(ActivationToken.Prefix, token);
        Assert.True(ActivationToken.TryParse(token, out var data, out var error), error);
        Assert.Equal("ZL8C-CAVL", data!.Code);
        Assert.Equal("https://api.example.test/", data.ApiBase);
    }

    [Fact]
    public void Plain_activation_code_is_not_a_token()
    {
        Assert.False(ActivationToken.LooksLikeToken("ZL8C-CAVL"));
        Assert.False(ActivationToken.LooksLikeToken("ENR-abc"));
        Assert.True(ActivationToken.LooksLikeToken("  clb1.abc.12345678 "));
    }

    [Fact]
    public void Damaged_token_is_rejected()
    {
        var token = ActivationToken.Create("ZL8C-CAVL", "https://api.example.test/", null);
        var damaged = token[..^1] + (token[^1] == 'a' ? 'b' : 'a');

        Assert.False(ActivationToken.TryParse(damaged, out _, out var error));
        Assert.Contains("damaged", error);
        Assert.False(ActivationToken.TryParse(token[..20], out _, out _));
    }

    [Fact]
    public void Expired_token_is_rejected()
    {
        var token = ActivationToken.Create("ZL8C-CAVL", "https://api.example.test/", DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.False(ActivationToken.TryParse(token, out _, out var error));
        Assert.Contains("expired", error);
    }

    [Fact]
    public void Token_made_by_the_CloudOrc_backend_is_read()
    {
        // Produced by backend/src/modules/backupV2/activationToken.js (buildActivationToken).
        const string fromBackend ="CLB1.eyJ2IjoxLCJjIjoiQUIxMi1DRDM0IiwiYSI6Imh0dHBzOi8vamVuaXNlLmV4YW1wbGUvIiwiZSI6NDEwMjQ0NDgwMH0.ce45e663";
        Assert.True(ActivationToken.TryParse(fromBackend, out var data, out var error), error);
        Assert.Equal("AB12-CD34", data!.Code);
        Assert.Equal("https://jenise.example/", data.ApiBase);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4102444800), data.ExpiresAt);
    }

    [Fact]
    public void Token_from_the_backend_format_is_read()
    {
        // Same algorithm as the CloudOrc backend (enrollment.service.js buildActivationToken).
        var token = ActivationToken.Create("AB12-CD34", "https://jenise.example/", DateTimeOffset.FromUnixTimeSeconds(4102444800));
        Assert.True(ActivationToken.TryParse(token, out var data, out _));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(4102444800), data!.ExpiresAt);
    }
}
