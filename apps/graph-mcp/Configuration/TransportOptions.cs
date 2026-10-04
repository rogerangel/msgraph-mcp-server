namespace GraphMcp.Configuration;

public sealed class TransportOptions
{
    public int McpPort { get; set; } = 8080;
    public int OperatorPort { get; set; } = 8081;
    public string[] AllowedHosts { get; set; } = ["localhost", "127.0.0.1"];
    public string[] AllowedOrigins { get; set; } = [];
    public string[] KnownProxies { get; set; } = [];
    public int MaxRequestBytes { get; set; } = 262_144;
}
