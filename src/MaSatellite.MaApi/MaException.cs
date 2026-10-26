namespace MaSatellite.MaApi;

public class MaException : Exception
{
    public MaException(string message)
        : base(message)
    {
    }

    public MaException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>The server needs a token and we have none, or it rejected ours.</summary>
public sealed class MaAuthException : MaException
{
    public MaAuthException(string message)
        : base(message)
    {
    }
}

/// <summary>The server answered a command with an error.</summary>
public sealed class MaCommandException : MaException
{
    public MaCommandException(string command, int errorCode, string? details)
        : base($"Music Assistant rejected '{command}' (code {errorCode}): {details}")
    {
        Command = command;
        ErrorCode = errorCode;
    }

    public string Command { get; }

    public int ErrorCode { get; }
}
