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
/// The body is buffered here, inside the handler, rather than being wrapped in a counting stream.
/// That costs the streaming of a person thumbnail, which is at most a few hundred kilobytes, and
/// buys two things worth more: the cap is enforced by the framework rather than by a stream this
/// repository would have to get right, and the whole read happens inside <c>SendAsync</c>, so the
/// client's timeout covers the body and not just the headers.
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
            // Checked before reading as well as while reading: a declared length spares us buffering
            // the whole of something we are going to refuse anyway.
            var declared = response.Content.Headers.ContentLength;
            if (declared > _maxBytes)
            {
                throw new ResponseTooLargeException(_maxBytes, declared);
            }

            // .NET 8 has no overload taking a token, and it does not need one here: this runs inside
            // the handler chain, so the client's own timeout is still registered against the
            // connection and aborts a stalled read exactly as it would a stalled set of headers.
            await response.Content.LoadIntoBufferAsync(_maxBytes).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
            when (exception is not ResponseTooLargeException && exception.InnerException is null)
        {
            response.Dispose();

            // LoadIntoBufferAsync reports the overrun and a mid-body connection failure as the same
            // type, and only the overrun arrives bare: the copy re-wraps an IOException with itself
            // as the inner exception, while the buffer-size check throws with none. Discriminating
            // on that beats matching a framework message, and both halves are pinned against the
            // real implementation in AdminImmichClientBoundsTests rather than left as a reading of it.
            throw new ResponseTooLargeException(_maxBytes, null);
        }
        catch
        {
            response.Dispose();
            throw;
        }

        return response;
    }
}
