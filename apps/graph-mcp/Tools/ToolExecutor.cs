using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using GraphMcp.Auth;
using GraphMcp.Configuration;
using GraphMcp.Infrastructure;
using GraphMcp.Models;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace GraphMcp.Tools;

/// <summary>Bounds and observes tool execution without recording arguments or results.</summary>
public sealed class ToolExecutor(ILogger<ToolExecutor> logger, IOptions<GraphOptions> options, IOptions<DraftOptions> drafts)
{
    public const int MaxResultBytes = 1_048_576;
    public int DefaultPageSize => Math.Min(20, options.Value.MaxPageSize);
    public int DefaultBodyChars => Math.Min(20_000, options.Value.MaxBodyChars);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ActivitySource Activities = new("GraphMcp.Tools");
    private static readonly Meter Meter = new("GraphMcp.Tools");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("graphmcp.tool.duration", "ms");

    public async Task<CallToolResult> ExecuteAsync<T>(string name, RequestContext<CallToolRequestParams> context,
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        using var activity = Activities.StartActivity(name);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.ToolTimeoutSeconds, 1, 45)));
        var started = Stopwatch.GetTimestamp();
        var outcome = "success";
        logger.LogInformation("Tool {Tool} invoked; correlation {CorrelationId}", name, correlationId);
        try
        {
            ToolSchemas.Validate(name, context.Params.Arguments, options.Value, drafts.Value);
            deadline.Token.ThrowIfCancellationRequested();
            var value = await operation(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            var structured = JsonSerializer.SerializeToElement(value, JsonOptions);
            var result = new CallToolResult
            {
                StructuredContent = structured,
                Content = [new TextContentBlock { Text = structured.GetRawText() }],
                IsError = false
            };
            // Include both protocol representations and JSON escaping in the bound.
            if (JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions).Length > MaxResultBytes)
                throw new GraphOperationException("response_too_large", "The result exceeds the response limit. Request fewer items or less content.");
            return result;
        }
        catch (GraphOperationException exception)
        {
            outcome = exception.Code;
            return Error(exception.Code, exception.Message, correlationId, exception.RetryAfterSeconds);
        }
        catch (GraphAuthenticationException exception)
        {
            outcome = exception.Code;
            return Error(exception.Code, exception.Message, correlationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "cancelled";
            throw;
        }
        catch (OperationCanceledException)
        {
            outcome = "upstream_timeout";
            return Error(outcome, "The operation exceeded its time limit.", correlationId);
        }
        catch (Exception)
        {
            // Never log exception text: upstream failures may contain mailbox content or tokens.
            outcome = "operation_failed";
            return Error(outcome, "The operation could not be completed.", correlationId);
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Duration.Record(elapsed, new KeyValuePair<string, object?>("tool", name), new("outcome", outcome));
            logger.LogInformation("Tool {Tool} completed with {Outcome} in {DurationMs} ms; correlation {CorrelationId}",
                name, outcome, elapsed, correlationId);
        }
    }

    public async Task<CallToolResult> ExecuteWriteAsync(string name, RequestContext<CallToolRequestParams> context,
        Func<DraftWriteState, CancellationToken, Task<DraftDto>> operation, CancellationToken cancellationToken)
    {
        // Local to this invocation, including when multiple calls share a request scope.
        var state = new DraftWriteState();
        var correlationId = Guid.NewGuid().ToString("N");
        using var activity = Activities.StartActivity(name);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.ToolTimeoutSeconds, 1, 45)));
        var started = Stopwatch.GetTimestamp();
        logger.LogInformation("Tool {Tool} invoked; correlation {CorrelationId}", name, correlationId);
        DraftWriteReceipt receipt;
        try
        {
            ToolSchemas.Validate(name, context.Params.Arguments, options.Value, drafts.Value);
            deadline.Token.ThrowIfCancellationRequested();
            var draft = await operation(state, deadline.Token).ConfigureAwait(false);
            // A timeout or disconnect after confirmed commit must not turn it into a retryable failure.
            receipt = state.ToReceipt(draft);
        }
        catch (GraphOperationException error) { receipt = state.ToReceipt(error: error); }
        catch (GraphAuthenticationException error)
        {
            receipt = state.ToReceipt(error: new GraphOperationException(error.Code, error.Message));
        }
        catch (OperationCanceledException)
        {
            receipt = state.ToReceipt(error: new GraphOperationException(
                cancellationToken.IsCancellationRequested ? "cancelled" : "upstream_timeout",
                "The operation was cancelled or exceeded its time limit before a confirmed result."));
        }
        catch (Exception)
        {
            receipt = state.ToReceipt(error: new GraphOperationException("operation_failed", "The draft operation could not be completed."));
        }

        try
        {
            var result = WriteResult(receipt);
            if (JsonSerializer.SerializeToUtf8Bytes(result, JsonOptions).Length <= MaxResultBytes) return result;
        }
        catch (Exception)
        {
            // Projection/serialization failure must preserve committed/unknown status, never prompt replay.
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Duration.Record(elapsed, new KeyValuePair<string, object?>("tool", name), new("outcome", state.Outcome));
            logger.LogInformation("Tool {Tool} completed with {Outcome} in {DurationMs} ms; correlation {CorrelationId}",
                name, state.Outcome, elapsed, correlationId);
        }
        return WriteResult(state.ToReceipt(error: new GraphOperationException("response_too_large", "Draft result details could not be returned.")));
    }

    private static CallToolResult WriteResult(DraftWriteReceipt receipt)
    {
        var structured = JsonSerializer.SerializeToElement(receipt, JsonOptions);
        return new CallToolResult
        {
            IsError = receipt.Outcome != "committed", StructuredContent = structured,
            Content = [new TextContentBlock { Text = structured.GetRawText() }]
        };
    }

    private static CallToolResult Error(string code, string message, string correlationId, int? retryAfterSeconds = null)
    {
        var retryable = code is "throttled" or "upstream_unavailable" or "upstream_timeout" or "authentication_unavailable";
        // Error content intentionally does not claim to conform to the successful DTO schema.
        var text = JsonSerializer.Serialize(new { code, message, retryable, retryAfterSeconds, correlationId }, JsonOptions);
        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = text }] };
    }
}
