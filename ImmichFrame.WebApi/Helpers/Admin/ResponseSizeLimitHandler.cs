namespace ImmichFrame.WebApi.Helpers.Admin;

/// <summary>
/// The upstream sent, or announced, more than <see cref="Limit"/> bytes.
/// <para>
/// A subclass of <see cref="HttpRequestException"/> so that every caller which already treats a
/// transport failure as an ordinary outcome keeps doing so, and a distinct type so the one caller
/// that reports it can say what actually happened. Collapsing it into "could not reach the server"
/// sends an administrator to check DNS for a server that answered perfectly well.
/// </para>
/// </summary>
internal sealed class ResponseTooLargeException(long limit, long? declared)
    : HttpRequestException(
        $"The response {(declared is null ? "exceeded" : $"declared {declared} bytes, more than")} the {limit} bytes ImmichFrame will read.")
{
    public long Limit { get; } = limit;
}

/// <summary>
/// Refuses to read more than <paramref name="_maxBytes"/> from an upstream response.
/// <para>
/// The picker fetches from whatever URL an administrator names, which may not be an Immich server at
/// all - a typo, or a host that answers every request with an endless stream. The generated Immich
/// client reads its responses with <c>HttpCompletionOption.ResponseHeadersRead</c>, so
/// <c>HttpClient.MaxResponseContentBufferSize</c> never applies to it and there is otherwise no
/// bound on what one request can pull into this process.
/// </para>
/// <para>
/// Calendar feeds are read through it too, capped at <c>IcalCalendarService.MaxFeedBytes</c>: a feed
/// is likewise whatever its host chooses to send, and HttpClient's own buffering stops at two
/// gigabytes.
/// </para>
/// <para>
/// The body is buffered here, inside the handler, rather than being handed on behind a counting
/// stream. That costs the streaming of a person thumbnail, which is at most a few hundred kilobytes,
/// and buys the thing worth more: the whole read happens inside <c>SendAsync</c>, under the token
/// the client hands down, so the client's timeout - and the caller hanging up - covers the body and
/// not just the headers.
/// </para>
/// <para>
/// The buffering is a loop of this handler's own rather than <c>HttpContent.LoadIntoBufferAsync</c>,
/// which would enforce the cap for us but on .NET 8 takes no token - and by the time it runs nothing
/// else is listening for one, because SocketsHttpHandler stops watching the request's token once it
/// has handed the headers back. Read that way, a body that stalled part-way outlived the client's
/// timeout until the far end chose to hang up; <c>AdminImmichClientBoundsTests</c> pins the
/// difference on a real socket. Disposing the response from a cancellation callback, the obvious
/// patch, does end that read, but only through the connection's drain - up to two seconds late - and
/// by disposing a stream that is still being read, which the stream makes no promise to survive. A
/// read given the token is the cancellation the framework actually supports: it closes the
/// connection under the read at once.
/// </para>
/// </summary>
internal sealed class ResponseSizeLimitHandler(long _maxBytes) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        try
        {
            // Checked before reading as well as while reading: a declared length spares us reading
            // the whole of something we are going to refuse anyway.
            var declared = response.Content.Headers.ContentLength;
            if (declared > _maxBytes)
            {
                throw new ResponseTooLargeException(_maxBytes, declared);
            }

            var buffered = await BufferAsync(response.Content, cancellationToken).ConfigureAwait(false);

            response.Content.Dispose();
            response.Content = buffered;
        }
        catch
        {
            // The caller is handed the exception and never this response, so nothing else will ever
            // release the connection it is holding.
            response.Dispose();
            throw;
        }

        return response;
    }

    /// <summary>
    /// <paramref name="content"/> read to its end and handed back as an in-memory copy carrying the
    /// same headers - what <c>LoadIntoBufferAsync</c> would have left behind, read under
    /// <paramref name="cancellationToken"/>.
    /// </summary>
    private async Task<HttpContent> BufferAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();

        try
        {
            using var body = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                // Counted before the bytes are kept, so the buffer never holds more than the limit.
                if (buffer.Length + read > _maxBytes)
                {
                    throw new ResponseTooLargeException(_maxBytes, null);
                }

                buffer.Write(chunk, 0, read);
            }
        }
        catch (IOException exception)
        {
            // A connection failing part-way through the body surfaces from its stream as a bare
            // IOException, which HttpClient passes on untouched - and every caller of this client
            // takes HttpRequestException, not IOException, as the ordinary "could not reach" outcome,
            // so left as it is a dropped connection would be a 500. LoadIntoBufferAsync wrapped it
            // the same way.
            throw new HttpRequestException($"The response body could not be read: {exception.Message}", exception);
        }

        var buffered = new ByteArrayContent(buffer.GetBuffer(), 0, (int)buffer.Length);
        foreach (var header in content.Headers)
        {
            buffered.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return buffered;
    }
}
