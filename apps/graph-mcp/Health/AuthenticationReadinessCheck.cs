using GraphMcp.Auth;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace GraphMcp.Health;

public sealed class AuthenticationReadinessState
{
    internal readonly SemaphoreSlim Gate = new(1, 1);
    internal DateTimeOffset CheckedAt;
    internal string? Generation;
    internal HealthCheckResult Cached = HealthCheckResult.Unhealthy("Authentication required.");
}

public sealed class AuthenticationReadinessCheck(IGraphCredentialProvider credentials, AuthenticationReadinessState state) : IHealthCheck
{

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            var generation = credentials.ConnectionGeneration;
            if (state.Generation == generation && DateTimeOffset.UtcNow - state.CheckedAt < TimeSpan.FromSeconds(60))
                return state.Cached;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await credentials.GetTokenAsync(false, timeout.Token);
                // Logout can occur just after token acquisition releases its lifecycle gate.
                // Never associate that older successful token with the new disconnected generation.
                state.Cached = credentials.ConnectionGeneration == generation
                    ? HealthCheckResult.Healthy()
                    : HealthCheckResult.Unhealthy("Authentication state changed.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                state.Cached = HealthCheckResult.Unhealthy("Authentication unavailable.");
            }
            catch (GraphAuthenticationException)
            {
                state.Cached = HealthCheckResult.Unhealthy("Authentication required or unavailable.");
            }
            // No exception objects or authentication details are attached to health results.
            state.Generation = generation;
            state.CheckedAt = DateTimeOffset.UtcNow;
            return state.Cached;
        }
        finally { state.Gate.Release(); }
    }
}
