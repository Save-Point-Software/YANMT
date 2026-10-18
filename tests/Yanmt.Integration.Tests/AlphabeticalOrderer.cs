using Xunit.Sdk;
using Xunit.v3;

namespace Yanmt.Integration.Tests;

/// <summary>Runs a class's tests in name order, so "1_…" runs before "2_…" (SHA-256/AES-128 before AES-256).</summary>
public sealed class AlphabeticalOrderer : ITestCaseOrderer
{
    public IReadOnlyCollection<TTestCase> OrderTestCases<TTestCase>(IReadOnlyCollection<TTestCase> testCases)
        where TTestCase : notnull, ITestCase =>
        testCases.OrderBy(tc => tc.TestCaseDisplayName, StringComparer.Ordinal).ToList();
}
