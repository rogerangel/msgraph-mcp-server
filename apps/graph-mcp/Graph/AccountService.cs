using GraphMcp.Configuration;
using GraphMcp.Infrastructure;
using GraphMcp.Models;
using Microsoft.Extensions.Options;

namespace GraphMcp.Graph;

public sealed class AccountService(GraphHttpClient graph, IOptions<MicrosoftOptions> microsoft) : IAccountService
{
    public async Task<AccountDto> GetMeAsync(CancellationToken cancellationToken)
    {
        using var response = await graph.GetAsync("me?$select=id,displayName,mail,userPrincipalName", cancellationToken);
        var value = response.RootElement;
        var id = value.Text("id");
        if (!string.Equals(id, microsoft.Value.ExpectedUserObjectId, StringComparison.OrdinalIgnoreCase))
            throw new GraphOperationException("account_mismatch", "The connected Microsoft account does not match the configured account.");
        return new(id!, value.BoundedText("displayName", 256), value.BoundedText("mail", 320), value.BoundedText("userPrincipalName", 320));
    }
}
