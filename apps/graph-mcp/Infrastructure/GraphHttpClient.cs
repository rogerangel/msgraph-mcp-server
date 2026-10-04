using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using GraphMcp.Auth;
using GraphMcp.Configuration;
using Microsoft.Extensions.Options;

namespace GraphMcp.Infrastructure;

public sealed class GraphConcurrencyGate(IOptions<GraphOptions> options)
{
    internal SemaphoreSlim Semaphore { get; } = new(options.Value.MaxConcurrentRequests);
}

public sealed partial class GraphHttpClient(HttpClient http, IGraphCredentialProvider credentials, IOptions<GraphOptions> options, GraphConcurrencyGate concurrency, ILogger<GraphHttpClient> logger)
{
    private readonly GraphOptions _options = options.Value;
    internal static readonly Uri Origin = new("https://graph.microsoft.com/v1.0/");
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<JsonDocument> GetAsync(string relativeUrl, CancellationToken cancellationToken)
        => await ReadJsonAsync(HttpMethod.Get, relativeUrl, null, cancellationToken);

    public async Task<JsonDocument> GetScheduleAsync(object body, CancellationToken cancellationToken)
        => await ReadJsonAsync(HttpMethod.Post, "me/calendar/getSchedule", JsonSerializer.Serialize(body, JsonOptions), cancellationToken);

    public Task<byte[]> GetAttachmentBytesAsync(string relativeUrl, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, relativeUrl, null, _options.MaxAttachmentBytes, cancellationToken);

    private async Task<JsonDocument> ReadJsonAsync(HttpMethod method, string url, string? body, CancellationToken cancellationToken)
    {
        var bytes = await SendAsync(method, url, body, _options.MaxJsonBytes, cancellationToken);
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { throw new GraphOperationException("invalid_upstream_response", "Microsoft Graph returned an invalid response."); }
    }

