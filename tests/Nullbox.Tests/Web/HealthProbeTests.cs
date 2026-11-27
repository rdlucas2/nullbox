using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Nullbox.Web;

namespace Nullbox.Tests.Web;

public sealed class HealthProbeTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData(null, 8080)]
    [InlineData("", 8080)]
    [InlineData("8080", 8080)]
    [InlineData("5000", 5000)]
    [InlineData("5000;6000", 5000)]
    [InlineData(" 7000 , 8000", 7000)]
    [InlineData("not-a-port", 8080)]
    [InlineData("70000", 8080)]
    public void DefaultUri_uses_first_configured_port(string? ports, int expectedPort)
    {
        var uri = HealthProbe.DefaultUri(ports);

        Assert.Equal(new Uri($"http://localhost:{expectedPort}/healthz"), uri);
    }

    [Fact]
    public async Task RunAsync_returns_0_against_a_healthy_app()
    {
        var handler = factory.Server.CreateHandler();

        var exitCode = await HealthProbe.RunAsync(new Uri("http://localhost/healthz"), handler, TextWriter.Null);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task RunAsync_returns_1_on_error_status()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        var exitCode = await HealthProbe.RunAsync(new Uri("http://localhost/healthz"), handler, TextWriter.Null);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task RunAsync_returns_1_when_unreachable()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused"));

        var exitCode = await HealthProbe.RunAsync(new Uri("http://localhost/healthz"), handler, TextWriter.Null);

        Assert.Equal(1, exitCode);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
