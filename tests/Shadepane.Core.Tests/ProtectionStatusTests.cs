using Shadepane.Core.Policy;

namespace Shadepane.Core.Tests;

public class ProtectionStatusTests
{
    [Theory]
    [InlineData(false, false, false, ProtectionIndicator.Protected)]
    [InlineData(false, false, true, ProtectionIndicator.ProtectedSite)]
    [InlineData(false, true, false, ProtectionIndicator.CaptureBlocked)]
    [InlineData(false, true, true, ProtectionIndicator.CaptureBlocked)]
    [InlineData(true, false, false, ProtectionIndicator.ContentHidden)]
    [InlineData(true, true, true, ProtectionIndicator.ContentHidden)]
    public void MostImportantStateWins(bool hidden, bool captureBlocked, bool protectedSite, ProtectionIndicator expected) =>
        Assert.Equal(expected, ProtectionStatus.Resolve(hidden, captureBlocked, protectedSite));
}
