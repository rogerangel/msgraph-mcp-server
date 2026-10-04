namespace GraphMcp.Configuration;

public sealed class AuthenticationOptions
{
    // Public HTTPS origin, independent of either internal listener port.
    public string OperatorBaseUrl { get; set; } = "";
    public string ClientCertificatePath { get; set; } = "";
    public string? ClientCertificatePasswordFile { get; set; }
    public string TokenCachePath { get; set; } = "/data/msal-cache.bin";
    public string OwnerStatePath { get; set; } = "/data/owner.bin";
    public string DataProtectionKeysPath { get; set; } = "/data/keys";
    public string DataProtectionCertificatePath { get; set; } = "";
    public string? DataProtectionCertificatePasswordFile { get; set; }

    public const string CallbackPath = "/operator/signin-oidc";
    public string CallbackUrl => OperatorBaseUrl.TrimEnd('/') + CallbackPath;

    public bool HasValidOperatorOrigin() =>
        Uri.TryCreate(OperatorBaseUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.AbsolutePath == "/"
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);
}
