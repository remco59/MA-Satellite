using System.Net;
using System.Text;

namespace MaSatellite.Host.Tests.NowPlaying;

/// <summary>An HttpMessageHandler that answers from a function and records every request URL.</summary>
internal sealed class StubHttp : HttpMessageHandler
{
    private readonly Func<Uri, (HttpStatusCode Status, string Body)> _respond;

    public StubHttp(Func<Uri, (HttpStatusCode, string)> respond) => _respond = respond;

    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        var (status, body) = _respond(request.RequestUri!);
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    public HttpClient Client() => new(this);
}
