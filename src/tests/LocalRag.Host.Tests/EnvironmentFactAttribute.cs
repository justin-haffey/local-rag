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
