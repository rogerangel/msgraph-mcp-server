using System.Text.Json;
using GraphMcp.Infrastructure;
using GraphMcp.Models;
using ModelContextProtocol.Protocol;

namespace GraphMcp.Tests.Mcp;

public sealed class DraftToolTests
{
    [Theory]
    [InlineData("mail_create_draft", "{\"subject\":\"Draft subject\",\"bodyText\":\"First line\\nSecond line\\tTab\"}", "create")]
    [InlineData("mail_create_reply_draft", "{\"messageId\":\"source\",\"bodyText\":\"\"}", "reply")]
    [InlineData("mail_create_reply_all_draft", "{\"messageId\":\"source\",\"bodyText\":\"<b>literal text</b>\"}", "replyAll")]
    [InlineData("mail_create_forward_draft", "{\"messageId\":\"source\",\"bodyText\":\"Forward text\",\"toRecipients\":[\"person@example.com\"]}", "forward")]
    public async Task CreationToolsUseExplicitServicesAndReturnCommittedReceipt(string tool, string json, string operation)
    {
        using var factory = new McpTestFactory();
        await using var client = await factory.ConnectAsync();
        var result = await client.CallToolAsync(Request(tool, json), TestContext.Current.CancellationToken);
        Assert.False(result.IsError);
        var receipt = result.StructuredContent!.Value;
        Assert.Equal("committed", receipt.GetProperty("outcome").GetString());
        Assert.False(receipt.GetProperty("retrySafe").GetBoolean());
        Assert.False(receipt.GetProperty("reconciliationRequired").GetBoolean());
        Assert.Equal("draft-id", receipt.GetProperty("messageId").GetString());
        Assert.Equal("protected-edit-version", receipt.GetProperty("draft").GetProperty("editVersion").GetString());
        Assert.Equal(operation, factory.Drafts.LastOperation);
        Assert.Equal(1, factory.Drafts.Calls);
        Assert.Equal(0, factory.Graph.Calls);
    }

    [Fact]
    public async Task UpdatePreservesOmissionAndExplicitClearing()
    {
        using var factory = new McpTestFactory();
        await using var client = await factory.ConnectAsync();
        var result = await client.CallToolAsync(Request("mail_update_draft",
            "{\"messageId\":\"external-draft\",\"editVersion\":\"version\",\"subject\":\"\",\"bodyText\":\"\",\"ccRecipients\":[]}"), TestContext.Current.CancellationToken);
        Assert.False(result.IsError);
        var request = Assert.IsType<UpdateDraftRequest>(factory.Drafts.LastRequest);
        Assert.Equal("external-draft", request.MessageId);
        Assert.Equal("", request.Subject);
        Assert.Equal("", request.BodyText);
        Assert.Empty(request.CcRecipients!);
        Assert.Null(request.ToRecipients);
        Assert.Null(request.BccRecipients);
    }

