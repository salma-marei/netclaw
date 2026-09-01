## 1. Auth contract

- [x] 1.1 Add `AuthMethod.ServiceAccount` to the `AuthMethod` enum in
  `src/Netclaw.Configuration/AuthMethod.cs`.
- [x] 1.2 Add `ServiceAccountJson` (`SensitiveString?`) to `ProviderEntry`
  so it binds from the secrets overlay like `ApiKey`.
- [x] 1.3 Add `ServiceAccountAuth : IProviderAuth` in
  `src/Netclaw.Providers/IProviderAuth.cs` with
  `SupportedAuthMethods = [AuthMethod.ServiceAccount]`.
- [x] 1.4 Add the `ServiceAccount` case to
  `ChatClientDoctorCheck.MissingCredentialMessage`: an entry that declares
  the method without `ServiceAccountJson` fails with guidance naming the
  secret.

## 2. Google Vertex provider

- [x] 2.1 Reference `Google.Apis.Auth` via `Directory.Packages.props` and
  `src/Netclaw.Providers/Netclaw.Providers.csproj`.
- [x] 2.2 `GoogleVertexEndpoint`: parse the credential JSON, resolve project
  (JSON `project_id`, `VendorOptions.ProjectId` override) and location
  (default `global`, `VendorOptions.Location` override), and produce the
  base URI plus exact chat and models paths. Reject malformed JSON and a
  missing project with loud errors before any network call.
- [x] 2.3 `IGoogleAccessTokenProvider` +
  `GoogleServiceAccountTokenProvider`: one `GoogleCredential` per credential
  JSON, scoped to `cloud-platform`, token via
  `GetAccessTokenForRequestAsync`. The library owns caching and refresh.
- [x] 2.4 `GoogleVertexTokenHandler : DelegatingHandler`: set
  `Authorization: Bearer` when the request carries no auth header.
- [x] 2.5 `GoogleVertexDescriptor`: TypeKey `google-vertex`, default endpoint
  `https://aiplatform.googleapis.com`, `ServiceAccountAuth`, `ProbeAsync`
  through the models path with a live token.
- [x] 2.6 `GoogleVertexProviderPlugin`: cache token providers per credential
  JSON, build the handler-wrapped `HttpClient`, and return the shared
  `OpenAiCompatibleChatClient` on the Vertex endpoint with the generic wire
  profile. No change to the shared client.
- [x] 2.7 Register the descriptor and plugin in
  `ProviderDescriptorCatalog.Create`,
  `ProviderDescriptorServiceExtensions`, and
  `LlmProviderServiceExtensions`.

## 3. Tests (fake credentials only)

- [x] 3.1 Endpoint builder: `global` produces
  `aiplatform.googleapis.com` and the exact project-scoped chat/models
  paths; a region override produces the regional host; malformed JSON and a
  missing `project_id` fail loudly.
- [x] 3.2 Token handler: with a fake `IGoogleAccessTokenProvider`, requests
  carry `Authorization: Bearer <token>`; no provider call happens when a
  header is already present.
- [x] 3.3 Plugin wiring: fake token provider + recording handler prove the
  chat request reaches the exact Vertex path with the injected token and a
  streaming SSE response parses.
- [x] 3.4 Probe: fake models-list response yields `DiscoveredModel` records.
- [x] 3.5 Doctor: `AuthMethod.ServiceAccount` without the secret produces
  the missing-credential guidance.
- [x] 3.6 No test or fixture contains real credential material.

## 4. Docs and gates

- [x] 4.1 Update
  `feeds/skills/.system/files/netclaw-operations/references/providers.md`
  with the `google-vertex` type and bump the skill `metadata.version`.
- [x] 4.2 `dotnet slopwatch analyze` passes with no new violations.
- [x] 4.3 Copyright headers verified on all new `.cs` files.
- [x] 4.4 Manual milestone check with a real credential (operator-run):
  `netclaw doctor` probes the provider, and a single headless prompt
  returns a Gemini text response. PASSED 2026-09-01: headless prompt
  returned the exact requested text via gcp/gemini-2.5-flash.

## 5. Environment credential flow (approved scope adjustment)

- [x] 5.1 `GoogleVertexCredentialResolver`: credential resolution in order
  inline `ServiceAccountJson` → `GOOGLE_APPLICATION_CREDENTIALS` file →
  loud error naming both forms. Explicit env read only; no ambient
  application-default fallback.
- [x] 5.2 File-path credential source: the file's JSON reuses the scoped
  inline token provider (`GoogleServiceAccountTokenProvider`), so both
  credential forms take the real OAuth2 token-exchange path. A raw file
  credential would otherwise mint a self-signed JWT that Vertex rejects
  with 401.
- [x] 5.3 Project/location resolution: VendorOptions →
  `GOOGLE_CLOUD_PROJECT` / `GOOGLE_CLOUD_LOCATION` → credential derivation
  and the `global` default. The endpoint builder stays a pure function.
- [x] 5.4 Doctor: `ServiceAccount` entries accept either credential form;
  a configured file path must exist; guidance names both forms.
- [x] 5.5 Tests: resolver precedence, env-file probe and plugin paths,
  missing file, and doctor environment cases — fake credentials only, with
  a fake environment lookup so tests never read machine-wide state.
- [x] 5.6 OpenSpec proposal/design/spec wording updated for the env-file
  flow; `netclaw-operations` provider reference updated.
- [x] 5.7 Publisher-model normalization: bare model IDs are sent as
  `google/<model>` (Vertex's OpenAI-compatible endpoint rejects bare IDs
  with 400); publisher-qualified IDs pass through. Unit-tested.

