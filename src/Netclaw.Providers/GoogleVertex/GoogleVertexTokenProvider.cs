// -----------------------------------------------------------------------
// <copyright file="GoogleVertexTokenProvider.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Google.Apis.Auth.OAuth2;

namespace Netclaw.Providers.GoogleVertex;

/// <summary>
/// Mints Google OAuth2 access tokens for one service-account credential.
/// The seam keeps <c>Google.Apis.Auth</c> out of automated tests: tests bind
/// a fake provider and never touch real credential material.
/// </summary>
internal interface IGoogleAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Production token source. Google's library signs the service-account JWT,
/// exchanges it for an access token, caches the result, and refreshes it
/// near expiry — Netclaw owns no expiry logic and persists no token.
/// </summary>
internal sealed class GoogleServiceAccountTokenProvider : IGoogleAccessTokenProvider
{
    private readonly GoogleCredential _credential;

    public GoogleServiceAccountTokenProvider(string serviceAccountJson)
    {
        _credential = CredentialFactory
            .FromJson(serviceAccountJson, JsonCredentialParameters.ServiceAccountCredentialType)
            .CreateScoped(GoogleVertexEndpoint.CloudPlatformScope);
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
        => await ((ITokenAccess)_credential).GetAccessTokenForRequestAsync(
            cancellationToken: cancellationToken);
}

/// <summary>
/// Injects the current Google access token as
/// <c>Authorization: Bearer</c> on requests that carry no auth header. The
/// shared <c>OpenAiCompatibleChatClient</c> leaves the header unset when its
/// static API key is null, so this handler is the sole auth source.
/// </summary>
internal sealed class GoogleVertexTokenHandler : DelegatingHandler
{
    private readonly IGoogleAccessTokenProvider _tokenProvider;

    public GoogleVertexTokenHandler(IGoogleAccessTokenProvider tokenProvider)
    {
        _tokenProvider = tokenProvider;
    }

    public GoogleVertexTokenHandler(IGoogleAccessTokenProvider tokenProvider, HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
        _tokenProvider = tokenProvider;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is null)
        {
            var token = await _tokenProvider.GetAccessTokenAsync(cancellationToken);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
