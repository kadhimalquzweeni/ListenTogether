using System.Net;
using ListenTogether.App.Networking;

namespace ListenTogether.Tests.Networking;

public sealed class HostReadinessCheckerTests
{
    [Fact]
    public async Task CheckAsync_WhenHealthAndStunSucceed_ReturnsReadyWithExternalCheckCaveat()
    {
        var checker = new HostReadinessChecker(new HttpClient(new StubHandler(HttpStatusCode.OK)), new StubProbe());
        var result = await checker.CheckAsync(new Uri("https://listen.example"));
        Assert.True(result.CanHost);
        Assert.Contains("different internet connection", result.Message);
        Assert.Contains("router port forwarding", result.Message);
    }

    [Fact]
    public async Task CheckAsync_WhenHealthFails_DoesNotProbeStun()
    {
        var probe = new StubProbe();
        var checker = new HostReadinessChecker(new HttpClient(new StubHandler(HttpStatusCode.ServiceUnavailable)), probe);
        var result = await checker.CheckAsync(new Uri("https://listen.example"));
        Assert.False(result.CanHost);
        Assert.Contains("HTTP 503", result.Message);
        Assert.False(probe.Called);
    }

    [Fact]
    public async Task CheckAsync_WhenStunFails_ReturnsRouterGuidance()
    {
        var checker = new HostReadinessChecker(
            new HttpClient(new StubHandler(HttpStatusCode.OK)), new StubProbe(new TimeoutException("No response")));
        var result = await checker.CheckAsync(new Uri("https://listen.example"));
        Assert.False(result.CanHost);
        Assert.Contains("UDP STUN", result.Message);
        Assert.Contains("3478", result.Message);
        Assert.Contains("router", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Assert.Equal("/healthz", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class StubProbe(Exception? failure = null) : IStunEndpointProbe
    {
        public bool Called { get; private set; }
        public Task CheckAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            Called = true;
            Assert.Equal("listen.example", host);
            Assert.Equal(3478, port);
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }
    }
}
