using Xunit;

namespace LocalRag.Host.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class EnvironmentFactAttribute : FactAttribute
{
    public EnvironmentFactAttribute(string variableName, string? expectedValue = null)
    {
        var actualValue = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(actualValue) ||
            (expectedValue is not null && !string.Equals(actualValue, expectedValue, StringComparison.Ordinal)))
        {
            Skip = expectedValue is null
                ? $"Set {variableName} to run this external integration test."
                : $"Set {variableName}={expectedValue} to run this external integration test.";
        }
    }
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AllEnvironmentFactAttribute : FactAttribute
{
    public AllEnvironmentFactAttribute(params string[] requirements)
    {
        foreach (var requirement in requirements)
        {
            var separator = requirement.IndexOf('=');
            var variableName = separator < 0 ? requirement : requirement[..separator];
            var expectedValue = separator < 0 ? null : requirement[(separator + 1)..];
            var actualValue = Environment.GetEnvironmentVariable(variableName);
            if (!string.IsNullOrWhiteSpace(actualValue) &&
                (expectedValue is null || string.Equals(actualValue, expectedValue, StringComparison.Ordinal)))
            {
                continue;
            }

            Skip = expectedValue is null
                ? $"Set {variableName} to run this external integration test."
                : $"Set {variableName}={expectedValue} to run this external integration test.";
            return;
        }
    }
}
