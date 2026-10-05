using System.Net;
using System.Text;
using Deduplicator.Core.Services;

namespace Deduplicator.Core.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task Valid_release_is_parsed_and_compared_without_downloading_assets()
    {
        var handler = new StubHandler("""
            {"tag_name":"v1.2.0","html_url":"https://github.com/acme/deduplicator/releases/tag/v1.2.0","body":"Что нового"}
            """);
        var service = new UpdateService(new HttpClient(handler), "1.0.0");

        var result = await service.CheckAsync("acme/deduplicator");

        Assert.NotNull(result);
        Assert.True(result!.IsNewer);
        Assert.Equal("v1.2.0", result.LatestVersion);
        Assert.StartsWith("https://github.com/", result.Url);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Invalid_repository_and_untrusted_release_url_are_not_opened()
    {
        var handler = new StubHandler("""
            {"tag_name":"v9.0.0","html_url":"https://example.com/fake.exe","body":"x"}
            """);
        var service = new UpdateService(new HttpClient(handler), "1.0.0");

        Assert.Null(await service.CheckAsync("owner/repo/extra"));
        Assert.Equal(0, handler.Requests);
        var result = await service.CheckAsync("owner/repo");
        Assert.NotNull(result);
        Assert.Empty(result!.Url);
    }

    [Fact]
    public async Task Oversized_response_is_rejected_before_json_materialization()
    {
        var handler = new StubHandler(new string(' ', 1024 * 1024 + 1));
        var service = new UpdateService(new HttpClient(handler), "1.0.0");
        Assert.Null(await service.CheckAsync("owner/repo"));
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
