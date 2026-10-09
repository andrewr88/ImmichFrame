using System.Text;
using System.Text.Json;
using ImmichFrame.Core.Interfaces;
using Microsoft.Extensions.Logging;

namespace ImmichFrame.Core.Helpers;

public static class WebhookHelper
{
    /// <summary>
    /// The named <c>HttpClient</c> notifications go out on, configured in <c>Program.cs</c>: bounded by
    /// <see cref="RequestTimeout"/>, and without the factory's request logging, which writes each
    /// request's full URL into the log - and a webhook URL commonly carries its secret in its path.
    /// </summary>
    public const string HttpClientName = "WebhookClient";

    /// <summary>
    /// How long one notification may take. One is sent for every asset served and nothing waits for
    /// it, so a receiver that accepts the connection and never answers would otherwise hold each of
    /// them open for HttpClient's default of 100 seconds.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// One instance for every notification. An options instance caches the metadata it builds, so a
    /// new one per notification built all of it again each time (CA1869).
    /// <para>
    /// The converter is part of the payload and has to stay. It serializes the notification's runtime
    /// type rather than the interface's two properties, and because it writes each value back out
    /// through a <see cref="JsonDocument"/>, the '+' of a DateTime's offset arrives escaped as
    /// \u002B, where serializing the runtime type directly would leave it bare. Receivers have always
    /// been sent those bytes; <c>WebhookTests</c> in ImmichFrame.WebApi.Tests pins them.
    /// </para>
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters = { new PolymorphicJsonConverter<IWebhookNotification>() },
        WriteIndented = true
    };

    /// <summary>
    /// Posts <paramref name="notification"/> to <paramref name="webhookUrl"/>, if one is configured.
    /// <para>
    /// Never throws. AssetController fires the notification and forgets it, so an exception escaping
    /// this was observed by nothing: a webhook that could not be reached left no trace in the log at
    /// all. A failure is logged as a warning instead - by exception or by status, never with the URL.
    /// </para>
    /// </summary>
    /// <param name="httpClient">
    /// A client created as <see cref="HttpClientName"/>, whose registration is what bounds the request
    /// and keeps its URL out of the log.
    /// </param>
    public static async Task SendWebhookNotification(IWebhookNotification notification, string? webhookUrl,
        HttpClient httpClient, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return;

        try
        {
            var json = JsonSerializer.Serialize(notification, SerializerOptions);
            using var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            // Done once the headers are in: only the status is read, and PostAsync read the whole
            // body first, holding the connection - and the body in memory - for however long the
            // receiver took to send it. Disposing the response lets go of the rest unread.
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

            if (response.IsSuccessStatusCode)
            {
                logger.LogDebug("Webhook notification sent: {statusCode} {status}", (int)response.StatusCode, response.StatusCode);
            }
            else
            {
                logger.LogWarning("Failed to send notification to webhook: {statusCode} {status}", (int)response.StatusCode, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            // Sanitized, as the calendar reader does with the same kind of message: a transport failure
            // can quote what the far end sent. HttpClient's own messages name the host and port at
            // most, never the path or query a webhook keeps its secret in.
            logger.LogWarning("Failed to send notification to webhook: {exceptionType}: {sanitizedReason}",
                ex.GetType().Name, ex.Message.SanitizeString());
        }
    }
}