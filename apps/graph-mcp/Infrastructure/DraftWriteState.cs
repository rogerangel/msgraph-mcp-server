using GraphMcp.Models;

namespace GraphMcp.Infrastructure;

// One instance per tool invocation. Once dispatched, failures cannot prove no mutation occurred.
// In particular, a confirmed commit must survive response parsing and caller cancellation failures.
public sealed class DraftWriteState
{
    public string Outcome { get; private set; } = "not_applied";
    public string? MessageId { get; private set; }

    public void MarkDispatched(string? knownDraftId = null)
    {
        Outcome = "unknown";
        SetMessageId(knownDraftId);
    }

    public void MarkCommitted() => Outcome = "committed";
    public void MarkRejected() => Outcome = "not_applied";

    public void SetMessageId(string? id)
    {
        if (MessageId is null && !string.IsNullOrWhiteSpace(id) && id.Length <= 2048 && !id.Any(char.IsControl)) MessageId = id;
    }

    public DraftWriteReceipt ToReceipt(DraftDto? draft = null, GraphOperationException? error = null)
    {
        if (draft is not null) SetMessageId(draft.Id);
        return Outcome switch
        {
            "committed" => new(Outcome, false, draft is null,
                draft is null ? "committed_details_unavailable" : "draft_saved",
                draft is null
                    ? "Microsoft Graph confirmed the write. Details are unavailable; inspect the draft before further action. Do not repeat the write."
                    : "The draft was saved. No message was sent. Do not repeat this write automatically.",
                MessageId, draft),
            "unknown" => new(Outcome, false, true, "write_outcome_unknown",
                "The write may have completed. This outcome is NOT retry-safe. Inspect the draft or Drafts folder before deciding what to do; do not repeat automatically.",
                MessageId, null),
            _ => new("not_applied", true, false, error?.Code ?? "write_not_applied",
                error?.Message ?? "No draft write was applied.", MessageId, null, error?.RetryAfterSeconds)
        };
    }
}
