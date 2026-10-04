using System.Net;
using System.Text.Json;
using GraphMcp.Auth;
using GraphMcp.Infrastructure;
using GraphMcp.Models;
using ModelContextProtocol.Protocol;

namespace GraphMcp.Tests.Mcp;

public sealed class McpContractTests
{
    [Fact]
    public async Task StreamableHttpDiscoversExactlyApprovedToolsWithStrictSchemas()
    {
        using var factory = new McpTestFactory();
        await using var client = await factory.ConnectAsync();
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        string[] expected = ["account_me", "mail_list", "mail_search", "mail_get", "mail_get_attachment",
            "calendar_list", "calendar_events", "calendar_get_event", "calendar_availability",
            "mail_create_draft", "mail_create_reply_draft", "mail_create_reply_all_draft", "mail_create_forward_draft", "mail_update_draft"];
        Assert.Equal(expected.Order(), tools.Select(tool => tool.Name).Order());
        foreach (var tool in tools)
        {
            var descriptor = tool.ProtocolTool;
            Assert.False(string.IsNullOrWhiteSpace(descriptor.Description));
            Assert.NotNull(descriptor.OutputSchema);
            Assert.False(descriptor.InputSchema.GetProperty("additionalProperties").GetBoolean());
            var write = descriptor.Name.StartsWith("mail_create_", StringComparison.Ordinal) || descriptor.Name == "mail_update_draft";
            Assert.Equal(!write, descriptor.Annotations!.ReadOnlyHint);
            Assert.Equal(descriptor.Name == "mail_update_draft", descriptor.Annotations.DestructiveHint);
            Assert.Equal(!write, descriptor.Annotations.IdempotentHint);
            Assert.True(descriptor.Annotations.OpenWorldHint);
            Assert.False(descriptor.InputSchema.GetProperty("properties").TryGetProperty("context", out _));
        }
        var attachment = tools.Single(tool => tool.Name == "mail_get_attachment");
        var representations = attachment.ProtocolTool.InputSchema.GetProperty("properties").GetProperty("representation").GetProperty("enum");
        Assert.Equal(["metadata", "text"], representations.EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task AccountReturnsStructuredProjection()
    {
        using var factory = new McpTestFactory();
        await using var client = await factory.ConnectAsync();
        var result = await client.CallToolAsync("account_me", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(result.IsError);
        Assert.Equal("account-id", result.StructuredContent!.Value.GetProperty("id").GetString());
        Assert.Equal(1, factory.Graph.Calls);
    }

    [Theory]
    [InlineData("account_me", "{\"url\":\"https://graph.microsoft.com/v1.0/users\"}")]
    [InlineData("mail_list", "{\"pageSize\":101}")]
    [InlineData("mail_list", "{\"pageSize\":0}")]
    [InlineData("mail_list", "{\"folder\":\"custom-id\"}")]
    [InlineData("mail_list", "{\"receivedAfter\":\"2026-10-01T00:00:00\"}")]
    [InlineData("mail_search", "{\"query\":\"x\",\"cursor\":\"escape\"}")]
    [InlineData("mail_get", "{\"messageId\":\"\"}")]
    [InlineData("mail_get", "{\"messageId\":\"m\",\"maxBodyChars\":40001}")]
    [InlineData("mail_get_attachment", "{\"messageId\":\"m\",\"attachmentId\":\"a\",\"representation\":\"base64\"}")]
    [InlineData("calendar_events", "{\"start\":\"2026-10-01T00:00:00Z\",\"end\":\"2026-11-02T00:00:00Z\"}")]
    [InlineData("calendar_events", "{\"start\":\"2026-10-02T00:00:00Z\",\"end\":\"2026-10-01T00:00:00Z\"}")]
    [InlineData("calendar_events", "{\"start\":\"2026-10-01\",\"end\":\"2026-10-02T00:00:00Z\"}")]
    [InlineData("calendar_get_event", "{\"eventId\":\"e\",\"timeZone\":\"Bogus/Zone\"}")]
    [InlineData("calendar_availability", "{\"start\":\"2026-10-01T00:00:00Z\",\"end\":\"2026-10-09T00:00:00Z\"}")]
    [InlineData("calendar_availability", "{\"start\":\"2026-10-01T00:00:00Z\",\"end\":\"2026-10-02T00:00:00Z\",\"intervalMinutes\":20}")]
    [InlineData("calendar_availability", "{\"start\":\"2026-10-01T00:00:00Z\",\"end\":\"2026-10-02T00:00:00Z\",\"additionalSchedules\":[\"Person <p@example.com>\"]}")]
    public async Task InvalidInputsNeverReachGraph(string name, string json)
    {
        using var factory = new McpTestFactory();
        await using var client = await factory.ConnectAsync();
        var result = await client.CallToolAsync(new CallToolRequestParams
        { Name = name, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) }, TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("invalid_input", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        Assert.Equal(0, factory.Graph.Calls);
    }

    [Fact]
    public async Task PaginationRemainsAnExplicitSeparateInvocation()
    {
        using var factory = new McpTestFactory();
        await using var client = await factory.ConnectAsync();
        var first = await client.CallToolAsync("mail_list", cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(first.IsError);
        Assert.Equal(20, Assert.IsType<MailListRequest>(factory.Graph.LastRequest).PageSize);
        Assert.Equal(1, factory.Graph.Calls);
        var cursor = first.StructuredContent!.Value.GetProperty("nextCursor").GetString();
        var second = await client.CallToolAsync("mail_list", new Dictionary<string, object?> { ["cursor"] = cursor }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(second.IsError);
        Assert.Equal(cursor, Assert.IsType<MailListRequest>(factory.Graph.LastRequest).Cursor);
        Assert.Equal(2, factory.Graph.Calls);
    }

    [Fact]
    public async Task AttachmentDefaultsToMetadataAndTextRequiresExplicitRequest()
    {
        using var factory = new McpTestFactory();
        await using var client = await factory.ConnectAsync();
        var arguments = new Dictionary<string, object?> { ["messageId"] = "message-id", ["attachmentId"] = "attachment-id" };
        var metadata = await client.CallToolAsync("mail_get_attachment", arguments, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("metadata", Assert.IsType<AttachmentRequest>(factory.Graph.LastRequest).Representation);
        Assert.Equal(JsonValueKind.Null, metadata.StructuredContent!.Value.GetProperty("content").ValueKind);
        arguments["representation"] = "text";
        var text = await client.CallToolAsync("mail_get_attachment", arguments, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Synthetic attachment text", text.StructuredContent!.Value.GetProperty("content").GetProperty("text").GetString());
    }

    [Fact]
    public async Task AvailabilityAcceptsNineAdditionalAddressesAndRejectsTen()
    {
        using var factory = new McpTestFactory();
        await using var client = await factory.ConnectAsync();
        var args = new Dictionary<string, object?>
        {
            ["start"] = "2026-10-01T00:00:00-04:00", ["end"] = "2026-10-01T02:00:00-04:00",
            ["additionalSchedules"] = Enumerable.Range(1, 9).Select(n => $"person{n}@example.com").ToArray(),
            ["timeZone"] = "America/New_York"
        };
        Assert.False((await client.CallToolAsync("calendar_availability", args, cancellationToken: TestContext.Current.CancellationToken)).IsError);
        var request = Assert.IsType<AvailabilityRequest>(factory.Graph.LastRequest);
        Assert.Equal(9, request.AdditionalSchedules.Length);
        Assert.Equal(TimeSpan.FromHours(-4), request.Start.Offset);
        args["additionalSchedules"] = Enumerable.Range(1, 10).Select(n => $"person{n}@example.com").ToArray();
        Assert.True((await client.CallToolAsync("calendar_availability", args, cancellationToken: TestContext.Current.CancellationToken)).IsError);
        Assert.Equal(1, factory.Graph.Calls);
    }

    [Fact]
    public async Task SafeErrorsPreserveRetryHintsAndHideUnexpectedExceptionDetails()
    {
        using var factory = new McpTestFactory();
        await using var client = await factory.ConnectAsync();
        factory.Graph.Failure = new GraphOperationException("throttled", "Microsoft Graph is throttling requests.", 60);
        var throttled = await client.CallToolAsync("account_me", cancellationToken: TestContext.Current.CancellationToken);
        using var error = JsonDocument.Parse(Assert.IsType<TextContentBlock>(Assert.Single(throttled.Content)).Text);
        Assert.True(throttled.IsError);
        Assert.True(error.RootElement.GetProperty("retryable").GetBoolean());
        Assert.Equal(60, error.RootElement.GetProperty("retryAfterSeconds").GetInt32());
        factory.Graph.Failure = new InvalidOperationException("synthetic-sensitive-token-and-body");
        var unexpected = await client.CallToolAsync("account_me", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(unexpected.IsError);
        Assert.DoesNotContain("synthetic-sensitive", Assert.IsType<TextContentBlock>(Assert.Single(unexpected.Content)).Text);
        factory.Graph.Failure = new GraphAuthenticationException("authentication_required");
        var auth = await client.CallToolAsync("account_me", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("authentication_required", Assert.IsType<TextContentBlock>(Assert.Single(auth.Content)).Text);
    }

    [Fact]
    public async Task OversizedToolResponseIsRejectedWithoutContent()
    {
        using var factory = new McpTestFactory();
        factory.Graph.AccountDisplayName = new string('x', 600_000);
        await using var client = await factory.ConnectAsync();
        var result = await client.CallToolAsync("account_me", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Null(result.StructuredContent);
        Assert.Contains("response_too_large", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
    }

    [Fact]
    public async Task ListenersAndOriginsRemainSeparated()
    {
        using var factory = new McpTestFactory();
        using var http = factory.CreateClient(new() { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/operator/signin-oidc?code=secret", TestContext.Current.CancellationToken)).StatusCode);
        http.DefaultRequestHeaders.Add("X-Test-Local-Port", "8081");
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/mcp", TestContext.Current.CancellationToken)).StatusCode);
        http.DefaultRequestHeaders.Remove("X-Test-Local-Port");
        http.DefaultRequestHeaders.Add("Origin", "https://unapproved.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await http.GetAsync("/mcp", TestContext.Current.CancellationToken)).StatusCode);
        http.DefaultRequestHeaders.Remove("Origin");
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/health/live", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task OmittedLimitsRespectSmallerDeploymentLimits()
    {
        using var factory = new McpTestFactory();
        factory.ConfigurationOverrides["Graph:MaxPageSize"] = "10";
        factory.ConfigurationOverrides["Graph:MaxBodyChars"] = "1000";
        await using var client = await factory.ConnectAsync();
        Assert.False((await client.CallToolAsync("mail_list", cancellationToken: TestContext.Current.CancellationToken)).IsError);
        Assert.Equal(10, Assert.IsType<MailListRequest>(factory.Graph.LastRequest).PageSize);
        Assert.False((await client.CallToolAsync("mail_get", new Dictionary<string, object?> { ["messageId"] = "m" },
            cancellationToken: TestContext.Current.CancellationToken)).IsError);
        Assert.Equal(1000, Assert.IsType<MailGetRequest>(factory.Graph.LastRequest).MaxBodyChars);
        Assert.True((await client.CallToolAsync("mail_list", new Dictionary<string, object?> { ["pageSize"] = 11 },
            cancellationToken: TestContext.Current.CancellationToken)).IsError);
        Assert.True((await client.CallToolAsync("mail_get", new Dictionary<string, object?> { ["messageId"] = "m", ["maxBodyChars"] = 1001 },
            cancellationToken: TestContext.Current.CancellationToken)).IsError);
        Assert.Equal(2, factory.Graph.Calls);
    }

    [Fact]
    public async Task WholeToolDeadlineCancelsTheService()
    {
        using var factory = new McpTestFactory();
        factory.ConfigurationOverrides["Graph:ToolTimeoutSeconds"] = "1";
        var serviceCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Graph.AccountHandler = async cancellationToken =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { serviceCancelled.SetResult(); throw; }
            throw new InvalidOperationException("Unreachable.");
        };
        await using var client = await factory.ConnectAsync();
        var result = await client.CallToolAsync("account_me", cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Contains("upstream_timeout", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
        await serviceCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CallerCancellationPropagatesToService()
    {
        using var factory = new McpTestFactory();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.Graph.AccountHandler = async cancellationToken =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) { stopped.SetResult(); throw; }
            throw new InvalidOperationException("Unreachable.");
        };
        await using var client = await factory.ConnectAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var call = client.CallToolAsync("account_me", cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        // A stateless HTTP disconnect may surface as an ended MCP response instead of an OCE.
        var failure = await Record.ExceptionAsync(async () => await call);
        Assert.True(failure is OperationCanceledException or ModelContextProtocol.McpException);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OperatorRequiresHttpsAndTrustsOnlyConfiguredProxy()
    {
        using var factory = new McpTestFactory();
        factory.ConfigurationOverrides["Transport:KnownProxies:0"] = "127.0.0.1";
        using var https = factory.CreateClient(new() { BaseAddress = new Uri("https://operator.test"), AllowAutoRedirect = false });
        https.DefaultRequestHeaders.Add("X-Test-Local-Port", "8081");
        Assert.Equal(HttpStatusCode.OK, (await https.GetAsync("/operator", TestContext.Current.CancellationToken)).StatusCode);

        using var http = factory.CreateClient(new() { BaseAddress = new Uri("http://operator.test"), AllowAutoRedirect = false });
        http.DefaultRequestHeaders.Add("X-Test-Local-Port", "8081");
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/operator", TestContext.Current.CancellationToken)).StatusCode);
        http.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/operator", TestContext.Current.CancellationToken)).StatusCode);
        http.DefaultRequestHeaders.Add("X-Test-Unknown-Proxy", "true");
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/operator", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("mail_search", "{\"query\":\"subject:invoice\"}")]
    [InlineData("calendar_list", "{}")]
    [InlineData("calendar_events", "{\"start\":\"2026-10-01T00:00:00Z\",\"end\":\"2026-10-02T00:00:00Z\"}")]
    [InlineData("calendar_get_event", "{\"eventId\":\"event-id\"}")]
    public async Task RemainingToolsMapToTheirExplicitServices(string tool, string json)
    {
        using var factory = new McpTestFactory();
        await using var client = await factory.ConnectAsync();
        var result = await client.CallToolAsync(new CallToolRequestParams
        { Name = tool, Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) }, TestContext.Current.CancellationToken);
        Assert.False(result.IsError);
        Assert.NotNull(result.StructuredContent);
        Assert.Equal(1, factory.Graph.Calls);
    }

    [Fact]
    public async Task ApplicationLogsContainOperationFactsWithoutInputsResultsOrExceptions()
    {
        using var factory = new McpTestFactory();
        factory.Graph.AccountDisplayName = "sensitive-output-marker";
        await using var client = await factory.ConnectAsync();
        await client.CallToolAsync("account_me", cancellationToken: TestContext.Current.CancellationToken);
        await client.CallToolAsync("mail_search", new Dictionary<string, object?> { ["query"] = "sensitive-query-marker" },
            cancellationToken: TestContext.Current.CancellationToken);
        factory.Graph.Failure = new InvalidOperationException("sensitive-exception-marker Authorization: Bearer synthetic-token");
        await client.CallToolAsync("account_me", cancellationToken: TestContext.Current.CancellationToken);
        var logs = string.Join('\n', factory.Logs);
        Assert.Contains("Tool account_me", logs);
        Assert.Contains("completed with", logs);
        Assert.DoesNotContain("sensitive-", logs);
        Assert.DoesNotContain("Authorization", logs);
        Assert.DoesNotContain("synthetic-token", logs);
    }
}
