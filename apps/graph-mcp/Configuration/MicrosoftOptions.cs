namespace GraphMcp.Configuration;

public sealed class MicrosoftOptions
{
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ExpectedUserObjectId { get; set; } = "";
    public string ExpectedHomeAccountId => $"{ExpectedUserObjectId.ToLowerInvariant()}.{TenantId.ToLowerInvariant()}";
}
