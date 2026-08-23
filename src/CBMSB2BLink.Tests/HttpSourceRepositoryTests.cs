using System;
using System.Collections.Generic;
using System.Net;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using CBMSB2BLink.Core.Models;
using CBMSB2BLink.Core.Options;
using CBMSB2BLink.Data;
using Microsoft.Extensions.Options;
using Xunit;

namespace CBMSB2BLink.Tests;

public class HttpSourceRepositoryTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Respond(request));
        }
    }

    [Fact]
    public async Task GetNewRecordsAsync_SendsExpectedUrlAndApiKeyHeader()
    {
        var handler = new FakeHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new List<BcbRecord>())
            }
        };
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://bridge.local/") };
        var options = Options.Create(new FallbackBridgeOptions { BaseUrl = "http://bridge.local/", ApiKey = "secret-key" });
        var repo = new HttpSourceRepository(httpClient, options);

        await repo.GetNewRecordsAsync(42, 100, CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("http://bridge.local/api/bcb-new?lastRowId=42&batchSize=100", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("secret-key", handler.LastRequest.Headers.GetValues("X-Api-Key").Single());
    }

    [Fact]
    public async Task GetNewRecordsAsync_DeserializesRecords()
    {
        var expected = new List<BcbRecord>
        {
            new() { RowId = 1, BcbCmsNo = 1, BcbIdNo1 = "ID1", BcbCreateDate = new DateTime(2026, 1, 1) },
            new() { RowId = 2, BcbCmsNo = 2, BcbIdNo1 = "ID2", BcbCreateDate = new DateTime(2026, 1, 2) }
        };
        var handler = new FakeHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(expected) }
        };
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://bridge.local/") };
        var options = Options.Create(new FallbackBridgeOptions { BaseUrl = "http://bridge.local/", ApiKey = "secret-key" });
        var repo = new HttpSourceRepository(httpClient, options);

        var result = await repo.GetNewRecordsAsync(0, 10, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal("ID1", result[0].BcbIdNo1);
        Assert.Equal("ID2", result[1].BcbIdNo1);
    }

    [Fact]
    public async Task GetNewRecordsAsync_NonSuccessStatus_Throws()
    {
        var handler = new FakeHandler
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        };
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://bridge.local/") };
        var options = Options.Create(new FallbackBridgeOptions { BaseUrl = "http://bridge.local/", ApiKey = "wrong-key" });
        var repo = new HttpSourceRepository(httpClient, options);

        await Assert.ThrowsAsync<HttpRequestException>(() => repo.GetNewRecordsAsync(0, 10, CancellationToken.None));
    }
}
