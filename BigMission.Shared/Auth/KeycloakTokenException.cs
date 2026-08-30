using System.Net;

namespace BigMission.Shared.Auth;

/// <summary>
/// Thrown when the Keycloak token endpoint replied but did not supply an access token.
/// </summary>
/// <remarks>
/// Deliberately not derived from <see cref="HttpRequestException"/>. Consumers treat that as
/// transient transport trouble - backing off, or throttling how often they report it - which is
/// right for a flaky network and wrong for this. A client that has been disabled, a secret that has
/// been rotated, or a realm that has been renamed will not come good on its own, and needs to be
/// seen every time rather than sampled. A genuine transport failure on the way to Keycloak still
/// surfaces as <see cref="HttpRequestException"/>, so the two remain distinguishable.
/// </remarks>
/// <param name="message">A description of what Keycloak said, safe to log.</param>
/// <param name="statusCode">The status Keycloak replied with.</param>
/// <param name="error">Keycloak's own error code, when it sent one.</param>
/// <param name="errorDescription">Keycloak's own error description, when it sent one.</param>
/// <param name="responseBody">The response body, capped in length. Null when the body was empty.</param>
public class KeycloakTokenException(
    string message,
    HttpStatusCode statusCode,
    string? error,
    string? errorDescription,
    string? responseBody) : Exception(message)
{
    /// <summary>
    /// The status code Keycloak replied with.
    /// </summary>
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>
    /// Keycloak's own error code, such as <c>invalid_client</c>, or null if it did not send one.
    /// </summary>
    public string? Error { get; } = error;

    /// <summary>
    /// Keycloak's own error description, or null if it did not send one.
    /// </summary>
    public string? ErrorDescription { get; } = errorDescription;

    /// <summary>
    /// The response body, capped in length, or null if the body was empty.
    /// </summary>
    /// <remarks>
    /// Kept off <see cref="Exception.Message"/> on purpose. When something other than Keycloak
    /// answers - a gateway, a captive portal - the body is an error page rather than an explanation,
    /// and the message is shown to end users by at least one consumer.
    /// </remarks>
    public string? ResponseBody { get; } = responseBody;
}
