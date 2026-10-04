using System.Security.Claims;
using System.Text.Json;
using GraphMcp.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using AuthOptions = GraphMcp.Configuration.AuthenticationOptions;

namespace GraphMcp.Auth;

public sealed class OwnerIdentityStore : IDisposable
{
    public const string GenerationClaim = "graph_mcp_generation";
    public const string GenerationProperty = "graph_mcp_login_generation";
    private readonly MicrosoftOptions _microsoft;
    private readonly AuthOptions _options;
    private readonly IDataProtector _protector;
    private readonly FileStream _processLock;
    private OwnerState _state;

    // Entire MSAL operations and callback processing share this lock, not just disk writes.
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public string Generation => _state.Generation;
    public bool Connected => _state.Connected;

    public OwnerIdentityStore(IOptions<MicrosoftOptions> microsoft, IOptions<AuthOptions> options, IDataProtectionProvider protection)
    {
        _microsoft = microsoft.Value;
        _options = options.Value;
        _protector = protection.CreateProtector("GraphMcp.Owner.v1", _microsoft.ClientId, _microsoft.ExpectedHomeAccountId);
        ProtectedFile.CreatePrivateDirectory(Path.GetDirectoryName(Path.GetFullPath(_options.OwnerStatePath))!);
        _processLock = new FileStream(_options.OwnerStatePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var bytes = ProtectedFile.Read(_options.OwnerStatePath, _protector);
            _state = bytes is null ? new(false, Guid.NewGuid().ToString("N"))
                : JsonSerializer.Deserialize<OwnerState>(bytes) ?? throw new InvalidDataException("Invalid protected owner state.");
            if (!Guid.TryParseExact(_state.Generation, "N", out _))
                throw new InvalidDataException("Invalid protected owner generation.");
        }
        catch
        {
            _processLock.Dispose();
            throw;
        }
    }

    public bool IsExpectedOwner(ClaimsPrincipal? principal) => principal is not null
        && string.Equals(principal.GetTenantId(), _microsoft.TenantId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(principal.GetObjectId(), _microsoft.ExpectedUserObjectId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(principal.GetHomeTenantId(), _microsoft.TenantId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(principal.GetHomeObjectId(), _microsoft.ExpectedUserObjectId, StringComparison.OrdinalIgnoreCase);

    public ClaimsPrincipal GetPrincipal()
    {
        if (!Connected) throw new GraphAuthenticationException("authentication_required");
        return new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("oid", _microsoft.ExpectedUserObjectId), new Claim("tid", _microsoft.TenantId),
            new Claim("uid", _microsoft.ExpectedUserObjectId), new Claim("utid", _microsoft.TenantId)
        ], GraphAuthenticationExtensions.OidcScheme));
    }

    // Caller holds Gate. Persist the generation before allowing a new connection or deleting cache state.
    public void Connect(ClaimsPrincipal principal)
    {
        if (!IsExpectedOwner(principal)) throw new GraphAuthenticationException("authentication_required");
        Save(new(true, Guid.NewGuid().ToString("N")));
    }

    public void Disconnect()
    {
        _state = new(false, Guid.NewGuid().ToString("N"));
        // Missing state is disconnected after restart, even if writing its replacement fails.
        File.Delete(_options.OwnerStatePath);
        ProtectedFile.Write(_options.OwnerStatePath, JsonSerializer.SerializeToUtf8Bytes(_state), _protector);
    }

    private void Save(OwnerState state)
    {
        ProtectedFile.Write(_options.OwnerStatePath, JsonSerializer.SerializeToUtf8Bytes(state), _protector);
        _state = state;
    }

    public void Dispose()
    {
        _processLock.Dispose();
        Gate.Dispose();
    }

    private sealed record OwnerState(bool Connected, string Generation);
}
