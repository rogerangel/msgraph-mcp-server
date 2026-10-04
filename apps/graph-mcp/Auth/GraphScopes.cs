namespace GraphMcp.Auth;

public static class GraphScopes
{
    public static readonly string[] Required = ["User.Read", "Mail.Read", "Calendars.Read"];

    // MSAL can report OIDC scopes alongside resource scopes. No other resource is permitted.
    public static bool AreExactlyApproved(IEnumerable<string> scopes)
    {
        var resource = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var original in scopes)
        {
            var scope = original;
            if (scope.StartsWith("https://graph.microsoft.com/", StringComparison.OrdinalIgnoreCase))
                scope = scope["https://graph.microsoft.com/".Length..];
            if (scope is "openid" or "profile" or "offline_access") continue;
            resource.Add(scope);
        }
        return resource.SetEquals(Required);
    }
}
