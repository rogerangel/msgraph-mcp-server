using System.Net;
using System.Net.Http.Headers;
using GraphMcp.Infrastructure;

namespace GraphMcp.Tests.Graph;

public sealed class DraftHttpTests
{
    private static object Payload => new { subject = "Draft", body = new { contentType = "Text", content = "Private content" } };

    [Theory]
    [InlineData(400, "invalid_graph_request")]
    [InlineData(401, "authentication_required")]
    [InlineData(403, "access_denied")]
    [InlineData(404, "not_found")]
    [InlineData(409, "draft_conflict")]
    [InlineData(412, "draft_conflict")]
    public async Task Rejected_writes_are_not_replayed_and_errors_are_safe(int status, string code)
    {
        using var fixture = new GraphTestFixture((_, _) => Task.FromResult(GraphTestFixture.Json(
            "{\"error\":\"secret-token and private mailbox content\"}", (HttpStatusCode)status)), new() { MaxRetries = 2 });
        var state = new DraftWriteState();
        var error = await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Client.CreateDraftAsync(Payload, state, TestContext.Current.CancellationToken));
        Assert.Equal(code, error.Code);
        var receipt = state.ToReceipt(error: error);
        Assert.Equal("not_applied", receipt.Outcome);
        Assert.True(receipt.RetrySafe);
        Assert.DoesNotContain("secret", receipt.Message);
        Assert.Single(fixture.Handler.Requests);
        Assert.Equal(new[] { false }, fixture.Credentials.Refreshes);
    }

    [Fact]
    public async Task Throttled_write_returns_delay_without_replay()
    {
        using var fixture = new GraphTestFixture((_, _) =>
        {
            var response = GraphTestFixture.Json("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(47));
            return Task.FromResult(response);
        }, new() { MaxRetries = 2 });
        var state = new DraftWriteState();
        var error = await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Client.CreateDraftAsync(Payload, state, TestContext.Current.CancellationToken));
        Assert.Equal(47, state.ToReceipt(error: error).RetryAfterSeconds);
        Assert.Equal("not_applied", state.Outcome);
        Assert.Single(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task Server_failure_is_unknown_and_not_retry_safe(int status)
    {
        using var fixture = new GraphTestFixture((_, _) => Task.FromResult(GraphTestFixture.Json("{}", (HttpStatusCode)status)), new() { MaxRetries = 2 });
        var state = new DraftWriteState();
        await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Client.CreateDraftAsync(Payload, state, TestContext.Current.CancellationToken));
        AssertUnknown(state);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Lost_connection_after_dispatch_is_unknown_without_replay()
    {
        using var fixture = new GraphTestFixture((_, _) => throw new HttpRequestException("secret detail"), new() { MaxRetries = 2 });
        var state = new DraftWriteState();
        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.CreateDraftAsync(Payload, state, TestContext.Current.CancellationToken));
        AssertUnknown(state);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Timeout_after_dispatch_is_unknown_without_replay()
    {
        using var fixture = new GraphTestFixture(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return GraphTestFixture.Json("{}");
        }, new() { MaxRetries = 2, RequestTimeoutSeconds = 1 });
        var state = new DraftWriteState();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.CreateDraftAsync(Payload, state, TestContext.Current.CancellationToken));
        AssertUnknown(state);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Cancellation_before_dispatch_proves_write_not_applied()
    {
        using var fixture = new GraphTestFixture((_, _) => throw new InvalidOperationException("Must not send"));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var state = new DraftWriteState();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.CreateDraftAsync(Payload, state, cancellation.Token));
        Assert.Equal("not_applied", state.Outcome);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData("not json", 2097152)]
    [InlineData("{\"id\":\"too-large\"}", 4)]
    public async Task Confirmed_commit_survives_invalid_or_oversized_response(string responseBody, int maxBytes)
    {
        using var fixture = new GraphTestFixture((_, _) => Task.FromResult(GraphTestFixture.Json(responseBody, HttpStatusCode.Created)), new() { MaxJsonBytes = maxBytes });
        var state = new DraftWriteState();
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.CreateDraftAsync(Payload, state, TestContext.Current.CancellationToken));
        AssertCommittedWithoutDetails(state);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Confirmed_commit_survives_response_stream_failure()
    {
        using var fixture = new GraphTestFixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new BrokenContent() }));
        var state = new DraftWriteState();
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.CreateDraftAsync(Payload, state, TestContext.Current.CancellationToken));
        AssertCommittedWithoutDetails(state);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Update_sends_exact_etag_and_retains_known_id_when_details_fail()
    {
        using var fixture = new GraphTestFixture((request, _) =>
        {
            Assert.Equal(HttpMethod.Patch, request.Method);
            Assert.Equal("/v1.0/me/messages/draft-id", request.RequestUri!.AbsolutePath);
            Assert.Equal("W/\"version-one\"", Assert.Single(request.Headers.IfMatch).ToString());
            return Task.FromResult(GraphTestFixture.Json("invalid response"));
        });
        var state = new DraftWriteState();
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.UpdateDraftAsync("draft-id", Payload, "W/\"version-one\"", state, TestContext.Current.CancellationToken));
        AssertCommittedWithoutDetails(state);
        Assert.Equal("draft-id", state.ToReceipt().MessageId);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("")]
    [InlineData("not-an-etag")]
    [InlineData("\"etag\"\r\nX-Evil: injected")]
    public async Task Invalid_conditional_headers_fail_before_dispatch(string etag)
    {
        using var fixture = new GraphTestFixture((_, _) => throw new InvalidOperationException("Must not send"));
        var state = new DraftWriteState();
        await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Client.UpdateDraftAsync("draft-id", Payload, etag, state, TestContext.Current.CancellationToken));
        Assert.Equal("not_applied", state.Outcome);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Reply_creation_never_mistakes_source_id_for_created_draft()
    {
        using var fixture = new GraphTestFixture((request, _) =>
        {
            Assert.Equal("/v1.0/me/messages/source-id/createReply", request.RequestUri!.AbsolutePath);
            return Task.FromResult(GraphTestFixture.Json("broken", HttpStatusCode.Created));
        });
        var state = new DraftWriteState();
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Client.CreateReplyDraftAsync("source-id", Payload, state, TestContext.Current.CancellationToken));
        AssertCommittedWithoutDetails(state);
        Assert.Null(state.ToReceipt().MessageId);
    }

    [Fact]
    public async Task Update_rejects_changed_connection_before_dispatch()
    {
        using var fixture = new GraphTestFixture((_, _) => throw new InvalidOperationException("Must not send"));
        var state = new DraftWriteState();
        var error = await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Client.UpdateDraftAsync("draft-id", Payload,
            "\"etag\"", state, TestContext.Current.CancellationToken, "old-connection"));
        Assert.Equal("invalid_edit_version", error.Code);
        Assert.Equal("not_applied", state.Outcome);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Update_receipt_never_adopts_a_different_response_id()
    {
        using var fixture = new GraphTestFixture((_, _) => Task.FromResult(GraphTestFixture.Json("{\"id\":\"another-message\"}")));
        var state = new DraftWriteState();
        using var result = await fixture.Client.UpdateDraftAsync("draft-id", Payload, "\"etag\"", state, TestContext.Current.CancellationToken);
        Assert.Equal("draft-id", state.ToReceipt().MessageId);
        Assert.Equal("committed", state.Outcome);
    }

    private static void AssertUnknown(DraftWriteState state)
    {
        var receipt = state.ToReceipt();
        Assert.Equal("unknown", receipt.Outcome);
        Assert.Equal("write_outcome_unknown", receipt.Code);
        Assert.False(receipt.RetrySafe);
        Assert.True(receipt.ReconciliationRequired);
        Assert.Contains("NOT retry-safe", receipt.Message);
        Assert.DoesNotContain("secret", receipt.Message);
    }

    private static void AssertCommittedWithoutDetails(DraftWriteState state)
    {
        var receipt = state.ToReceipt();
        Assert.Equal("committed", receipt.Outcome);
        Assert.Equal("committed_details_unavailable", receipt.Code);
        Assert.False(receipt.RetrySafe);
        Assert.True(receipt.ReconciliationRequired);
        Assert.Null(receipt.Draft);
    }

    private sealed class BrokenContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new IOException("private upstream detail");
    }
}
