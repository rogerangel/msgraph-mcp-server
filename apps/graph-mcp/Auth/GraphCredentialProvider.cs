using GraphMcp.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.Identity.Web;

namespace GraphMcp.Auth;

public sealed class GraphCredentialProvider(ITokenAcquisition tokens, OwnerIdentityStore owner,
    IOptions<MicrosoftOptions> microsoft) : IGraphCredentialProvider
{
    public string ConnectionGeneration => owner.Generation;

    public async Task<string> GetTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await owner.Gate.WaitAsync(cancellationToken);
        try
        {
            var result = await tokens.GetAuthenticationResultForUserAsync(GraphScopes.Required,
                authenticationScheme: GraphAuthenticationExtensions.OidcScheme,
                tenantId: microsoft.Value.TenantId, user: owner.GetPrincipal(),
                tokenAcquisitionOptions: new TokenAcquisitionOptions
                {
                    ForceRefresh = forceRefresh,
                    CancellationToken = cancellationToken
                });
            cancellationToken.ThrowIfCancellationRequested();
            if (!GraphScopes.AreExactlyApproved(result.Scopes)
                || !string.Equals(result.Account?.HomeAccountId.Identifier, microsoft.Value.ExpectedHomeAccountId, StringComparison.OrdinalIgnoreCase))
                throw new GraphAuthenticationException("authentication_required");
            return result.AccessToken;
        }
        catch (MicrosoftIdentityWebChallengeUserException)
        {
            throw new GraphAuthenticationException("authentication_required");
        }
        catch (MsalUiRequiredException)
        {
            throw new GraphAuthenticationException("authentication_required");
        }
        catch (MsalException)
        {
            throw new GraphAuthenticationException("authentication_unavailable");
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new GraphAuthenticationException("authentication_required");
        }
        catch (IOException)
        {
            throw new GraphAuthenticationException("authentication_unavailable");
        }
        catch (UnauthorizedAccessException)
        {
            throw new GraphAuthenticationException("authentication_unavailable");
        }
        finally { owner.Gate.Release(); }
    }
}