    internal static Uri ValidateUrl(HttpMethod method, string url)
    {
        var uri = Uri.TryCreate(url, UriKind.Absolute, out var absolute) ? absolute : new Uri(Origin, url);
        if (uri.Scheme != "https" || uri.Host != Origin.Host || uri.Port != 443 || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new GraphOperationException("invalid_upstream_response", "An invalid Microsoft Graph URL was rejected.");
        var path = uri.AbsolutePath;
        var isRead = method == HttpMethod.Get && AllowedGetPath().IsMatch(path);
        var isSchedule = method == HttpMethod.Post && path == "/v1.0/me/calendar/getSchedule" && uri.Query.Length == 0;
        if (!isRead && !isSchedule)
            throw new GraphOperationException("operation_not_allowed", "This Microsoft Graph operation is not permitted.");
        return uri;
    }

    [GeneratedRegex("^/v1\\.0/me(?:/messages(?:/[^/]+(?:/attachments(?:/[^/]+(?:/\\$value)?)?)?)?|/mailFolders/(?:inbox|sentitems|archive|drafts|deleteditems|junkemail)/messages|/calendars(?:/[^/]+(?:/calendarView|/events/[^/]+)?)?|/calendar(?:/calendarView|/events/[^/]+))?$")]
    private static partial Regex AllowedGetPath();

    private async Task<byte[]> SendAsync(HttpMethod method, string url, string? body, int maxBytes, CancellationToken cancellationToken)
    {
        var uri = ValidateUrl(method, url);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.ToolTimeoutSeconds));
        var token = deadline.Token;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var operation = body is not null ? "calendar_availability" : uri.AbsolutePath.Contains("attachments", StringComparison.Ordinal) ? "mail_attachment" : uri.AbsolutePath.Contains("messages", StringComparison.Ordinal) ? "mail_read" : uri.AbsolutePath.Contains("calendar", StringComparison.Ordinal) ? "calendar_read" : "account_read";
        var acquired = false;
        try
        {
            await concurrency.Semaphore.WaitAsync(token);
            acquired = true;
            var refreshed = false;
            var forceRefreshNext = false;
            var retry = 0;
            while (true)
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                attempt.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
                using var request = new HttpRequestMessage(method, uri);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await credentials.GetTokenAsync(forceRefreshNext, attempt.Token));
                forceRefreshNext = false;
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.TryAddWithoutValidation("Prefer", "outlook.body-content-type=\"text\", outlook.timezone=\"UTC\", IdType=\"ImmutableId\"");
                if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                try
                {
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
                    var requestId = response.Headers.TryGetValues("request-id", out var requestIds) && Guid.TryParse(requestIds.FirstOrDefault(), out var id) ? id.ToString() : null;
                    if (response.IsSuccessStatusCode)
                    {
                        if (response.Content.Headers.ContentLength > maxBytes) throw TooLarge();
                        await using var stream = await response.Content.ReadAsStreamAsync(attempt.Token);
                        using var output = new MemoryStream();
                        var buffer = new byte[8192];
                        while (true)
                        {
                            var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxBytes + 1 - (int)output.Length)), attempt.Token);
                            if (count == 0) break;
                            output.Write(buffer, 0, count);
                            if (output.Length > maxBytes) throw TooLarge();
                        }
                        logger.LogInformation("Graph operation {Operation} succeeded in {DurationMs} ms; request {GraphRequestId}", operation, started.ElapsedMilliseconds, requestId);
                        return output.ToArray();
                    }
                    if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshed && retry < _options.MaxRetries)
                    {
                        refreshed = true;
                        forceRefreshNext = true;
                        retry++;
                        continue;
                    }
                    var retryAfter = GetRetryDelay(response, retry);
                    if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode is >= 500 and <= 599) && retry < _options.MaxRetries)
                    {
                        if (response.StatusCode == HttpStatusCode.TooManyRequests)
                            logger.LogWarning("Graph operation {Operation} throttled; retry after {RetryAfterSeconds}; request {GraphRequestId}", operation, (int)Math.Ceiling(retryAfter.TotalSeconds), requestId);
                        if (started.Elapsed + retryAfter + TimeSpan.FromSeconds(1) >= TimeSpan.FromSeconds(_options.ToolTimeoutSeconds))
                            throw MapError(response.StatusCode, retryAfter, requestId);
                        retry++;
                        await Task.Delay(retryAfter, token);
                        continue;
                    }
                    throw MapError(response.StatusCode, retryAfter, requestId);
                }
                catch (HttpRequestException) when (retry < _options.MaxRetries)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, retry++) + Random.Shared.Next(100)), token);
                }
                catch (HttpRequestException) { throw new GraphOperationException("upstream_unavailable", "Microsoft Graph is temporarily unavailable."); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && retry < _options.MaxRetries)
                {
                    retry++;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    throw new GraphOperationException("upstream_timeout", "The Microsoft Graph request timed out.");
                }
            }
        }
        catch (GraphOperationException exception)
        {
            logger.LogWarning("Graph operation {Operation} failed with {Outcome} in {DurationMs} ms; request {GraphRequestId}", operation, exception.Code, started.ElapsedMilliseconds, exception.RequestId);
            throw;
        }
        catch (GraphAuthenticationException exception)
        {
            logger.LogWarning("Graph operation {Operation} failed with {Outcome} in {DurationMs} ms", operation, exception.Code, started.ElapsedMilliseconds);
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Graph operation {Operation} timed out after {DurationMs} ms", operation, started.ElapsedMilliseconds);
            throw new GraphOperationException("upstream_timeout", "The Microsoft Graph operation timed out.");
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Graph operation {Operation} was cancelled after {DurationMs} ms", operation, started.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            if (acquired) concurrency.Semaphore.Release();
        }
    }

    private static GraphOperationException TooLarge() => new("response_too_large", "The Microsoft Graph response exceeds the configured size limit.");
    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int retry)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header?.Date is { } date) return date <= DateTimeOffset.UtcNow ? TimeSpan.Zero : date - DateTimeOffset.UtcNow;
        return TimeSpan.FromMilliseconds(500 * Math.Pow(2, retry) + Random.Shared.Next(100));
    }
    private static GraphOperationException MapError(HttpStatusCode status, TimeSpan delay, string? requestId) => status switch
    {
        HttpStatusCode.Unauthorized => new("authentication_required", "Microsoft sign-in is required.", requestId: requestId),
        HttpStatusCode.Forbidden => new("access_denied", "Microsoft Graph denied access. Permissions will not be expanded automatically.", requestId: requestId),
        HttpStatusCode.NotFound => new("not_found", "The requested item was not found.", requestId: requestId),
        HttpStatusCode.TooManyRequests => new("throttled", "Microsoft Graph is throttling requests; retry later.", (int)Math.Min(int.MaxValue, Math.Ceiling(delay.TotalSeconds)), requestId),
        HttpStatusCode.BadRequest => new("invalid_graph_request", "Microsoft Graph could not process the supplied query or range.", requestId: requestId),
        _ => new("upstream_unavailable", "Microsoft Graph could not complete the operation.", requestId: requestId)
    };
}
