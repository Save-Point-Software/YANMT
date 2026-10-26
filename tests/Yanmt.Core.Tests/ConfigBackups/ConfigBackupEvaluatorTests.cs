using Yanmt.Core.ConfigBackups;
using Yanmt.Core.Status;

namespace Yanmt.Core.Tests.ConfigBackups;

public class ConfigBackupEvaluatorTests
{
    [Fact]
    public void Hash_is_stable_lowercase_sha256()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ConfigBackupEvaluator.Hash("abc"));
    }

    [Fact]
    public void Classifies_initial_unchanged_and_changed()
    {
        Assert.Equal(ConfigChangeKind.Initial, ConfigBackupEvaluator.Classify(null, "h1"));
        Assert.Equal(ConfigChangeKind.Unchanged, ConfigBackupEvaluator.Classify("h1", "h1"));
        Assert.Equal(ConfigChangeKind.Changed, ConfigBackupEvaluator.Classify("h1", "h2"));
    }

    [Theory]
    [InlineData(ConfigChangeKind.Initial)]
    [InlineData(ConfigChangeKind.Unchanged)]
    [InlineData(ConfigChangeKind.Changed)]
    public void Success_is_normal(ConfigChangeKind kind) =>
        Assert.Equal(Severity.Normal, ConfigBackupEvaluator.EvaluateSuccess(kind).State);

    [Theory]
    [InlineData("SSH auth failed", "SSH auth failed")]
    [InlineData("  ", "backup failed")]
    public void Failure_is_warning_with_a_reason(string reason, string expected)
    {
        var e = ConfigBackupEvaluator.EvaluateFailure(reason);
        Assert.Equal(Severity.Warning, e.State);
        Assert.Equal(expected, e.Reason);
    }
}
