using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GraphMcp.Infrastructure;

public sealed partial class GraphHttpClient
{
    public Task<JsonDocument> CreateDraftAsync(object body, DraftWriteState state, CancellationToken cancellationToken)
        => SendDraftAsync(HttpMethod.Post, "me/messages", body, null, null, "draft_create", state, cancellationToken);

    public Task<JsonDocument> CreateReplyDraftAsync(string messageId, object body, DraftWriteState state, CancellationToken cancellationToken)
        => SendDraftAsync(HttpMethod.Post, $"me/messages/{GraphInput.Id(messageId)}/createReply", body, null, null, "draft_reply", state, cancellationToken);

    public Task<JsonDocument> CreateReplyAllDraftAsync(string messageId, object body, DraftWriteState state, CancellationToken cancellationToken)
        => SendDraftAsync(HttpMethod.Post, $"me/messages/{GraphInput.Id(messageId)}/createReplyAll", body, null, null, "draft_reply_all", state, cancellationToken);

    public Task<JsonDocument> CreateForwardDraftAsync(string messageId, object body, DraftWriteState state, CancellationToken cancellationToken)
        => SendDraftAsync(HttpMethod.Post, $"me/messages/{GraphInput.Id(messageId)}/createForward", body, null, null, "draft_forward", state, cancellationToken);

    public Task<JsonDocument> UpdateDraftAsync(string messageId, object body, string etag, DraftWriteState state, CancellationToken cancellationToken,
        string? expectedGeneration = null)
        => SendDraftAsync(HttpMethod.Patch, $"me/messages/{GraphInput.Id(messageId)}", body, etag, messageId, "draft_update", state, cancellationToken, expectedGeneration);

    private static Uri ValidateDraftUrl(HttpMethod method, string path)
    {
        var uri = new Uri(Origin, path);
        var allowed = method == HttpMethod.Post && DraftCreatePath().IsMatch(uri.AbsolutePath)
            || method == HttpMethod.Patch && DraftUpdatePath().IsMatch(uri.AbsolutePath);
        if (!allowed || uri.Scheme != "https" || uri.Host != Origin.Host || uri.Port != 443
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            throw new GraphOperationException("operation_not_allowed", "This Microsoft Graph operation is not permitted.");
        return uri;
    }

    [GeneratedRegex("^/v1\\.0/me/messages(?:/[^/]+/(?:createReply|createReplyAll|createForward))?$")]
    private static partial Regex DraftCreatePath();
    [GeneratedRegex("^/v1\\.0/me/messages/[^/]+$")]
    private static partial Regex DraftUpdatePath();

    private async Task<JsonDocument> SendDraftAsync(HttpMethod method, string path, object body, string? etag,
        string? knownDraftId, string operation, DraftWriteState state, CancellationToken cancellationToken, string? expectedGeneration = null)
    {
        var uri = ValidateDraftUrl(method, path);
        var payload = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        // Bound even JSON escaping expansion from validated 20,000-character input.
        if (payload.Length > 262_144) throw GraphOperationException.Invalid("The draft request exceeds its byte limit.");
        if (method == HttpMethod.Patch && (etag is null || etag.Length > 2048 || etag.Any(char.IsControl)
            || !EntityTagHeaderValue.TryParse(etag, out var parsed) || parsed.Tag == "*"))
            throw GraphOperationException.Invalid("A specific draft version is required.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(_options.ToolTimeoutSeconds));
        var token = deadline.Token;
        var started = System.Diagnostics.Stopwatch.StartNew();
        var acquired = false;
        string? requestId = null;
        try
        {
            await concurrency.Semaphore.WaitAsync(token);
            acquired = true;
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
            attempt.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await credentials.GetTokenAsync(false, attempt.Token));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("Prefer", "outlook.body-content-type=\"text\", IdType=\"ImmutableId\"");
            if (etag is not null) request.Headers.IfMatch.Add(EntityTagHeaderValue.Parse(etag));
            request.Content = new ByteArrayContent(payload);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            attempt.Token.ThrowIfCancellationRequested();
            if (expectedGeneration is not null && expectedGeneration != credentials.ConnectionGeneration)
                throw new GraphOperationException("invalid_edit_version", "The Microsoft connection changed. Read the draft again.");
            // From this point no automatic replay is safe. HttpClient cannot prove whether a
            // failed/cancelled exchange reached Graph, so classify uncertain failures conservatively.
            state.MarkDispatched(knownDraftId);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token);
            // Record commitment before inspecting any optional response headers/body.
            if (response.IsSuccessStatusCode) state.MarkCommitted();
            else if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Conflict
                or HttpStatusCode.PreconditionFailed or HttpStatusCode.UnprocessableEntity
                or HttpStatusCode.TooManyRequests)
                state.MarkRejected();
            requestId = response.Headers.TryGetValues("request-id", out var ids) && Guid.TryParse(ids.FirstOrDefault(), out var id) ? id.ToString() : null;
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
                    throw new GraphOperationException("draft_conflict", "The draft changed. Read it again before deciding on an update.", requestId: requestId);
                var delay = GetRetryDelay(response, 0);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    logger.LogWarning("Graph operation {Operation} throttled; retry after {RetryAfterSeconds}; request {GraphRequestId}",
                        operation, (int)Math.Min(int.MaxValue, Math.Ceiling(delay.TotalSeconds)), requestId);
                throw MapError(response.StatusCode, delay, requestId);
            }

            if (response.Content.Headers.ContentLength > _options.MaxJsonBytes) throw TooLarge();
            await using var stream = await response.Content.ReadAsStreamAsync(attempt.Token);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, _options.MaxJsonBytes + 1 - (int)output.Length)), attempt.Token);
                if (count == 0) break;
                output.Write(buffer, 0, count);
                if (output.Length > _options.MaxJsonBytes) throw TooLarge();
            }
            var document = JsonDocument.Parse(output.ToArray());
            if (knownDraftId is null && document.RootElement.ValueKind == JsonValueKind.Object)
                state.SetMessageId(document.RootElement.Text("id"));
            return document;
        }
        finally
        {
            if (acquired) concurrency.Semaphore.Release();
            // No URLs, arguments, ETags, tokens or response bodies in write logs.
            logger.LogInformation("Graph operation {Operation} completed with {Outcome} in {DurationMs} ms; request {GraphRequestId}",
                operation, state.Outcome, started.ElapsedMilliseconds, requestId);
        }
    }
}
