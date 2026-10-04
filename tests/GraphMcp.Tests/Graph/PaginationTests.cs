using System.Text.Json;
using GraphMcp.Infrastructure;
using GraphMcp.Models;
using Microsoft.AspNetCore.DataProtection;

namespace GraphMcp.Tests.Graph;

public sealed class PaginationTests
{
    [Fact]
    public async Task ExpiredCursorCannotMakeAnotherGraphRequest()
    {
        using var fixture = new GraphTestFixture((request, _) => Task.FromResult(GraphTestFixture.Json(JsonSerializer.Serialize(new Dictionary<string, object> { ["value"] = new[] { new { id = "m1" } }, ["@odata.nextLink"] = request.RequestUri!.AbsoluteUri + "&%24skip=1" }))));
        var first = await fixture.Mail.ListAsync(new() { PageSize = 1 }, TestContext.Current.CancellationToken);
        var protector = fixture.Protection.CreateProtector("GraphMcp.Pagination.v1");
        var state = System.Text.Json.Nodes.JsonNode.Parse(protector.Unprotect(first.NextCursor!))!;
        state["expires"] = DateTimeOffset.UtcNow.AddMinutes(-1);
        var expired = protector.Protect(state.ToJsonString());
        await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Mail.ListAsync(new() { PageSize = 1, Cursor = expired }, TestContext.Current.CancellationToken));
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task OversizedProtectedCursorIsNotReturned()
    {
        using var fixture = new GraphTestFixture((request, _) => Task.FromResult(GraphTestFixture.Json(JsonSerializer.Serialize(new Dictionary<string, object> { ["value"] = new[] { new { id = "m1" } }, ["@odata.nextLink"] = request.RequestUri!.AbsoluteUri + "&%24skiptoken=" + new string('a', 12_000) }))));
        var error = await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Mail.ListAsync(new() { PageSize = 1 }, TestContext.Current.CancellationToken));
        Assert.Equal("pagination_unavailable", error.Code);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task ConnectionChangeDuringQueryCannotMintNewCursor()
    {
        GraphTestFixture? fixture = null;
        fixture = new GraphTestFixture((request, _) =>
        {
            fixture!.Credentials.ConnectionGeneration = "new-generation";
            return Task.FromResult(GraphTestFixture.Json(JsonSerializer.Serialize(new Dictionary<string, object> { ["value"] = new[] { new { id = "m1" } }, ["@odata.nextLink"] = request.RequestUri!.AbsoluteUri + "&%24skip=1" })));
        });
        using (fixture)
        {
            var error = await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Mail.ListAsync(new() { PageSize = 1 }, TestContext.Current.CancellationToken));
            Assert.Equal("invalid_input", error.Code);
        }
    }

    [Fact]
    public async Task CursorIsOpaqueAndBoundToArgumentsAndGeneration()
    {
        var calls = 0;
        using var fixture = new GraphTestFixture((request, _) => Task.FromResult(GraphTestFixture.Json(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["value"] = new[] { new { id = "m" + ++calls } },
            ["@odata.nextLink"] = request.RequestUri!.AbsoluteUri.Split("&%24skip=", StringSplitOptions.None)[0] + "&%24skip=" + calls
        }))));
        var first = await fixture.Mail.ListAsync(new() { PageSize = 1 }, TestContext.Current.CancellationToken);
        Assert.NotNull(first.NextCursor);
        Assert.DoesNotContain("graph.microsoft.com", first.NextCursor);
        var next = await fixture.Mail.ListAsync(new() { PageSize = 1, Cursor = first.NextCursor }, TestContext.Current.CancellationToken);
        Assert.Equal("m2", next.Items[0].Id);
        await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Mail.ListAsync(new() { PageSize = 2, Cursor = first.NextCursor }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Mail.ListAsync(new() { PageSize = 1, Cursor = first.NextCursor + "tampered" }, TestContext.Current.CancellationToken));
        fixture.Credentials.ConnectionGeneration = "reconnected";
        await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Mail.ListAsync(new() { PageSize = 1, Cursor = first.NextCursor }, TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("https://attacker.example/v1.0/me/mailFolders/inbox/messages")]
    [InlineData("https://graph.microsoft.com/v1.0/me/messages")]
    public async Task RejectsUnexpectedNextLinkOriginOrRoute(string next)
    {
        using var fixture = new GraphTestFixture((_, _) => Task.FromResult(GraphTestFixture.Json(JsonSerializer.Serialize(new Dictionary<string, object> { ["value"] = Array.Empty<object>(), ["@odata.nextLink"] = next }))));
        var error = await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Mail.ListAsync(new(), TestContext.Current.CancellationToken));
        Assert.Equal("invalid_upstream_response", error.Code);
    }

    [Fact]
    public async Task StopsAfterTenPagesWithoutAutoFetching()
    {
        var calls = 0;
        string? initial = null;
        using var fixture = new GraphTestFixture((request, _) =>
        {
            initial ??= request.RequestUri!.AbsoluteUri;
            return Task.FromResult(GraphTestFixture.Json(JsonSerializer.Serialize(new Dictionary<string, object> { ["value"] = new[] { new { id = "m" + ++calls } }, ["@odata.nextLink"] = initial + "&%24skip=" + calls })));
        });
        string? cursor = null;
        PageResult<MailSummaryDto>? result = null;
        for (var page = 1; page <= 10; page++)
        {
            result = await fixture.Mail.ListAsync(new() { PageSize = 1, Cursor = cursor }, TestContext.Current.CancellationToken);
            cursor = result.NextCursor;
            Assert.Equal(page, calls);
        }
        Assert.True(result!.Truncated);
        Assert.Null(cursor);
    }
}
