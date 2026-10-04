using System.Net;
using System.Text;
using GraphMcp.Auth;
using GraphMcp.Configuration;
using GraphMcp.Graph;
using GraphMcp.Infrastructure;
using GraphMcp.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace GraphMcp.Tests.Graph;

internal sealed class FakeCredential : IGraphCredentialProvider
{
    public string ConnectionGeneration { get; set; } = "generation-a";
    public List<bool> Refreshes { get; } = [];
    public Task<string> GetTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Refreshes.Add(forceRefresh);
        return Task.FromResult("fake-token-never-log");
    }
}

internal sealed class FakeGraphHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return handle(request, cancellationToken);
    }
}

internal sealed class FakeAccount : IAccountService
{
    public Task<AccountDto> GetMeAsync(CancellationToken cancellationToken) => Task.FromResult(new AccountDto("owner-id", "Owner", "owner@example.com", "owner@example.com"));
}

internal sealed class GraphTestFixture : IDisposable
{
    public FakeCredential Credentials { get; } = new();
    public FakeGraphHandler Handler { get; }
    public GraphOptions Options { get; }
    public GraphHttpClient Client { get; }
    public GraphCursorProtector Cursors { get; }
    public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();
    public MailService Mail { get; }
    public CalendarService Calendar { get; }
    private readonly HttpClient _http;
    public GraphTestFixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler, GraphOptions? options = null)
    {
        Options = options ?? new GraphOptions { MaxRetries = 0 };
        Handler = new(handler);
        _http = new(Handler);
        Client = new(_http, Credentials, Microsoft.Extensions.Options.Options.Create(Options), new GraphConcurrencyGate(Microsoft.Extensions.Options.Options.Create(Options)), NullLogger<GraphHttpClient>.Instance);
        Cursors = new(Protection, Credentials, Microsoft.Extensions.Options.Options.Create(new MicrosoftOptions { ExpectedUserObjectId = "owner-id" }));
        Mail = new(Client, Cursors, Microsoft.Extensions.Options.Options.Create(Options));
        Calendar = new(Client, new FakeAccount(), Cursors, Microsoft.Extensions.Options.Options.Create(Options));
    }
    public static HttpResponseMessage Json(string value, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    public void Dispose() => _http.Dispose();
}
