// -----------------------------------------------------------------------
// <copyright file="GoogleVertexProviderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Providers.GoogleVertex;
using Netclaw.Providers.SelfHosted;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

/// <summary>
/// Milestone-1 coverage for the google-vertex provider. Every test runs on
/// fake credential material (generated throwaway RSA keys) and fake token
/// providers — no test mints a real Google token or contains real secret
/// material. Tests that resolve credentials bind a fake environment lookup
/// so machine-wide environment state cannot change outcomes.
/// </summary>
public sealed class GoogleVertexProviderTests
{
    private const string FakeSse = """
        data: {"id":"abc","model":"gemini-2.5-flash","choices":[{"index":0,"delta":{"content":"hello"}}]}

        data: {"id":"abc","model":"gemini-2.5-flash","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

        data: [DONE]

        """;

    // ---- Endpoint builder (pure functions) ----

    [Fact]
    public void GlobalEndpointsDeriveFromCredentialProject()
    {
        var vertex = GoogleVertexEndpoint.FromCredentialJson(FakeCredentialJson("test-project"));

        Assert.Equal("https://aiplatform.googleapis.com", vertex.BaseUri.ToString().TrimEnd('/'));
        Assert.Equal(
            "/v1/projects/test-project/locations/global/endpoints/openapi/chat/completions",
            vertex.ChatCompletionsPath);
        Assert.Equal(
            "/v1/projects/test-project/locations/global/endpoints/openapi/models",
            vertex.ModelsPath);
        Assert.Equal("global", vertex.Location);
    }

    [Fact]
    public void LocationOverrideProducesRegionalHost()
    {
        var vertex = GoogleVertexEndpoint.FromCredentialJson(
            FakeCredentialJson("test-project"), locationOverride: "us-central1");

        Assert.Equal(
            "https://us-central1-aiplatform.googleapis.com",
            vertex.BaseUri.ToString().TrimEnd('/'));
        Assert.Contains("/locations/us-central1/", vertex.ChatCompletionsPath);
    }

    [Fact]
    public void ProjectIdOverrideWinsOverCredential()
    {
        var vertex = GoogleVertexEndpoint.FromCredentialJson(
            FakeCredentialJson("cred-project"), projectIdOverride: "override-project");

        Assert.Equal("override-project", vertex.ProjectId);
        Assert.Contains("/projects/override-project/", vertex.ChatCompletionsPath);
    }

