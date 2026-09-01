// -----------------------------------------------------------------------
// <copyright file="GoogleVertexCredentialResolver.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Cryptography;
using System.Text;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;

namespace Netclaw.Providers.GoogleVertex;

/// <summary>
/// Resolves the Vertex credential and endpoint inputs for one provider
/// entry. Precedence: explicit VendorOptions first, then the operator
/// environment (<c>GOOGLE_CLOUD_PROJECT</c>, <c>GOOGLE_CLOUD_LOCATION</c>),
/// then derivation from the credential itself. The resolver reads
/// <c>GOOGLE_APPLICATION_CREDENTIALS</c> explicitly and never falls back to
/// ambient application-default credentials: a missing credential is a loud
/// configuration error, not a silent identity switch.
/// </summary>
internal static class GoogleVertexCredentialResolver
{
    public static GoogleVertexCredential Resolve(
        ProviderEntry entry, Func<string, string?>? environmentLookup = null)
    {
        var inlineJson = entry.ServiceAccountJson?.Value;
        if (!string.IsNullOrWhiteSpace(inlineJson))
            return new GoogleVertexCredential.InlineCredential(inlineJson);

        var path = GoogleVertexCredentialResolver.Lookup(
            GoogleVertexDescriptor.CredentialsPathEnvironmentVariable, environmentLookup);
        if (!string.IsNullOrWhiteSpace(path))
        {
            if (!File.Exists(path))
                throw new InvalidOperationException(
                    $"google-vertex GOOGLE_APPLICATION_CREDENTIALS points to a missing file: '{path}'. Restore the file or update the environment variable.");

            return new GoogleVertexCredential.FileCredential(path);
        }

        throw new InvalidOperationException(
            "google-vertex has no service-account credential. Set GOOGLE_APPLICATION_CREDENTIALS to the "
            + "service-account JSON file path, or store the full JSON as "
            + "Providers:<name>.ServiceAccountJson in secrets.json.");
    }

    public static string? ResolveProjectId(
        GoogleVertexVendorOptions? options, Func<string, string?>? environmentLookup = null)
        => FirstNonEmpty(
            options?.ProjectId,
            Lookup(GoogleVertexDescriptor.ProjectEnvironmentVariable, environmentLookup));

    public static string? ResolveLocation(
        GoogleVertexVendorOptions? options, Func<string, string?>? environmentLookup = null)
        => FirstNonEmpty(
            options?.Location,
            Lookup(GoogleVertexDescriptor.LocationEnvironmentVariable, environmentLookup));

    public static string? Lookup(string name, Func<string, string?>? environmentLookup)
        => (environmentLookup ?? Environment.GetEnvironmentVariable)(name);

    private static string? FirstNonEmpty(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first) ? first : second;
}

/// <summary>
/// A resolved service-account credential. Both forms hand the same JSON
/// text to the endpoint builder, so project derivation behaves identically
/// regardless of where the credential lives.
/// </summary>
internal abstract record GoogleVertexCredential
{
    /// <summary>
    /// Credential JSON text. File sources read on demand with loud IO
    /// errors so an unreadable file cannot degrade into an opaque auth
    /// failure mid-request.
    /// </summary>
    public abstract string JsonText { get; }

    public abstract IGoogleAccessTokenProvider CreateTokenProvider();

    /// <summary>
    /// Stable token-provider cache key. Inline keys hash the content; file
    /// keys hash path plus last-write time, so a rotated key file is picked
    /// up without a daemon restart.
    /// </summary>
    public abstract string CacheKey { get; }

    internal sealed record InlineCredential(string Json) : GoogleVertexCredential
    {
        public override string JsonText => Json;

        public override IGoogleAccessTokenProvider CreateTokenProvider() =>
            new GoogleServiceAccountTokenProvider(Json);

        public override string CacheKey => "inline:" + Hash(Json);
    }

    internal sealed record FileCredential(string Path) : GoogleVertexCredential
    {
        public override string JsonText
        {
            get
            {
                try
                {
                    return File.ReadAllText(Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException(
                        $"google-vertex could not read the GOOGLE_APPLICATION_CREDENTIALS file '{Path}': {ex.Message}", ex);
                }
            }
        }

        public override IGoogleAccessTokenProvider CreateTokenProvider() =>
            new GoogleServiceAccountTokenProvider(JsonText);

        public override string CacheKey
            => "file:" + Hash(Path + '|' + File.GetLastWriteTimeUtc(Path).Ticks);
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