    [Theory]
    [InlineData("mail_create_draft", "{}")]
    [InlineData("mail_create_draft", "{\"subject\":\"s\"}")]
    [InlineData("mail_create_draft", "{\"subject\":42,\"bodyText\":\"x\"}")]
    [InlineData("mail_create_draft", "{\"subject\":\"s\",\"bodyText\":{}}")]
    [InlineData("mail_create_reply_draft", "{\"bodyText\":\"x\"}")]
    [InlineData("mail_create_reply_all_draft", "{\"messageId\":false,\"bodyText\":\"x\"}")]
    [InlineData("mail_create_forward_draft", "{\"messageId\":\"m\",\"bodyText\":\"x\"}")]
    [InlineData("mail_create_forward_draft", "{\"messageId\":\"m\",\"bodyText\":\"x\",\"toRecipients\":\"person@example.com\"}")]
    [InlineData("mail_update_draft", "{\"messageId\":\"m\",\"bodyText\":\"x\"}")]
    [InlineData("mail_update_draft", "{\"messageId\":\"m\",\"editVersion\":[],\"bodyText\":\"x\"}")]
    [InlineData("mail_create_draft", "{\"subject\":\"\",\"bodyText\":\"x\"}")]
    [InlineData("mail_create_draft", "{\"subject\":\"s\",\"bodyText\":\"x\",\"attachments\":[]}")]
    [InlineData("mail_create_draft", "{\"subject\":\"s\",\"bodyText\":\"x\",\"sender\":\"a@example.com\"}")]
    [InlineData("mail_create_draft", "{\"subject\":\"s\",\"bodyText\":\"x\",\"toRecipients\":[\"Name <a@example.com>\"]}")]
    [InlineData("mail_create_draft", "{\"subject\":\"s\",\"bodyText\":\"x\\u0000\"}")]
    [InlineData("mail_create_reply_draft", "{\"messageId\":\"m\",\"bodyText\":\"x\",\"comment\":\"escape\"}")]
    [InlineData("mail_create_reply_all_draft", "{\"messageId\":\"m\",\"bodyText\":\"x\",\"toRecipients\":[]}")]
    [InlineData("mail_create_forward_draft", "{\"messageId\":\"m\",\"bodyText\":\"x\",\"toRecipients\":[]}")]
    [InlineData("mail_update_draft", "{\"messageId\":\"m\",\"editVersion\":\"v\"}")]
    [InlineData("mail_update_draft", "{\"messageId\":\"m\",\"editVersion\":\"v\",\"bodyText\":null}")]
    [InlineData("mail_update_draft", "{\"messageId\":\"m\",\"editVersion\":\"v\",\"toRecipients\":null}")]
    [InlineData("mail_update_draft", "{\"messageId\":\"m\",\"editVersion\":\"v\",\"subject\":null}")]
    [InlineData("mail_update_draft", "{\"messageId\":\"m\",\"editVersion\":\"v\",\"isRead\":true}")]
    [InlineData("mail_update_draft", "{\"messageId\":\"m\",\"editVersion\":\"v\",\"importance\":\"high\"}")]
    [InlineData("mail_update_draft", "{\"messageId\":\"m\",\"editVersion\":\"v\",\"url\":\"https://graph.microsoft.com/v1.0/me/messages/m/send\"}")]
    public async Task InvalidWritesReturnNotAppliedWithoutCallingService(string tool, string json)
    {
        using var factory = new McpTestFactory();
        await using var client = await factory.ConnectAsync();
        var result = await client.CallToolAsync(Request(tool, json), TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Equal("not_applied", result.StructuredContent!.Value.GetProperty("outcome").GetString());
        Assert.Equal("invalid_input", result.StructuredContent.Value.GetProperty("code").GetString());
        Assert.Equal(0, factory.Drafts.Calls);
        Assert.Equal(0, factory.Graph.Calls);
    }

    [Fact]
    public async Task BodyLimitAllowsTwentyThousandUnicodeCharactersButRejectsTheNext()
    {
        using var factory = new McpTestFactory();
        await using var client = await factory.ConnectAsync();
        var arguments = new Dictionary<string, object?> { ["subject"] = "Unicode draft", ["bodyText"] = new string('漢', 20_000) };
        var accepted = await client.CallToolAsync("mail_create_draft", arguments, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(accepted.IsError);
        Assert.Equal(20_000, Assert.IsType<CreateDraftRequest>(factory.Drafts.LastRequest).BodyText.Length);
        arguments["bodyText"] = new string('漢', 20_001);
        var rejected = await client.CallToolAsync("mail_create_draft", arguments, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(rejected.IsError);
        Assert.Equal("not_applied", rejected.StructuredContent!.Value.GetProperty("outcome").GetString());
        Assert.Equal(1, factory.Drafts.Calls);
    }

    [Fact]
    public async Task RecipientLimitAppliesAcrossAllExplicitLists()
    {
        using var factory = new McpTestFactory();
        await using var client = await factory.ConnectAsync();
        var arguments = new Dictionary<string, object?>
        {
            ["subject"] = "s", ["bodyText"] = "", ["toRecipients"] = Addresses(10), ["ccRecipients"] = Addresses(10)
        };
        Assert.False((await client.CallToolAsync("mail_create_draft", arguments, cancellationToken: TestContext.Current.CancellationToken)).IsError);
        arguments["bccRecipients"] = Addresses(1);
        Assert.True((await client.CallToolAsync("mail_create_draft", arguments, cancellationToken: TestContext.Current.CancellationToken)).IsError);
        Assert.Equal(1, factory.Drafts.Calls);
    }

    [Fact]
    public async Task DeploymentMayTightenBodyAndRecipientLimits()
    {
        using var factory = new McpTestFactory();
        factory.ConfigurationOverrides["Drafts:MaxBodyChars"] = "10";
        factory.ConfigurationOverrides["Drafts:MaxRecipients"] = "2";
        await using var client = await factory.ConnectAsync();
        Assert.True((await client.CallToolAsync("mail_create_draft", new Dictionary<string, object?> { ["subject"] = "s", ["bodyText"] = new string('x', 11) },
            cancellationToken: TestContext.Current.CancellationToken)).IsError);
        Assert.True((await client.CallToolAsync("mail_create_draft", new Dictionary<string, object?> { ["subject"] = "s", ["bodyText"] = "", ["toRecipients"] = Addresses(3) },
            cancellationToken: TestContext.Current.CancellationToken)).IsError);
        Assert.Equal(0, factory.Drafts.Calls);
    }

    [Fact]
    public async Task UpdateToolRemainsDiscoverableButRealServiceIsDisabledBeforeGraph()
    {
        using var factory = new McpTestFactory { UseRealDraftService = true };
        await using var client = await factory.ConnectAsync();
        Assert.Contains(await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken), tool => tool.Name == "mail_update_draft");
        var result = await client.CallToolAsync(Request("mail_update_draft",
            "{\"messageId\":\"m\",\"editVersion\":\"v\",\"bodyText\":\"x\"}"), TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Equal("not_applied", result.StructuredContent!.Value.GetProperty("outcome").GetString());
        Assert.Equal("draft_updates_disabled", result.StructuredContent.Value.GetProperty("code").GetString());
        Assert.Equal(0, factory.Graph.Calls);
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("timeout")]
    [InlineData("parse")]
    public async Task FailuresAfterDispatchRemainUnknownAndAreNeverRetried(string failure)
    {
        using var factory = new McpTestFactory();
        factory.Drafts.Handler = (_, state, _) =>
        {
            state.MarkDispatched("known-draft-id");
            throw failure switch
            {
                "timeout" => new OperationCanceledException(),
                "parse" => new GraphOperationException("invalid_upstream_response", "Invalid response."),
                _ => new HttpRequestException("sensitive write details")
            };
        };
        await using var client = await factory.ConnectAsync();
        var result = await client.CallToolAsync(Request("mail_create_draft", "{\"subject\":\"s\",\"bodyText\":\"body\"}"), TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        var receipt = result.StructuredContent!.Value;
        Assert.Equal("unknown", receipt.GetProperty("outcome").GetString());
        Assert.Equal("write_outcome_unknown", receipt.GetProperty("code").GetString());
        Assert.False(receipt.GetProperty("retrySafe").GetBoolean());
        Assert.True(receipt.GetProperty("reconciliationRequired").GetBoolean());
        Assert.Equal("known-draft-id", receipt.GetProperty("messageId").GetString());
        Assert.Equal(1, factory.Drafts.Calls);
        Assert.DoesNotContain("sensitive", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("readback")]
    [InlineData("projection")]
    public async Task ConfirmedCommitSurvivesLaterFailuresWithoutSuggestingReplay(string failure)
    {
        using var factory = new McpTestFactory();
        factory.Drafts.Handler = (_, state, _) =>
        {
            state.MarkDispatched();
            state.SetMessageId("created-draft-id");
            state.MarkCommitted();
            throw failure switch
            {
                "timeout" => new OperationCanceledException(),
                "readback" => new GraphOperationException("not_found", "Readback was unavailable."),
                _ => new InvalidOperationException("sensitive body and token")
            };
        };
        await using var client = await factory.ConnectAsync();
        var result = await client.CallToolAsync(Request("mail_create_draft", "{\"subject\":\"s\",\"bodyText\":\"body\"}"), TestContext.Current.CancellationToken);
        Assert.False(result.IsError);
        var receipt = result.StructuredContent!.Value;
        Assert.Equal("committed", receipt.GetProperty("outcome").GetString());
        Assert.Equal("committed_details_unavailable", receipt.GetProperty("code").GetString());
        Assert.Equal("created-draft-id", receipt.GetProperty("messageId").GetString());
        Assert.False(receipt.GetProperty("retrySafe").GetBoolean());
        Assert.True(receipt.GetProperty("reconciliationRequired").GetBoolean());
        Assert.Equal(1, factory.Drafts.Calls);
    }

    [Fact]
    public async Task OversizedProjectionDropsDetailsWhilePreservingCommittedReceipt()
    {
        using var factory = new McpTestFactory();
        factory.Drafts.Handler = (_, state, _) =>
        {
            state.MarkDispatched();
            state.MarkCommitted();
            return Task.FromResult(FakeDraftServices.SavedDraft with { Subject = new string('x', 600_000) });
        };
        await using var client = await factory.ConnectAsync();
        var result = await client.CallToolAsync(Request("mail_create_draft", "{\"subject\":\"s\",\"bodyText\":\"body\"}"), TestContext.Current.CancellationToken);
        Assert.False(result.IsError);
        Assert.Equal("committed", result.StructuredContent!.Value.GetProperty("outcome").GetString());
        Assert.Equal("draft-id", result.StructuredContent.Value.GetProperty("messageId").GetString());
        Assert.Equal(JsonValueKind.Null, result.StructuredContent.Value.GetProperty("draft").ValueKind);
        Assert.True(result.StructuredContent.Value.GetProperty("reconciliationRequired").GetBoolean());
    }

    [Fact]
    public async Task DeadlineAfterCommitCannotReplaceSuccessWithRetryableTimeout()
    {
        using var factory = new McpTestFactory();
        factory.ConfigurationOverrides["Graph:ToolTimeoutSeconds"] = "1";
        factory.Drafts.Handler = async (_, state, cancellationToken) =>
        {
            state.MarkDispatched();
            state.MarkCommitted();
            // Simulate a committed response whose final details complete at the deadline.
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { return FakeDraftServices.SavedDraft; }
            throw new InvalidOperationException("Unreachable.");
        };
        await using var client = await factory.ConnectAsync();
        var result = await client.CallToolAsync(Request("mail_create_draft", "{\"subject\":\"s\",\"bodyText\":\"body\"}"), TestContext.Current.CancellationToken);
        Assert.False(result.IsError);
        Assert.Equal("committed", result.StructuredContent!.Value.GetProperty("outcome").GetString());
        Assert.False(result.StructuredContent.Value.GetProperty("retrySafe").GetBoolean());
    }

    [Fact]
    public async Task StateIsFreshPerInvocationAndLogsExcludeAuthoredContentAndVersion()
    {
        using var factory = new McpTestFactory();
        await using var client = await factory.ConnectAsync();
        var create = await client.CallToolAsync(Request("mail_create_draft", "{\"subject\":\"sensitive-subject\",\"bodyText\":\"sensitive-body\"}"), TestContext.Current.CancellationToken);
        Assert.Equal("committed", create.StructuredContent!.Value.GetProperty("outcome").GetString());
        factory.Drafts.Handler = (_, _, _) => throw GraphOperationException.Invalid("The draft version is invalid.");
        var update = await client.CallToolAsync(Request("mail_update_draft",
            "{\"messageId\":\"sensitive-id\",\"editVersion\":\"sensitive-version\",\"bodyText\":\"sensitive-body\"}"), TestContext.Current.CancellationToken);
        Assert.Equal("not_applied", update.StructuredContent!.Value.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, update.StructuredContent.Value.GetProperty("messageId").ValueKind);
        Assert.DoesNotContain("sensitive-", string.Join('\n', factory.Logs));
    }

    private static CallToolRequestParams Request(string name, string json) => new()
    { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) };
    private static string[] Addresses(int count) => Enumerable.Range(1, count).Select(index => $"person{index}@example.com").ToArray();
}
