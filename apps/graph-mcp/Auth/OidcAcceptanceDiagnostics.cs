using System.Security.Claims;

namespace GraphMcp.Auth;

// Only fixed labels enter these events. Do not add exceptions, principals, protocol messages,
// authentication results/properties, request metadata, or configured identifiers as log arguments.
internal static class OidcAcceptanceDiagnostics
{
    public static void Write(HttpContext context, string stage, string gate, string status, string comparison = "none")
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("GraphMcp.Auth.OidcAcceptanceDiagnostics");
        var level = status is "failed" or "mismatch" ? LogLevel.Warning : LogLevel.Information;
        logger.Log(level, new EventId(4100, "OidcAcceptanceGate"),
            "OIDC acceptance stage {Stage}; gate {Gate}; status {Status}; comparison {Comparison}",
            stage, gate, status, comparison);
    }

    public static bool CheckOwner(HttpContext context, OwnerIdentityStore owner, ClaimsPrincipal? principal, string stage)
    {
        var mismatch = owner.GetIdentityMismatch(principal);
        if (mismatch == OwnerIdentityMismatch.None)
        {
            Write(context, stage, "owner_identity", "approved");
            return true;
        }
        if (mismatch.HasFlag(OwnerIdentityMismatch.Tenant)) Write(context, stage, "owner_identity", "mismatch", "tenant");
        if (mismatch.HasFlag(OwnerIdentityMismatch.Object)) Write(context, stage, "owner_identity", "mismatch", "object");
        if (mismatch.HasFlag(OwnerIdentityMismatch.HomeTenant)) Write(context, stage, "owner_identity", "mismatch", "home-tenant");
        if (mismatch.HasFlag(OwnerIdentityMismatch.HomeObject)) Write(context, stage, "owner_identity", "mismatch", "home-object");
        return false;
    }
}
