namespace EMMA.Domain;

public static class ErrorCodes
{
    public const string InvalidRequest = "invalid_request";
    public const string NotFound = "not_found";
    public const string Timeout = "timeout";
    public const string Cancelled = "cancelled";
    public const string Unauthenticated = "unauthenticated";
    public const string UpstreamFailure = "upstream_failure";
}