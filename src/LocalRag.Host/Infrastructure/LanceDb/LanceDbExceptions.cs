namespace LocalRag.Infrastructure.LanceDb;

public sealed class LanceDbConfigurationException(string message) : InvalidOperationException(message);

public sealed class LanceDbNativeException(int status, string message) : InvalidOperationException(message)
{
    public int Status { get; } = status;
}

