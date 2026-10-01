using DSPiConsole.Core.Models;

namespace DSPiConsole.Core.Tests;

/// <summary>Firmware versions and the REQ_GET_PLATFORM decode. Ported from the
/// macOS Console's FirmwarePlatformReplyTests.</summary>
public class FirmwareVersionTests
{
    /// <summary>A platform reply: platform, major, the legacy minor/patch
    /// nibbles, a reserved byte, full-width minor and patch, the ordinal.</summary>
    private static byte[] Reply(int major, int minor, int patch, int beta = 0, int length = 7) =>
        new byte[] { 1, (byte)major, (byte)((minor << 4) | (patch & 0x0F)), 0, (byte)minor, (byte)patch, (byte)beta }[..length];

    [Fact]
    public void FullReplyCarriesTheOrdinal()
    {
        var decoded = FirmwareVersion.FromPlatformReply(Reply(1, 1, 6, beta: 3));
        Assert.Equal((byte)1, decoded?.Platform);
        Assert.Equal(new FirmwareVersion(1, 1, 6, 3), decoded?.Version);
        Assert.Equal(new FirmwareVersion(1, 1, 6), FirmwareVersion.FromPlatformReply(Reply(1, 1, 6, beta: 0))?.Version);
    }

    [Fact]
    public void ShortReplyAt116IsAnEarlyBetaNotAFinal()
    {
        var version = FirmwareVersion.FromPlatformReply(Reply(1, 1, 6, length: 6))?.Version;
        Assert.Equal(FirmwareVersion.EarlyBeta, version?.Beta);
        Assert.NotEqual(new FirmwareVersion(1, 1, 6), version);
    }

    [Fact]
    public void ShortReplyBelow116IsStillAFinal()
    {
        Assert.Equal(new FirmwareVersion(1, 1, 5), FirmwareVersion.FromPlatformReply(Reply(1, 1, 5, length: 4))?.Version);
        Assert.Equal(new FirmwareVersion(1, 0, 9), FirmwareVersion.FromPlatformReply(Reply(1, 0, 9, length: 6))?.Version);
    }

    [Fact]
    public void FullWidthFieldsPassFifteen()
    {
        Assert.Equal(new FirmwareVersion(1, 2, 17, 1), FirmwareVersion.FromPlatformReply(Reply(1, 2, 17, beta: 1))?.Version);
    }

    [Fact]
    public void TooShortReplyIsRejected() =>
        Assert.Null(FirmwareVersion.FromPlatformReply(new byte[] { 1, 1, 0x16 }));

    [Fact]
    public void EarlyBetaSortsBelowEveryNumberedBetaAndTheFinal()
    {
        var early = new FirmwareVersion(1, 1, 6, FirmwareVersion.EarlyBeta);
        Assert.True(early < new FirmwareVersion(1, 1, 6, 1));
        Assert.True(early < new FirmwareVersion(1, 1, 6, 2));
        Assert.True(early < new FirmwareVersion(1, 1, 6));
        Assert.True(early > new FirmwareVersion(1, 1, 5));
        Assert.True(new FirmwareVersion(1, 1, 7, 3) < new FirmwareVersion(1, 1, 7));
    }

    [Fact]
    public void DescriptionAndTag()
    {
        var early = new FirmwareVersion(1, 1, 6, FirmwareVersion.EarlyBeta);
        Assert.Equal("1.1.6 early beta", early.ToString());
        Assert.Null(early.TagSuffix);
        Assert.Equal("1.1.6-beta2", new FirmwareVersion(1, 1, 6, 2).TagSuffix);
        Assert.Equal("1.1.6 beta 4", new FirmwareVersion(1, 1, 6, 4).ToString());
    }

    [Theory]
    [InlineData("1.1.7", 1, 1, 7, 0)]
    [InlineData("1.1.6-beta4", 1, 1, 6, 4)]
    [InlineData("v1.2", 1, 2, 0, 0)]
    [InlineData("1.1.6-beta4+abc123", 1, 1, 6, 4)]
    [InlineData("1.1.5-rc1", 1, 1, 5, 0)]
    public void ParsesAppAndTagSpellings(string text, int major, int minor, int patch, int beta) =>
        Assert.Equal(new FirmwareVersion(major, minor, patch, beta), FirmwareVersion.Parse(text));

    [Fact]
    public void ParseRejectsNoNumber()
    {
        Assert.Null(FirmwareVersion.Parse("dev"));
        Assert.Null(FirmwareVersion.Parse(""));
    }
}
