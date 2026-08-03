// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using LinFan.App.Services;
using Xunit;

namespace LinFan.App.Tests;

public class UpdateCheckServiceTests
{
    private static readonly SemVer Current = new(0, 1, 0, null);

    private static UpdateCheckService With(HttpStatusCode status, string body) =>
        new(new HttpClient(new StubHandler((_, _) => new HttpResponseMessage(status) { Content = new StringContent(body) })), "o/r");

    [Fact]
    public async Task NewerRelease_ReturnsUpdateInfo()
    {
        var svc = With(HttpStatusCode.OK,
            """{ "tag_name": "v0.2.0", "html_url": "https://github.com/o/r/releases/tag/v0.2.0" }""");

        UpdateInfo? info = await svc.CheckAsync(Current);

        Assert.NotNull(info);
        Assert.Equal("0.2.0", info!.LatestVersion);
        Assert.Equal("https://github.com/o/r/releases/tag/v0.2.0", info.ReleaseUrl);
    }

    [Fact]
    public async Task SameVersion_ReturnsNull()
    {
        var svc = With(HttpStatusCode.OK, """{ "tag_name": "0.1.0" }""");
        Assert.Null(await svc.CheckAsync(Current));
    }

    [Fact]
    public async Task OlderVersion_ReturnsNull()
    {
        var svc = With(HttpStatusCode.OK, """{ "tag_name": "0.0.9" }""");
        Assert.Null(await svc.CheckAsync(Current));
    }

    [Fact]
    public async Task NotFound_ReturnsNull()  // vor dem ersten Release
    {
        var svc = With(HttpStatusCode.NotFound, "Not Found");
        Assert.Null(await svc.CheckAsync(Current));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{ }")]                       // kein tag_name
    [InlineData("""{ "tag_name": 42 }""")]    // tag_name kein String
    [InlineData("""{ "tag_name": "garbage" }""")]
    public async Task MalformedOrMissing_ReturnsNull(string body)
    {
        var svc = With(HttpStatusCode.OK, body);
        Assert.Null(await svc.CheckAsync(Current));
    }

    [Fact]
    public async Task MissingHtmlUrl_FallsBackToReleasesLatest()
    {
        var svc = With(HttpStatusCode.OK, """{ "tag_name": "0.2.0" }""");
        UpdateInfo? info = await svc.CheckAsync(Current);
        Assert.Equal("https://github.com/o/r/releases/latest", info!.ReleaseUrl);
    }

    [Fact]
    public async Task NetworkError_ReturnsNull()
    {
        var svc = new UpdateCheckService(
            new HttpClient(new StubHandler((_, _) => throw new HttpRequestException("offline"))), "o/r");
        Assert.Null(await svc.CheckAsync(Current));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> _respond;
        public StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) => _respond = respond;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_respond(request, ct));
    }
}
