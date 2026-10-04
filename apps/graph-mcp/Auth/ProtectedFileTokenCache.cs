using GraphMcp.Configuration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web.TokenCacheProviders;
using AuthOptions = GraphMcp.Configuration.AuthenticationOptions;

namespace GraphMcp.Auth;

public class ProtectedFileTokenCache : MsalAbstractTokenCacheProvider
{
    private readonly string _path;
    private readonly string _accountKey;
    private readonly IDataProtector _protector;

    // Do not give the base provider a protector: its legacy fallback accepts plaintext after
    // unprotect fails. Our read hook unprotects first and propagates every decryption failure.
    public ProtectedFileTokenCache(IOptions<AuthOptions> options, IOptions<MicrosoftOptions> microsoft,
        IDataProtectionProvider protection) : base()
    {
        _path = options.Value.TokenCachePath;
        _accountKey = microsoft.Value.ExpectedHomeAccountId;
        _protector = protection.CreateProtector("GraphMcp.MsalCache.v1", microsoft.Value.ClientId, _accountKey);
    }

    private void CheckOwner(string key)
    {
        if (!string.Equals(key, _accountKey, StringComparison.OrdinalIgnoreCase))
            throw new GraphAuthenticationException("authentication_required");
    }

    protected override Task<byte[]?> ReadCacheBytesAsync(string cacheKey)
    {
        CheckOwner(cacheKey);
        return Task.FromResult(ProtectedFile.Read(_path, _protector));
    }

    protected override Task WriteCacheBytesAsync(string cacheKey, byte[] bytes)
    {
        CheckOwner(cacheKey);
        ProtectedFile.Write(_path, bytes, _protector);
        return Task.CompletedTask;
    }

    protected override Task RemoveKeyAsync(string cacheKey)
    {
        CheckOwner(cacheKey);
        File.Delete(_path);
        return Task.CompletedTask;
    }

    protected override Task<byte[]?> ReadCacheBytesAsync(string cacheKey, CacheSerializerHints hints)
    {
        hints.CancellationToken.ThrowIfCancellationRequested();
        return ReadCacheBytesAsync(cacheKey);
    }

    protected override Task WriteCacheBytesAsync(string cacheKey, byte[] bytes, CacheSerializerHints hints)
    {
        hints.CancellationToken.ThrowIfCancellationRequested();
        return WriteCacheBytesAsync(cacheKey, bytes);
    }

    protected override Task RemoveKeyAsync(string cacheKey, CacheSerializerHints hints)
    {
        hints.CancellationToken.ThrowIfCancellationRequested();
        return RemoveKeyAsync(cacheKey);
    }
}
