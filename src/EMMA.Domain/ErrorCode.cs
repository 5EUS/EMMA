namespace EMMA.Domain;

/// <summary>Failure classes shared by every plugin call; mirrors <c>error-code</c> in the WIT contract.</summary>
public enum ErrorCode
{
    /// <summary>The id or token does not exist (any more).</summary>
    NotFound,

    /// <summary>Credentials are missing, wrong or expired; the user must act.</summary>
    Unauthorized,

    /// <summary>The upstream asked for slower requests.</summary>
    RateLimited,

    /// <summary>The upstream is down or unreachable; retrying later may help.</summary>
    Unavailable,

    /// <summary>The request was malformed.</summary>
    InvalidArgument,

    /// <summary>The plugin does not support this request.</summary>
    Unsupported,

    /// <summary>The call was cancelled by deadline or shutdown.</summary>
    Cancelled,

    /// <summary>A bug in the plugin or a contract violation by it.</summary>
    Internal,
}
