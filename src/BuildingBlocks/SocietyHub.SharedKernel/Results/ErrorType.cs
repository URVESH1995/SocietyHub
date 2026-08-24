namespace SocietyHub.SharedKernel.Results;

/// <summary>
/// The kind of failure, which the API layer translates into an HTTP status code.
/// Handlers pick the kind; only the endpoint filter knows about HTTP.
/// </summary>
public enum ErrorType
{
    /// <summary>Unexpected or unclassified failure. Maps to 500.</summary>
    Failure = 0,

    /// <summary>Input did not satisfy the contract. Maps to 400.</summary>
    Validation = 1,

    /// <summary>The requested resource does not exist. Maps to 404.</summary>
    NotFound = 2,

    /// <summary>State conflict, e.g. duplicate or concurrency clash. Maps to 409.</summary>
    Conflict = 3,

    /// <summary>Caller is not authenticated. Maps to 401.</summary>
    Unauthorized = 4,

    /// <summary>Caller is authenticated but not allowed. Maps to 403.</summary>
    Forbidden = 5,
}
