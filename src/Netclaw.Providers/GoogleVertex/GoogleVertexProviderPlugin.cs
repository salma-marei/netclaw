// -----------------------------------------------------------------------
// <copyright file="GoogleVertexProviderPlugin.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Concurrent;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;
using Netclaw.Providers.SelfHosted;

namespace Netclaw.Providers.GoogleVertex;

/// <summary>
/// Daemon-side plugin for Vertex AI. Builds the shared
/// <c>OpenAiCompatibleChatClient</c> on the project-scoped Vertex endpoint
/// and injects the Google access token through an HTTP handler. Token
/// providers are cached per credential so Google's internal token cache
/// survives per-call client construction.
/// </summary>
public sealed class GoogleVertexProviderPlugin : ProviderPluginBase<GoogleVertexDescriptor>
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<string, string?>? _environmentLookup;
    private readonly ConcurrentDictionary<string, IGoogleAccessTokenProvider> _tokenProviders =
        new(StringComparer.Ordinal);

    public GoogleVertexProviderPlugin(GoogleVertexDescriptor descriptor, ILoggerFactory loggerFactory)
        : this(descriptor, loggerFactory, environmentLookup: null)
    {
    }

    // Test seam: lets tests run credential resolution against a fake
    // environment instead of machine-wide state.
    internal GoogleVertexProviderPlugin(
        GoogleVertexDescriptor descriptor,
        ILoggerFactory loggerFactory,
        Func<string, string?>? environmentLookup)
        : base(descriptor)
    {
        _loggerFactory = loggerFactory;
        _environmentLookup = environmentLookup;
    }

    public override IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
    {
        var credential = GoogleVertexCredentialResolver.Resolve(entry, _environmentLookup);

        var vendorOptions = entry.GetVendorOptions<GoogleVertexVendorOptions>();
        var vertex = GoogleVertexEndpoint.FromCredentialJson(
            credential.JsonText,
            GoogleVertexCredentialResolver.ResolveProjectId(vendorOptions, _environmentLookup),
            GoogleVertexCredentialResolver.ResolveLocation(vendorOptions, _environmentLookup));

        var tokenProvider = _tokenProviders.GetOrAdd(
            credential.CacheKey, _ => credential.CreateTokenProvider());

        var httpClient = new HttpClient(
            new GoogleVertexTokenHandler(
                tokenProvider, new SessionAffinityHandler()),
            disposeHandler: true)
        {
            BaseAddress = vertex.BaseUri,
            Timeout = TimeSpan.FromHours(1)
        };

        var endpoint = new OpenAiCompatibleEndpoint(
            vertex.BaseUri, vertex.ChatCompletionsPath, vertex.ModelsPath);

        return new OpenAiCompatibleChatClient(
            httpClient,
            endpoint,
            NormalizePublisherModelId(model.ModelId),
            OpenAiCompatibleWireProfile.Generic,
            _loggerFactory.CreateLogger<OpenAiCompatibleChatClient>());
    }

    /// <summary>
    /// Vertex's OpenAI-compatible endpoint rejects bare model IDs: the
    /// request body's <c>model</c> field must be publisher-qualified
    /// (<c>google/gemini-2.5-flash</c>). Bare Gemini IDs are normalized to
    /// the <c>google/</c> publisher; IDs that already carry a publisher
    /// (Gemini, partner models) pass through unchanged.
    /// </summary>
    internal static string NormalizePublisherModelId(string modelId)
        => modelId.Contains('/', StringComparison.Ordinal)
            ? modelId
            : $"google/{modelId}";
}
