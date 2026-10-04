using System.ComponentModel;
using GraphMcp.Graph;
using GraphMcp.Models;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GraphMcp.Tools;

public sealed class AccountTools(IAccountService account, ToolExecutor executor)
{
    [McpServerTool(Name = "account_me", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true,
        UseStructuredContent = true, OutputSchemaType = typeof(AccountDto))]
    [Description("Identify the connected Microsoft 365 account. Returns only minimal profile information; does not search the directory.")]
    public Task<CallToolResult> Me(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken = default) =>
        executor.ExecuteAsync("account_me", context, account.GetMeAsync, cancellationToken);
}
