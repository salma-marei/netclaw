## Context

The internal Vertex reference setup authenticates with one secret: the full
service-account JSON as a string. Google's client library parses it, scopes
it to `cloud-platform`, derives `project_id`, and mints short-lived access
tokens for Vertex calls. The location is `global`.

Netclaw's provider architecture already has every seam this needs:

- `ILlmProviderPlugin.CreateChatClient(ProviderEntry, ModelReference)` is the
  per-provider construction point (`ProviderPluginFactory` dispatches on
  `ProviderEntry.Type`).
- `OpenAiCompatibleChatClient` speaks the OpenAI chat-completions dialect,
  including SSE streaming and tool calls. It reads `Authorization` from a
  static `ApiKey` on `OpenAiCompatibleEndpoint` at request time and sends no
  header when that field is null.
- `OpenAiCompatibleEndpoint` is a record with explicit
  `ChatCompletionsPath` and `ModelsPath`, so a plugin can pin exact Vertex
  paths without the `FromBaseUrl` version-suffix heuristic.
- `secrets.json` + `SensitiveString` already carry per-provider secrets and
  decrypt `ENC:` values at bind time.

Vertex AI exposes an OpenAI-compatible surface at
`https://aiplatform.googleapis.com/v1/projects/{project}/locations/global/endpoints/openapi/`
(chat: `chat/completions`, discovery: `models`). Regional locations use the
`{region}-aiplatform.googleapis.com` host.

## Goals / Non-Goals

**Goals:**

- Milestone 1 proves one path: service-account JSON to Google auth to Vertex
  to Gemini to a simple text response.
- The secret is the inline JSON string only, stored through the existing
  encrypted secrets path.
- `project_id` derives from the credential JSON. Location defaults to
  `global`; `VendorOptions` can override both.
- All token handling delegates to `Google.Apis.Auth`.

**Non-Goals:**

- Ambient application-default fallbacks beyond the explicit
  `GOOGLE_APPLICATION_CREDENTIALS` read (gcloud ADC chain, metadata server).
- Interactive wizard or provider-manager TUI flows.
- Thought-signature handling, audio changes, Express-mode API keys.
- Any use of the heavyweight Vertex gRPC SDK or the GenAI SDK. The reference
  Python client uses `google-genai` as a convenience wrapper only; the
  credential facts it demonstrates map one-to-one onto `Google.Apis.Auth`.

## Architecture

New `src/Netclaw.Providers/GoogleVertex/`:

- `GoogleVertexEndpoint`: parses the credential JSON, reads `project_id`
  (override via `VendorOptions.ProjectId`), resolves the location (default
  `global`, override via `VendorOptions.Location`), and produces the base
  URI plus exact chat and models paths.
- `IGoogleAccessTokenProvider`: one method,
  `GetAccessTokenAsync(CancellationToken)`. This seam keeps
  `Google.Apis.Auth` behind one interface and makes every automated test
  fakeable.
- `GoogleServiceAccountTokenProvider`: production implementation. Builds one
  `GoogleCredential` from the JSON with the `cloud-platform` scope and
  delegates to `GetAccessTokenForRequestAsync`. The library caches and
  refreshes the token internally; Netclaw owns no expiry logic and
  persists no token.
- `GoogleVertexTokenHandler : DelegatingHandler`: sets
  `Authorization: Bearer` from the token provider when the request carries
  no auth header. The shared chat client stays untouched because it skips
  the header when `ApiKey` is null.
- `GoogleVertexDescriptor`: TypeKey `google-vertex`, default endpoint
  `https://aiplatform.googleapis.com`, `ServiceAccountAuth` declaration,
  `ProbeAsync` that lists models through the token provider.
- `GoogleVertexProviderPlugin`: caches one token provider per credential
  JSON, builds the handler-wrapped `HttpClient`, and returns the shared
  `OpenAiCompatibleChatClient` on the Vertex endpoint with the generic wire
  profile.

Configuration:

- `Providers.<name>.Type = "google-vertex"`,
  `AuthMethod = "ServiceAccount"` in netclaw.json.
- `Providers.<name>.ServiceAccountJson` in secrets.json (encrypted at rest
  when written through the app), or the standard
  `GOOGLE_APPLICATION_CREDENTIALS` environment variable pointing at the
  service-account JSON file.
- `Providers.<name>.VendorOptions.ProjectId` and `.Location` optional in
  netclaw.json; the `GOOGLE_CLOUD_PROJECT` and `GOOGLE_CLOUD_LOCATION`
  environment variables serve the same role for environment-driven
  deployments.

`GoogleVertexCredentialResolver` owns the resolution order: inline secret
first, then the credentials file path, then a loud error naming both forms.
Project and location resolve as VendorOptions, then environment, then
credential derivation and the `global` default. The resolver reads
`GOOGLE_APPLICATION_CREDENTIALS` explicitly; ambient application-default
fallbacks (gcloud ADC file, metadata server) are deliberately not consulted
so a misconfigured path cannot silently switch identity.

## Failure Modes and Recovery

- Missing or empty `ServiceAccountJson` with `AuthMethod.ServiceAccount`:
  doctor reports missing-credential guidance; plugin construction throws
  with fix guidance. Startup stays fail-closed through existing
  `ProviderRuntimeValidation`.
- Malformed credential JSON: the endpoint builder rejects it before any
  network call with a parse error.
- Credential JSON without `project_id` and no override: loud error naming
  the missing field. No guessed project.
- Token mint failure (network, revoked key, wrong scope): the HTTP error
  surfaces through the existing provider error path with the provider label.
- Expired token: `Google.Apis.Auth` refreshes transparently inside the
  cached credential; no Netclaw code path observes expiry.
- Daemon restart: stateless. The plugin rebuilds the credential from the
  secret on first use.

## Actor and Persistence Boundaries

No actor changes. Sessions keep addressing models through `IChatClient` and
`ModelReference`; the provider type appears only behind the plugin seam.
Persistence is untouched: no new persisted types, no token storage, no
schema change. The secret persists only through the existing encrypted
secrets file.
