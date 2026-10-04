using System.Net;
using System.Net.Http.Headers;
using GraphMcp.Configuration;
using GraphMcp.Infrastructure;

namespace GraphMcp.Tests.Graph;

public sealed class GraphResilienceTests
{
    [Theory]
    [InlineData(401, "authentication_required")]
    [InlineData(403, "access_denied")]
    [InlineData(404, "not_found")]
    [InlineData(500, "upstream_unavailable")]
    public async Task MapsGraphErrorsWithoutLeakingResponseBodies(int status, string code)
    {
        using var fixture = new GraphTestFixture((_, _) => Task.FromResult(GraphTestFixture.Json("{\"error\":{\"message\":\"private mailbox body and secret-token\"}}", (HttpStatusCode)status)));
        var error = await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Client.GetAsync("me", TestContext.Current.CancellationToken));
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("private", error.Message);
        Assert.DoesNotContain("secret-token", error.Message);
    }

    [Fact]
    public async Task UnauthorizedRefreshesOnceAndSafeRetryDoesNotRefreshAgain()
    {
        var calls = 0;
        using var fixture = new GraphTestFixture((_, _) =>
        {
            var response = GraphTestFixture.Json("{}", ++calls switch { 1 => HttpStatusCode.Unauthorized, 2 => HttpStatusCode.ServiceUnavailable, _ => HttpStatusCode.OK });
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        }, new() { MaxRetries = 2 });
        using var result = await fixture.Client.GetAsync("me", TestContext.Current.CancellationToken);
        Assert.Equal(new[] { false, true, false }, fixture.Credentials.Refreshes);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ThrottleRetriesOnlyAfterSpecifiedDelay()
    {
        var calls = 0;
        var times = new List<DateTimeOffset>();
        using var fixture = new GraphTestFixture((_, _) =>
        {
            times.Add(DateTimeOffset.UtcNow);
            var response = GraphTestFixture.Json("{}", ++calls == 1 ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return Task.FromResult(response);
        }, new() { MaxRetries = 2 });
        using var result = await fixture.Client.GetAsync("me", TestContext.Current.CancellationToken);
        Assert.True(times[1] - times[0] >= TimeSpan.FromMilliseconds(950));
    }

    [Fact]
    public async Task ThrottleBeyondBudgetIsReturnedWithoutEarlyRetry()
    {
        using var fixture = new GraphTestFixture((_, _) =>
        {
            var response = GraphTestFixture.Json("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return Task.FromResult(response);
        }, new() { MaxRetries = 2 });
        var error = await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Client.GetAsync("me", TestContext.Current.CancellationToken));
        Assert.Equal("throttled", error.Code);
        Assert.Equal(120, error.RetryAfterSeconds);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task TimeoutIsSafeAndCallerCancellationIsPropagated()
    {
        using var fixture = new GraphTestFixture(async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return GraphTestFixture.Json("{}"); }, new() { MaxRetries = 0, RequestTimeoutSeconds = 1 });
        var error = await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Client.GetAsync("me", TestContext.Current.CancellationToken));
        Assert.Equal("upstream_timeout", error.Code);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.GetAsync("me", cancellation.Token));
    }

    [Theory]
    [InlineData("https://attacker.example/v1.0/me")]
    [InlineData("https://graph.microsoft.com/beta/me")]
    [InlineData("me/drive")]
    [InlineData("me/mailboxSettings")]
    [InlineData("users/another-user/messages")]
    [InlineData("me/calendar")]
    public async Task DisallowedOriginsAndRoutesFailBeforeHttp(string url)
    {
        using var fixture = new GraphTestFixture((_, _) => throw new InvalidOperationException());
        await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Client.GetAsync(url, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task ResponseByteCapIsEnforcedBeforeJsonParsing()
    {
        using var fixture = new GraphTestFixture((_, _) => Task.FromResult(GraphTestFixture.Json("{\"value\":\"too large\"}")), new() { MaxJsonBytes = 4 });
        var error = await Assert.ThrowsAsync<GraphOperationException>(() => fixture.Client.GetAsync("me", TestContext.Current.CancellationToken));
        Assert.Equal("response_too_large", error.Code);
    }
}
