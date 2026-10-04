namespace GraphMcp.Configuration;

public sealed class GraphOptions
{
    public const string SectionName = "Graph";
    public int MaxPageSize { get; set; } = 100;
    public int MaxCalendarRangeDays { get; set; } = 31;
    public int MaxAttachmentBytes { get; set; } = 1_048_576;
    public int MaxAttachmentTextChars { get; set; } = 32_768;
    public int MaxBodyChars { get; set; } = 40_000;
    public int MaxJsonBytes { get; set; } = 2_097_152;
    public int RequestTimeoutSeconds { get; set; } = 15;
    public int ToolTimeoutSeconds { get; set; } = 45;
    public int MaxRetries { get; set; } = 2;
    public int MaxConcurrentRequests { get; set; } = 4;
}
