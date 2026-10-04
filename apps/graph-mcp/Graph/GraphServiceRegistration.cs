using GraphMcp.Configuration;
using GraphMcp.Infrastructure;

namespace GraphMcp.Graph;

public static class GraphServiceRegistration
{
    public static IServiceCollection AddGraphServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DraftOptions>().BindConfiguration("Drafts")
            .Validate(x => x.MaxBodyChars is >= 1 and <= 20_000 && x.MaxRecipients is >= 1 and <= 20,
                "Draft limits exceed the approved bounds.")
            .ValidateOnStart();
        services.AddOptions<GraphOptions>().Bind(configuration.GetSection(GraphOptions.SectionName))
            .Validate(x => x.MaxPageSize is >= 1 and <= 100 && x.MaxCalendarRangeDays is >= 1 and <= 31, "Graph collection limits exceed Phase 1 bounds.")
            .Validate(x => x.MaxAttachmentBytes is >= 1 and <= 1_048_576 && x.MaxAttachmentTextChars is >= 1 and <= 32_768 && x.MaxBodyChars is >= 1 and <= 40_000, "Graph content limits exceed Phase 1 bounds.")
            .Validate(x => x.MaxJsonBytes is >= 1 and <= 2_097_152 && x.MaxRetries is >= 0 and <= 2 && x.RequestTimeoutSeconds is >= 1 and <= 15 && x.ToolTimeoutSeconds is >= 1 and <= 45 && x.MaxConcurrentRequests is >= 1 and <= 4, "Graph request limits exceed Phase 1 bounds.")
            .ValidateOnStart();
        services.AddSingleton<GraphConcurrencyGate>();
        services.AddScoped<GraphCursorProtector>();
        services.AddScoped<DraftEditVersionProtector>();
        services.AddHttpClient<GraphHttpClient>(http => http.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
            .RemoveAllLoggers();
        services.AddTransient<IAccountService, AccountService>();
        services.AddTransient<IMailService, MailService>();
        services.AddTransient<IDraftService, DraftService>();
        services.AddTransient<ICalendarService, CalendarService>();
        return services;
    }
}
