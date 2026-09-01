// -----------------------------------------------------------------------
// <copyright file="GoogleVertexDescriptor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Headers;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;

namespace Netclaw.Providers.GoogleVertex;

/// <summary>
/// Provider descriptor for Vertex AI. Authenticates with a Google Cloud
/// service-account credential — inline JSON from secrets.json or the
/// standard <c>GOOGLE_APPLICATION_CREDENTIALS</c> file — and serves Gemini
/// through Vertex AI's OpenAI-compatible endpoint. Chat transport is the
/// shared <c>OpenAiCompatibleChatClient</c>; only the endpoint shape and the
/// credential type differ from the generic openai-compatible provider.
/// </summary>
public sealed class GoogleVertexDescriptor : IProviderDescriptor
{
    /// <summary>
    /// Environment variable holding the service-account JSON file path.
    /// Read explicitly — never through ambient application-default
    /// fallbacks, so a missing or broken path fails loudly.
    /// </summary>
    public const string CredentialsPathEnvironmentVariable = "GOOGLE_APPLICATION_CREDENTIALS";

    /// <summary>Environment variable overriding the GCP project.</summary>
    public const string ProjectEnvironmentVariable = "GOOGLE_CLOUD_PROJECT";

    /// <summary>Environment variable overriding the Vertex location.</summary>
    public const string LocationEnvironmentVariable = "GOOGLE_CLOUD_LOCATION";

    private readonly HttpClient _httpClient;
    private readonly Func<GoogleVertexCredential, IGoogleAccessTokenProvider> _tokenProviderFactory;
    private readonly Func<string, string?>? _environmentLookup;

    public GoogleVertexDescriptor(HttpClient httpClient)
        : this(httpClient,
            credential => credential.CreateTokenProvider(),
            environmentLookup: null)
    {
    }

    // Test seams: production always uses the Google library and the real
    // process environment; tests bind a fake token provider and a fake
    // environment lookup so no unit test touches real credentials or
    // machine-wide state.
    internal GoogleVertexDescriptor(
        HttpClient httpClient,
        Func<GoogleVertexCredential, IGoogleAccessTokenProvider> tokenProviderFactory,
        Func<string, string?>? environmentLookup)
    {
        _httpClient = httpClient;
        _tokenProviderFactory = tokenProviderFactory;
        _environmentLookup = environmentLookup;
    }

    public string TypeKey => "google-vertex";
    public string DisplayName => "Google Vertex AI (service account)";

    // Display/override default only. The probe builds the project-scoped
    // path from the credential, so this value is never concatenated into a
    // real request URL.
    public string DefaultEndpoint => "https://aiplatform.googleapis.com";

    // The real listing path is project- and location-scoped and built per
    // credential in ProbeAsync; this value exists for the descriptor contract.
    public string ModelListingPath => "/v1/models";

    public IProviderAuth Auth { get; } = new ServiceAccountAuth();

    public async Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        GoogleVertexEndpoint vertex;
        GoogleVertexCredential credential;
        try
        {
            credential = GoogleVertexCredentialResolver.Resolve(entry, _environmentLookup);
            var vendorOptions = entry.GetVendorOptions<GoogleVertexVendorOptions>();
            vertex = GoogleVertexEndpoint.FromCredentialJson(
                credential.JsonText,
                GoogleVertexCredentialResolver.ResolveProjectId(vendorOptions, _environmentLookup),
                GoogleVertexCredentialResolver.ResolveLocation(vendorOptions, _environmentLookup));
        }
        catch (InvalidOperationException ex)
        {
            return new ProviderProbeResult(false, ex.Message, []);
        }

        string token;
        try
        {
            token = await _tokenProviderFactory(credential)
                .GetAccessTokenAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            return new ProviderProbeResult(false,
                $"google-vertex could not mint an access token from the service-account credential: {ex.Message}", []);
        }

        return await ProbeHelpers.ExecuteProbeAsync(
            _httpClient,
            TypeKey,
            vertex.BaseUri.ToString().TrimEnd('/'),
            vertex.ModelsPath,
            entryEndpoint: null,
            request => request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token),
            ProbeHelpers.ParseOpenAiStyleModels,
            ct);
    }
}
