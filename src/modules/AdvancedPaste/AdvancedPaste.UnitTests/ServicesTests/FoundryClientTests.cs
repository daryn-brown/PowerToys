// Copyright (c) Microsoft Corporation
// The Microsoft Corporation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LanguageModelProvider.FoundryLocal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AdvancedPaste.UnitTests.ServicesTests;

[TestClass]
public sealed class FoundryClientTests
{
    private static readonly Uri ServiceUri = new("http://127.0.0.1:5273");

    [TestMethod]
    public async Task ListCachedModels_WithCurrentApiPayload_ParsesModelIds()
    {
        const string payload = """
            {
              "data": [
                {
                  "id": "phi-4-mini-instruct-generic-gpu:3",
                  "object": "model",
                  "created": 1710000000,
                  "owned_by": "Microsoft"
                }
              ],
              "IsDelta": false,
              "Successful": true,
              "HttpStatusCode": 0,
              "object": "list"
            }
            """;

        HttpMethod? requestMethod = null;
        string? requestPath = null;
        bool acceptsJson = false;
        bool hasUserAgent = false;
        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            requestMethod = request.Method;
            requestPath = request.RequestUri?.AbsolutePath;
            acceptsJson = request.Headers.Accept.Any(header => header.MediaType == "application/json");
            hasUserAgent = request.Headers.UserAgent.Any();
            return JsonResponse(payload);
        }));
        using var client = new FoundryClient(httpClient, ServiceUri);

        var models = await client.ListCachedModels();

        Assert.AreEqual(HttpMethod.Get, requestMethod);
        Assert.AreEqual("/v1/models", requestPath);
        Assert.IsTrue(acceptsJson);
        Assert.IsTrue(hasUserAgent);
        Assert.AreEqual(1, models.Count);
        Assert.AreEqual("phi-4-mini-instruct-generic-gpu:3", models[0].Name);
    }

    [TestMethod]
    public async Task EnsureModelLoaded_WithCurrentApi_UsesNewManagementEndpoints()
    {
        const string modelId = "phi-4-mini-instruct-generic-gpu:3";
        var loadedModelRequests = 0;
        var loadRequested = false;

        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            var requestPath = Uri.UnescapeDataString(request.RequestUri?.AbsolutePath ?? string.Empty);
            if (requestPath == "/models/loaded")
            {
                loadedModelRequests++;
                return JsonResponse(loadedModelRequests == 1 ? "[]" : $"[\"{modelId}\"]");
            }

            if (requestPath == $"/models/load/{modelId}")
            {
                loadRequested = true;
                return JsonResponse("""{"status":"loaded"}""");
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        using var client = new FoundryClient(httpClient, ServiceUri);

        var loaded = await client.EnsureModelLoaded(modelId);

        Assert.IsTrue(loaded);
        Assert.IsTrue(loadRequested);
        Assert.AreEqual(2, loadedModelRequests);
    }

    private static HttpResponseMessage JsonResponse(string content)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_handler(request));
        }
    }
}