    [Fact]
    public void MalformedCredentialFailsBeforeAnyNetworkCall()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => GoogleVertexEndpoint.FromCredentialJson("{not json"));

        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void CredentialWithoutProjectFailsLoudly()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => GoogleVertexEndpoint.FromCredentialJson("""{"type":"service_account"}"""));

        Assert.Contains("project_id", ex.Message);
    }

    // ---- Credential resolver ----

    [Fact]
    public void ResolvePrefersInlineJsonOverEnvironmentFile()
    {
        var credential = GoogleVertexCredentialResolver.Resolve(
            new ProviderEntry { ServiceAccountJson = new(FakeCredentialJson("inline-project")) },
            environmentLookup: _ => "/tmp/env-sa.json");

        var inline = Assert.IsType<GoogleVertexCredential.InlineCredential>(credential);
        Assert.Contains("inline-project", inline.JsonText);
    }

    [Fact]
    public void ResolveUsesEnvironmentFilePathWhenNoInline()
    {
        var path = WriteTempCredentialFile(FakeCredentialJson("file-project"));

        var credential = GoogleVertexCredentialResolver.Resolve(
            new ProviderEntry { Type = "google-vertex" },
            environmentLookup: name => name == GoogleVertexDescriptor.CredentialsPathEnvironmentVariable
                ? path
                : null);

        var file = Assert.IsType<GoogleVertexCredential.FileCredential>(credential);
        Assert.Equal(path, file.Path);
        Assert.Contains("file-project", credential.JsonText);
    }

    [Fact]
    public void ResolveFailsLoudlyWithoutAnyCredential()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            GoogleVertexCredentialResolver.Resolve(
                new ProviderEntry { Type = "google-vertex" }, environmentLookup: _ => null));

        Assert.Contains("GOOGLE_APPLICATION_CREDENTIALS", ex.Message);
        Assert.Contains("ServiceAccountJson", ex.Message);
    }

    [Fact]
    public void ResolveFailsLoudlyWhenEnvironmentFileMissing()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            GoogleVertexCredentialResolver.Resolve(
                new ProviderEntry { Type = "google-vertex" },
                environmentLookup: name => name == GoogleVertexDescriptor.CredentialsPathEnvironmentVariable
                    ? @"C:\nonexistent\sa-key.json"
                    : null));

        Assert.Contains("missing file", ex.Message);
    }

    [Fact]
    public void ProjectAndLocationPreferVendorOptionsOverEnvironment()
    {
        var options = new GoogleVertexVendorOptions { ProjectId = "vendor-project", Location = "europe-west4" };

        Assert.Equal("vendor-project", GoogleVertexCredentialResolver.ResolveProjectId(
            options, _ => "env-project"));
        Assert.Equal("europe-west4", GoogleVertexCredentialResolver.ResolveLocation(
            options, _ => "us-central1"));
    }

    [Fact]
    public void ProjectAndLocationFallBackToEnvironment()
    {
        Func<string, string?> lookup = name => name switch
        {
            GoogleVertexDescriptor.ProjectEnvironmentVariable => "env-project",
            GoogleVertexDescriptor.LocationEnvironmentVariable => "us-central1",
            _ => null,
        };

        Assert.Equal("env-project", GoogleVertexCredentialResolver.ResolveProjectId(null, lookup));
        Assert.Equal("us-central1", GoogleVertexCredentialResolver.ResolveLocation(null, lookup));
    }

    // ---- Token handler ----

    [Fact]
    public async Task TokenHandlerInjectsBearerFromTokenProvider()
    {
        HttpRequestMessage? captured = null;
        using var handler = new GoogleVertexTokenHandler(
            new FakeTokenProvider("fake-token"), new RecordingHandler(_ =>
            {
                return PlainResponse("{}");
            }, request => captured = request));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://aiplatform.googleapis.com") };

        await client.GetAsync("/v1/models", TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal("fake-token", captured.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task TokenHandlerSkipsMintWhenHeaderPresent()
    {
        var tokenProvider = new FakeTokenProvider("unused");
        using var handler = new GoogleVertexTokenHandler(
            tokenProvider, new RecordingHandler(_ => PlainResponse("{}"), _ => { }));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://aiplatform.googleapis.com") };

        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Authorization = new("Bearer", "operator-token");
        await client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(0, tokenProvider.MintCount);
    }

    [Fact]
    public void FileCredentialTokenProviderConstructsOffline()
    {
        var path = WriteTempCredentialFile(RealisticFakeCredentialJson());
        var credential = GoogleVertexCredentialResolver.Resolve(
            new ProviderEntry { Type = "google-vertex" },
            environmentLookup: name => name == GoogleVertexDescriptor.CredentialsPathEnvironmentVariable
                ? path
                : null);

        var provider = credential.CreateTokenProvider();

        Assert.NotNull(provider);
        // Both credential forms must take the scoped OAuth2 token-exchange
        // path — a raw file credential would mint a self-signed JWT that
        // Vertex rejects with 401.
        Assert.IsType<GoogleServiceAccountTokenProvider>(provider);
    }

    // ---- Chat transport composition ----

    [Fact]
    public async Task ChatRequestReachesProjectScopedVertexPathWithToken()
    {
        HttpRequestMessage? captured = null;
        using var recording = new RecordingHandler(_ => SseResponse(FakeSse), request => captured = request);

        var vertex = GoogleVertexEndpoint.FromCredentialJson(FakeCredentialJson("test-project"));
        var endpoint = new OpenAiCompatibleEndpoint(
            vertex.BaseUri, vertex.ChatCompletionsPath, vertex.ModelsPath);
        using var httpClient = new HttpClient(
            new GoogleVertexTokenHandler(new FakeTokenProvider("fake-token"), recording))
        {
            BaseAddress = vertex.BaseUri
        };
        var client = new OpenAiCompatibleChatClient(httpClient, endpoint, "gemini-2.5-flash");

        var text = await CollectStreamingTextAsync(client);

        Assert.Equal("hello", text);
        Assert.NotNull(captured);
        Assert.Equal(
            "https://aiplatform.googleapis.com/v1/projects/test-project/locations/global/endpoints/openapi/chat/completions",
            captured.RequestUri?.ToString());
        Assert.Equal("fake-token", captured.Headers.Authorization?.Parameter);
    }

    // ---- Probe ----

    [Fact]
    public async Task ProbeFailsLoudlyWithoutAnyCredential()
    {
        var descriptor = new GoogleVertexDescriptor(
            new HttpClient(), credential => new FakeTokenProvider("unused"), environmentLookup: _ => null);

        var result = await descriptor.ProbeAsync(
            new ProviderEntry { Type = "google-vertex" }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("GOOGLE_APPLICATION_CREDENTIALS", result.ErrorMessage);
        Assert.Contains("ServiceAccountJson", result.ErrorMessage);
    }

    [Fact]
    public async Task ProbeRejectsMalformedCredentialBeforeNetwork()
    {
        var descriptor = new GoogleVertexDescriptor(
            new HttpClient(), credential => new FakeTokenProvider("unused"), environmentLookup: _ => null);

        var result = await descriptor.ProbeAsync(
            new ProviderEntry { Type = "google-vertex", ServiceAccountJson = new("{not json") },
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("not valid JSON", result.ErrorMessage);
    }

    [Fact]
    public async Task ProbeListsModelsWithDerivedToken()
    {
        HttpRequestMessage? captured = null;
        using var recording = new RecordingHandler(
            _ => JsonResponse("""
                {"object":"list","data":[{"id":"gemini-2.5-flash"},{"id":"gemini-2.5-pro"}]}
                """),
            request => captured = request);
        var descriptor = new GoogleVertexDescriptor(
            new HttpClient(recording), credential => new FakeTokenProvider("fake-token"), environmentLookup: _ => null);

        var result = await descriptor.ProbeAsync(
            new ProviderEntry
            {
                Type = "google-vertex",
                AuthMethod = AuthMethod.ServiceAccount,
                ServiceAccountJson = new(FakeCredentialJson("test-project"))
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(captured);
        Assert.Equal(
            "https://aiplatform.googleapis.com/v1/projects/test-project/locations/global/endpoints/openapi/models",
            captured.RequestUri?.ToString());
        Assert.Equal("fake-token", captured.Headers.Authorization?.Parameter);
        Assert.Contains(result.Models, m => m.ModelId.Value == "gemini-2.5-flash");
    }

    [Fact]
    public async Task ProbeUsesEnvironmentCredentialFile()
    {
        var credentialPath = WriteTempCredentialFile(RealisticFakeCredentialJson());
        HttpRequestMessage? captured = null;
        using var recording = new RecordingHandler(
            _ => JsonResponse("""
                {"object":"list","data":[{"id":"gemini-2.5-flash"}]}
                """),
            request => captured = request);
        Func<string, string?> lookup = name => name switch
        {
            GoogleVertexDescriptor.CredentialsPathEnvironmentVariable => credentialPath,
            GoogleVertexDescriptor.ProjectEnvironmentVariable => "env-project",
            _ => null,
        };
        var descriptor = new GoogleVertexDescriptor(
            new HttpClient(recording), credential => new FakeTokenProvider("fake-token"), lookup);

        var result = await descriptor.ProbeAsync(
            new ProviderEntry { Type = "google-vertex", AuthMethod = AuthMethod.ServiceAccount },
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(captured);
        Assert.Equal(
            "https://aiplatform.googleapis.com/v1/projects/env-project/locations/global/endpoints/openapi/models",
            captured.RequestUri?.ToString());
        Assert.Equal("fake-token", captured.Headers.Authorization?.Parameter);
    }

    // ---- Plugin ----

    [Fact]
    public void PluginFailsWithoutCredential()
    {
        var plugin = new GoogleVertexProviderPlugin(
            new GoogleVertexDescriptor(new HttpClient()),
            NullLoggerFactory.Instance,
            environmentLookup: _ => null);

        Assert.Throws<InvalidOperationException>(() => plugin.CreateChatClient(
            new ProviderEntry { Type = "google-vertex" },
            new ModelReference { Provider = "gcp", ModelId = "gemini-2.5-flash" }));
    }

    [Fact]
    public void PluginBuildsClientFromFakeCredential()
    {
        // Uses a generated throwaway RSA key so GoogleCredential construction
        // succeeds offline. No token mint happens during construction.
        var plugin = new GoogleVertexProviderPlugin(
            new GoogleVertexDescriptor(new HttpClient()), NullLoggerFactory.Instance);

        var client = plugin.CreateChatClient(
            new ProviderEntry
            {
                Type = "google-vertex",
                AuthMethod = AuthMethod.ServiceAccount,
                ServiceAccountJson = new(RealisticFakeCredentialJson())
            },
            new ModelReference { Provider = "gcp", ModelId = "gemini-2.5-flash" });

        Assert.NotNull(client);
        Assert.IsType<OpenAiCompatibleChatClient>(client);
    }

    [Fact]
    public void PluginBuildsClientFromEnvironmentCredentialFile()
    {
        var credentialPath = WriteTempCredentialFile(RealisticFakeCredentialJson());
        var plugin = new GoogleVertexProviderPlugin(
            new GoogleVertexDescriptor(new HttpClient()),
            NullLoggerFactory.Instance,
            environmentLookup: name => name == GoogleVertexDescriptor.CredentialsPathEnvironmentVariable
                ? credentialPath
                : null);

        var client = plugin.CreateChatClient(
            new ProviderEntry { Type = "google-vertex", AuthMethod = AuthMethod.ServiceAccount },
            new ModelReference { Provider = "gcp", ModelId = "gemini-2.5-flash" });

        Assert.NotNull(client);
        Assert.IsType<OpenAiCompatibleChatClient>(client);
    }

    [Fact]
    public void NormalizePublisherModelId_PrefixesBareGeminiId()
    {
        Assert.Equal("google/gemini-2.5-flash", GoogleVertexProviderPlugin.NormalizePublisherModelId("gemini-2.5-flash"));
    }

    [Fact]
    public void NormalizePublisherModelId_PassesThroughPublisherQualifiedId()
    {
        Assert.Equal("google/gemini-2.5-flash", GoogleVertexProviderPlugin.NormalizePublisherModelId("google/gemini-2.5-flash"));
        Assert.Equal("meta/llama-3-70b", GoogleVertexProviderPlugin.NormalizePublisherModelId("meta/llama-3-70b"));
    }

    // ---- Helpers ----

    private static async Task<string> CollectStreamingTextAsync(IChatClient client)
    {
        var text = new StringBuilder();
        await foreach (var update in client.GetStreamingResponseAsync(
            [new ChatMessage(ChatRole.User, "test")],
            cancellationToken: TestContext.Current.CancellationToken))
        {
            foreach (var content in update.Contents)
            {
                if (content is TextContent { Text: { } piece })
                    text.Append(piece);
            }
        }

        return text.ToString();
    }

    private static HttpResponseMessage PlainResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage JsonResponse(string body) => PlainResponse(body);

    private static HttpResponseMessage SseResponse(string sse) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
    };

    private static string FakeCredentialJson(string projectId) => $$"""
        {"type":"service_account","project_id":"{{projectId}}"}
        """;

    /// <summary>
    /// Writes fake credential JSON to a temp file for environment-file tests.
    /// The file is fake material; deletion on process exit is acceptable.
    /// </summary>
    private static string WriteTempCredentialFile(string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "netclaw-tests", "vertex-credentials");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>
    /// Structurally valid service-account JSON for offline GoogleCredential
    /// construction. The RSA key is generated in-test and grants nothing.
    /// </summary>
    private static string RealisticFakeCredentialJson()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var key = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
        return $$"""
            {
              "type": "service_account",
              "project_id": "test-project",
              "client_email": "sa@test-project.iam.gserviceaccount.com",
              "private_key": "-----BEGIN PRIVATE KEY-----\n{{key}}\n-----END PRIVATE KEY-----\n"
            }
            """;
    }

    private sealed class FakeTokenProvider(string token) : IGoogleAccessTokenProvider
    {
        public int MintCount { get; private set; }

        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            MintCount++;
            return Task.FromResult(token);
        }
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        Action<HttpRequestMessage>? onRequest = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onRequest?.Invoke(request);
            return Task.FromResult(responder(request));
        }
    }
}
