using System.Net;
using System.Net.Http.Headers;
using System.Text;

[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Forge.Core.Tests;

/// <summary>An HTTP handler that answers from a function and remembers every request (as in Kovati Hub's tests).</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        var resp = answer(request);
        resp.RequestMessage ??= request;
        return Task.FromResult(resp);
    }

    public static HttpResponseMessage Json(string body, string? etag = null)
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (etag is not null) r.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        return r;
    }
}
