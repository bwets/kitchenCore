using System.Net;

namespace KitchenCore.Client.Services;

/// <summary>Why a call to the server did not work, in terms the UI can translate.</summary>
public enum ApiFailure
{
    /// <summary>The server could not be reached at all.</summary>
    Unreachable,

    /// <summary>This device has not registered yet.</summary>
    Unregistered,

    /// <summary>Registered, but not allowed to do this.</summary>
    NotAllowed,

    /// <summary>Someone else changed the data first.</summary>
    Conflict,

    /// <summary>The server answered, but with a failure.</summary>
    Server,
}

/// <summary>
/// A call to the server that did not work.
///
/// Thrown rather than returned so a page cannot quietly carry on with empty data
/// and show an empty week that looks like a real one. The reason is an enum, not
/// a message: the UI is bilingual, so the wording belongs in the resource files
/// rather than in whatever the server or the HTTP stack happened to say.
/// </summary>
public sealed class ApiException(ApiFailure failure, string? detail = null)
    : Exception(detail ?? failure.ToString())
{
    public ApiFailure Failure { get; } = failure;

    /// <summary>Resource key for the message to show.</summary>
    public string ResourceKey => Failure switch
    {
        ApiFailure.Unreachable => "Error_Unreachable",
        ApiFailure.Unregistered => "Error_Unregistered",
        ApiFailure.NotAllowed => "Error_NotAllowed",
        ApiFailure.Conflict => "Conflict",
        _ => "Error_Server",
    };

    /// <summary>Maps a response the server actually produced.</summary>
    public static ApiException From(HttpStatusCode status, string? detail = null) => new(status switch
    {
        HttpStatusCode.Unauthorized => ApiFailure.Unregistered,
        HttpStatusCode.Forbidden => ApiFailure.NotAllowed,
        HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict => ApiFailure.Conflict,
        _ => ApiFailure.Server,
    }, detail);
}
