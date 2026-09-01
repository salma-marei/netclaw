## Why

Operators hold Google Cloud service-account credentials and need Netclaw to
reach Gemini models on Vertex AI with them. Netclaw has no Google provider
type today. The `openai-compatible` type cannot serve this case: Vertex
rejects static API keys and requires an OAuth2 access token minted from the
service-account credential. The internal reference setup proves the flow:
the full service-account JSON arrives as one secret string, `project_id`
comes from that JSON, the Vertex location is `global`, the scope is
`cloud-platform`, and Google's library mints the short-lived access token.

## What Changes

- Add a `google-vertex` provider type (descriptor + plugin) that reuses the
  existing `OpenAiCompatibleChatClient` transport against Vertex AI's
  OpenAI-compatible endpoint.
- Authenticate with `Google.Apis.Auth`: the provider accepts the
  service-account credential as inline JSON in secrets.json or through the
  standard `GOOGLE_APPLICATION_CREDENTIALS` file path, creates a scoped
  `GoogleCredential`, and injects the library-managed Bearer token through
  an HTTP handler. No token is ever persisted.
- Derive `project_id` from the credential JSON. `VendorOptions.ProjectId`
  and the `GOOGLE_CLOUD_PROJECT` environment variable override it, in that
  order.
- Default the Vertex location to `global`. `VendorOptions.Location` and the
  `GOOGLE_CLOUD_LOCATION` environment variable override it, in that order.
- Add `AuthMethod.ServiceAccount` and one secret field
  `Providers.<name>.ServiceAccountJson` bound from the existing encrypted
  secrets path. No new secret infrastructure.
- Model discovery and doctor probing use the Vertex `/models` endpoint with
  the same token.
- Update the `netclaw-operations` system skill provider reference and bump
  its version.
- No config schema change: the `Providers` schema section is open.
- Milestone 1 proves one path: service-account JSON to Google auth to Vertex
  to Gemini to a simple text response. Thought-signature handling, audio
  work, and interactive wizard flows stay out of scope.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `netclaw-model-providers`: the supported provider type keys gain
  `google-vertex`. A new requirement defines the provider contract: inline
  service-account JSON credential, derived project, `global` default
  location, Bearer tokens minted per request, and Vertex OpenAI-compatible
  chat and model-discovery paths.

## Impact

- **Code:** `src/Netclaw.Configuration/AuthMethod.cs` (new enum value),
  `src/Netclaw.Configuration/ProviderEntry.cs` (new `ServiceAccountJson`
  secret field), `src/Netclaw.Providers/IProviderAuth.cs` (new
  `ServiceAccountAuth` shape), new `src/Netclaw.Providers/GoogleVertex/`
  (endpoint builder, token provider, HTTP handler, descriptor, plugin),
  registration in `ProviderDescriptorCatalog`,
  `ProviderDescriptorServiceExtensions`, and `LlmProviderServiceExtensions`,
  and a `ServiceAccount` case in `ChatClientDoctorCheck`.
- **Package:** `Google.Apis.Auth` referenced through
  `Directory.Packages.props`. It carries the JWT signing, token cache, and
  refresh; Netclaw owns none of that.
- **Tests:** fake-token-provider tests for the endpoint builder, handler,
  plugin wiring, probe, and doctor guidance. No test touches a real
  credential.
- **No change:** `OpenAiCompatibleChatClient`, other provider plugins,
  config schema, secrets format, persistence, actor code.
- **Security:** the service-account JSON is secret material. It lives only
  in the encrypted secrets file or the host environment. Netclaw must never
  log it, echo it, or persist derived access tokens.
- **Out of scope:** ambient application-default fallbacks beyond the
  explicit `GOOGLE_APPLICATION_CREDENTIALS` read (gcloud ADC chain,
  metadata server), interactive wizard support, thought-signature handling,
  Express-mode API keys, regional defaults other than `global`.
