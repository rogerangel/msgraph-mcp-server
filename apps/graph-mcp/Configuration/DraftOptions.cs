namespace GraphMcp.Configuration;

public sealed class DraftOptions
{
    public int MaxBodyChars { get; set; } = 20_000;
    public int MaxRecipients { get; set; } = 20;
    // Operator attestation that the live Graph stale-ETag test passed for this deployment.
    // This gates only PATCH; the four native creation operations remain available.
    public bool EnableUpdates { get; set; }
}
