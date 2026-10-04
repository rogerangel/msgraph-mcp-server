using GraphMcp.Auth;
using GraphMcp.Health;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GraphMcp.Tests.Health;

public sealed class AuthenticationReadinessTests
{
    [Fact]
    public async Task Silent_acquisition_is_cached_across_dependency_scopes()
    {
        var shared = new SharedCredentials();
        var services = new ServiceCollection();
        services.AddSingleton<AuthenticationReadinessState>();
        services.AddScoped<IGraphCredentialProvider>(_ => new Credentials(shared));
        services.AddScoped<AuthenticationReadinessCheck>();
        using var provider = services.BuildServiceProvider();
        using (var first = provider.CreateScope())
        {
            var result = await first.ServiceProvider.GetRequiredService<AuthenticationReadinessCheck>()
                .CheckHealthAsync(new(), TestContext.Current.CancellationToken);
            Assert.Equal(HealthStatus.Healthy, result.Status);
        }
        using (var second = provider.CreateScope())
        {
            var result = await second.ServiceProvider.GetRequiredService<AuthenticationReadinessCheck>()
                .CheckHealthAsync(new(), TestContext.Current.CancellationToken);
            Assert.Equal(HealthStatus.Healthy, result.Status);
        }
        Assert.Equal(1, shared.Calls);
        Assert.False(shared.ForceRefresh);
    }

    [Fact]
    public async Task Changed_generation_bypasses_cache_and_failure_contains_no_exception_or_tokens()
    {
        var shared = new SharedCredentials();
        var health = new AuthenticationReadinessCheck(new Credentials(shared), new());
        Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync(new(), TestContext.Current.CancellationToken)).Status);
        shared.Generation = "after-logout";
        shared.Acquire = _ => throw new GraphAuthenticationException("authentication_required");
        var result = await health.CheckHealthAsync(new(), TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(2, shared.Calls);
        Assert.Null(result.Exception);
        Assert.Empty(result.Data);
        Assert.DoesNotContain("synthetic-token", result.Description ?? "");
    }

    [Fact]
    public async Task Logout_during_acquisition_cannot_cache_old_success_for_new_generation()
    {
        var shared = new SharedCredentials();
        shared.Acquire = _ => { shared.Generation = "disconnected"; return Task.FromResult("synthetic-token"); };
        var health = new AuthenticationReadinessCheck(new Credentials(shared), new());
        Assert.Equal(HealthStatus.Unhealthy, (await health.CheckHealthAsync(new(), TestContext.Current.CancellationToken)).Status);
        shared.Acquire = _ => throw new GraphAuthenticationException("authentication_required");
        Assert.Equal(HealthStatus.Unhealthy, (await health.CheckHealthAsync(new(), TestContext.Current.CancellationToken)).Status);
        Assert.Equal(2, shared.Calls);
    }

    [Fact]
    public async Task Canceled_probe_does_not_poison_cache_and_releases_gate()
    {
        var shared = new SharedCredentials();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        shared.Acquire = async token =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        };
        var health = new AuthenticationReadinessCheck(new Credentials(shared), new());
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var probe = health.CheckHealthAsync(new(), canceled.Token);
        await started.Task;
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
        shared.Acquire = _ => Task.FromResult("synthetic-token");
        Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync(new(), TestContext.Current.CancellationToken)).Status);
        Assert.Equal(2, shared.Calls);
    }

    [Fact]
    public async Task Internal_timeout_is_an_unhealthy_result_without_exception_details()
    {
        var shared = new SharedCredentials { Acquire = _ => throw new OperationCanceledException("sensitive upstream detail") };
        var result = await new AuthenticationReadinessCheck(new Credentials(shared), new())
            .CheckHealthAsync(new(), TestContext.Current.CancellationToken);
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Null(result.Exception);
        Assert.Equal("Authentication unavailable.", result.Description);
    }

    [Fact]
    public async Task Concurrent_probes_share_one_acquisition()
    {
        var pending = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shared = new SharedCredentials { Acquire = _ => pending.Task };
        var health = new AuthenticationReadinessCheck(new Credentials(shared), new());
        var first = health.CheckHealthAsync(new(), TestContext.Current.CancellationToken);
        var second = health.CheckHealthAsync(new(), TestContext.Current.CancellationToken);
        Assert.Equal(1, shared.Calls);
        pending.SetResult("synthetic-token");
        Assert.All(await Task.WhenAll(first, second), result => Assert.Equal(HealthStatus.Healthy, result.Status));
        Assert.Equal(1, shared.Calls);
    }

    private sealed class SharedCredentials
    {
        public string Generation = "connected-generation";
        public int Calls;
        public bool ForceRefresh;
        public Func<CancellationToken, Task<string>> Acquire = _ => Task.FromResult("synthetic-token");
    }

    private sealed class Credentials(SharedCredentials shared) : IGraphCredentialProvider
    {
        public string ConnectionGeneration => shared.Generation;
        public Task<string> GetTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
        {
            shared.Calls++;
            shared.ForceRefresh = forceRefresh;
            return shared.Acquire(cancellationToken);
        }
    }
}
