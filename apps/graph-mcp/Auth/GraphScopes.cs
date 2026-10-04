namespace GraphMcp.Auth;

public static class GraphScopes
{
    public static readonly string[] Required = ["User.Read", "Mail.ReadWrite", "Calendars.Read"];

    public static bool ContainsRequiredScopes(IEnumerable<string> scopes) => ContainsRequiredScopes(scopes, out _);

    // Entra may return previously consented Graph scopes as well as those requested.
    // Extra grants never expand the service's hard-coded Graph routes or MCP tool surface.
    public static bool ContainsRequiredScopes(IEnumerable<string> scopes, out bool hasAdditionalScopes)
    {
        var resource = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var original in scopes)
        {
            var scope = original;
            if (scope.StartsWith("https://graph.microsoft.com/", StringComparison.OrdinalIgnoreCase))
                scope = scope["https://graph.microsoft.com/".Length..];
            if (scope.Equals("openid", StringComparison.OrdinalIgnoreCase)
                || scope.Equals("profile", StringComparison.OrdinalIgnoreCase)
                || scope.Equals("offline_access", StringComparison.OrdinalIgnoreCase)
                || scope.Equals("email", StringComparison.OrdinalIgnoreCase)) continue;
            resource.Add(scope);
        }
        hasAdditionalScopes = resource.Any(scope => !Required.Contains(scope, StringComparer.OrdinalIgnoreCase));
        return resource.IsSupersetOf(Required);
    }
}
