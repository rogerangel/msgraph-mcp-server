namespace GraphMcp.Auth;

public interface IGraphCredentialProvider
{
    string ConnectionGeneration { get; }
    Task<string> GetTokenAsync(bool forceRefresh, CancellationToken cancellationToken);
}

public sealed class GraphAuthenticationException(string code) : Exception(
    code == "authentication_required"
        ? "Connect the configured Microsoft account through the private operator interface."
        : "Microsoft authentication is temporarily unavailable.")
{
    public string Code { get; } = code;
}
