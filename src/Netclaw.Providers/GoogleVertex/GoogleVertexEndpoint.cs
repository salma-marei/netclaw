// -----------------------------------------------------------------------
// <copyright file="GoogleVertexEndpoint.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;

namespace Netclaw.Providers.GoogleVertex;

/// <summary>
/// Resolved Vertex AI OpenAI-compatible endpoint for one credential. The
/// GCP project derives from the credential JSON <c>project_id</c> field and
/// the location defaults to <c>global</c>; both accept an explicit override
/// through <see cref="GoogleVertexVendorOptions"/>.
/// </summary>
public sealed record GoogleVertexEndpoint(
    Uri BaseUri,
    string ChatCompletionsPath,
    string ModelsPath,
    string ProjectId,
    string Location)
{
    /// <summary>Vertex AI serves Gemini from the global endpoint by default.</summary>
    public const string DefaultLocation = "global";

    /// <summary>Scope every Vertex call needs; also the token-mint scope.</summary>
    public const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";

    public static GoogleVertexEndpoint FromCredentialJson(
        string serviceAccountJson,
        string? projectIdOverride = null,
        string? locationOverride = null)
    {
        var projectId = ResolveProjectId(serviceAccountJson, projectIdOverride);
        var location = ResolveLocation(locationOverride);
        return FromProjectAndLocation(projectId, location);
    }

    public static GoogleVertexEndpoint FromProjectAndLocation(string projectId, string location)
    {
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException(
                "google-vertex could not resolve a GCP project ID. The service-account JSON must contain a non-empty 'project_id', or VendorOptions must set ProjectId.");

        if (string.IsNullOrWhiteSpace(location))
            throw new InvalidOperationException(
                "google-vertex could not resolve a Vertex AI location. VendorOptions.Location must be a non-empty region or 'global'.");

        // The global endpoint has no region prefix; regional endpoints do.
        var host = string.Equals(location, DefaultLocation, StringComparison.OrdinalIgnoreCase)
            ? "aiplatform.googleapis.com"
            : $"{location.ToLowerInvariant()}-aiplatform.googleapis.com";

        var baseUri = new Uri($"https://{host}");
        var prefix = $"/v1/projects/{Uri.EscapeDataString(projectId)}/locations/{Uri.EscapeDataString(location)}/endpoints/openapi";

        return new GoogleVertexEndpoint(
            baseUri,
            ChatCompletionsPath: $"{prefix}/chat/completions",
            ModelsPath: $"{prefix}/models",
            ProjectId: projectId,
            Location: location);
    }

    private static string ResolveProjectId(string serviceAccountJson, string? projectIdOverride)
    {
        if (!string.IsNullOrWhiteSpace(projectIdOverride))
            return projectIdOverride;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(serviceAccountJson);
        }
        catch (JsonException ex)
        {
            // Fail before any network call: a malformed credential can only
            // produce opaque auth errors later.
            throw new InvalidOperationException(
                "google-vertex ServiceAccountJson is not valid JSON. Check the service-account credential stored in secrets.json.", ex);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("project_id", out var projectIdElement)
                || projectIdElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(projectIdElement.GetString()))
            {
                throw new InvalidOperationException(
                    "google-vertex ServiceAccountJson does not contain a 'project_id' field. Set VendorOptions.ProjectId or use a service-account credential that declares its project.");
            }

            return projectIdElement.GetString()!;
        }
    }

    private static string ResolveLocation(string? locationOverride)
        => string.IsNullOrWhiteSpace(locationOverride) ? DefaultLocation : locationOverride;
}

/// <summary>
/// Operator-owned, non-secret Vertex options bound from
/// <c>Providers:&lt;name&gt;:VendorOptions</c>. Both fields are optional;
/// absence means "derive from the credential" and "global" respectively.
/// </summary>
public sealed class GoogleVertexVendorOptions : Netclaw.Configuration.Providers.IVendorOptions
{
    public string? ProjectId { get; init; }
    public string? Location { get; init; }
}
