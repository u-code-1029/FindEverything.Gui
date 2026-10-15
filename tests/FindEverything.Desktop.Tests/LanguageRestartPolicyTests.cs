using FindEverything.Desktop.Localization;
using Xunit;

namespace FindEverything.Desktop.Tests;

public sealed class LanguageRestartPolicyTests
{
    [Fact]
    public void A_snapshot_started_before_or_during_the_write_cannot_clear_the_newer_choice()
    {
        var policy = new LanguageRestartPolicy();
        var oldRevision = policy.Revision;
        using var change = policy.BeginChange("en-US");
        var writeRevision = policy.Revision;
        Assert.True(policy.SuppressWatcherRestart("ko-KR", writeRevision));
        change.Commit();
        Assert.True(policy.SuppressWatcherRestart("ko-KR", oldRevision));
        Assert.True(policy.SuppressWatcherRestart("ko-KR", writeRevision));
        Assert.True(policy.SuppressWatcherRestart("en-US", policy.Revision));
        Assert.False(policy.AllowsAutomaticRestart("en-US"));
    }

    [Fact]
    public void A_failed_write_restores_the_previous_policy()
    {
        var policy = new LanguageRestartPolicy();
        using (policy.BeginChange("en-US"))
            Assert.False(policy.AllowsAutomaticRestart("en-US"));
        Assert.True(policy.AllowsAutomaticRestart("en-US"));

        using (var choice = policy.BeginChange("en-US")) choice.Commit();
        using (policy.BeginChange("ko-KR"))
            Assert.False(policy.AllowsAutomaticRestart("ko-KR"));
        Assert.False(policy.AllowsAutomaticRestart("en-US"));
        Assert.True(policy.AllowsAutomaticRestart("ko-KR"));
    }
}
