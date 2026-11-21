using CloudOrc.Backup.Tray;
using Xunit;

namespace CloudOrc.Backup.Tray.Tests;

public class EnrollmentTokenInputTests
{
    private const string Token = "CLB1.eyJ2IjoxLCJjIjoiQUIxMi1DRDM0IiwiYSI6Imh0dHBzOi8vamVuaXNlLmV4YW1wbGUvIiwiZSI6NDEwMjQ0NDgwMH0.ce45e663";

    [Fact]
    public void AcceptsTokenAndKeepsItsCase()
    {
        Assert.True(EnrollmentTokenInput.TryNormalize(Token, out var token));
        Assert.Equal(Token, token);
    }

    [Fact]
    public void RemovesSpacesAndLineBreaksFromAPastedToken()
    {
        var pasted = "  " + Token[..30] + "\r\n " + Token[30..] + " ";
        Assert.True(EnrollmentTokenInput.TryNormalize(pasted, out var token));
        Assert.Equal(Token, token);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ABCD-1234")]          // old activation code: no longer accepted
    [InlineData("CLB1.")]
    [InlineData("CLB1.payload")]       // no checksum
    [InlineData("CLB1.pay/load.abc")]  // not base64url
    [InlineData("XYZ1.payload.abc")]
    public void RejectsAnythingElse(string input)
    {
        Assert.False(EnrollmentTokenInput.TryNormalize(input, out _));
    }
}
