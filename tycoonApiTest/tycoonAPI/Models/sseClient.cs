public class SseClient
{
    public HttpResponse Response { get; }
    public CancellationToken CancellationToken { get; }

    public SseClient(HttpResponse response, CancellationToken ct)
    {
        Response = response;
        CancellationToken = ct;
    }
}
