## MODIFIED Requirements

### Requirement: Multi-provider support

The system SHALL support selecting one provider profile from a supported set.
Supported provider type keys SHALL include `ollama`, `openai-compatible`,
`openrouter`, `openai`, `anthropic`, `github-copilot`, `veniceai`, `deepseek`,
and `google-vertex`. All provider interactions SHALL use the
Microsoft.Extensions.AI `IChatClient` abstraction layer, ensuring
provider-agnostic model access throughout the application.

Provider model discovery SHALL extract modality metadata where the provider
API supports it. `DiscoveredModel` records SHALL include `InputModalities`
and `OutputModalities` fields populated from provider responses.

The `openai-compatible` provider SHALL support both no authentication and
API-key authentication. The API key SHALL be optional. When an API key is
configured, all OpenAI-compatible requests (chat completion, model
discovery, capability probing) SHALL send it as `Authorization: Bearer`.
When no API key is configured, requests SHALL send no authentication header.

#### Scenario: Switch provider

- **GIVEN** OpenRouter is configured
- **WHEN** operator selects Anthropic, OpenAI, Ollama, OpenAI-compatible,
  OpenRouter, GitHub Copilot, Venice.ai, DeepSeek, or Google Vertex profile
- **THEN** runtime uses selected provider through the `IChatClient` interface
  after validation

#### Scenario: Provider accessed through MEAI abstraction

- **GIVEN** a provider profile is configured
- **WHEN** the session actor sends a chat completion request
- **THEN** the request is routed through the `IChatClient` abstraction
- **AND** no provider-specific types leak into session or actor code

#### Scenario: Ollama discovery includes modality

- **GIVEN** an Ollama provider is configured
- **WHEN** model discovery runs via `ProviderProbe`
- **THEN** the returned `DiscoveredModel` records SHALL include
  `InputModalities` and `OutputModalities` populated from `/api/show`
  capability data

#### Scenario: OpenRouter discovery includes modality

- **GIVEN** an OpenRouter provider is configured
- **WHEN** model discovery runs via `ProviderProbe`
- **THEN** the returned `DiscoveredModel` records SHALL include context-window
  metadata when the backend exposes a known field shape, including vLLM
  `max_model_len`, DwarfStar/ds4 `context_length` or
  `top_provider.context_length`, and llama.cpp `meta.n_ctx` or
  `meta.n_ctx_train`

#### Scenario: OpenAI-compatible discovery includes backend context metadata

- **GIVEN** an OpenAI-compatible provider is configured
- **WHEN** model discovery runs via `ProviderProbe`
- **THEN** the returned `DiscoveredModel` records SHALL include context-window
  metadata when the backend exposes a known field shape, including vLLM
  `max_model_len`, DwarfStar/ds4 `context_length` or
  `top_provider.context_length`, and llama.cpp `meta.n_ctx` or
  `meta.n_ctx_train`

#### Scenario: Add an OpenAI-compatible provider with an API key

- **WHEN** the operator adds an `openai-compatible` provider and supplies an
  API key through an interactive surface
- **THEN** Netclaw stores `AuthMethod: ApiKey` in the provider entry
- **AND** Netclaw stores the key through the encrypted secrets path
- **AND** chat, discovery, and probe requests send the key as
  `Authorization: Bearer`

#### Scenario: Add an OpenAI-compatible provider without an API key

- **WHEN** the operator adds an `openai-compatible` provider and supplies no
  API key
- **THEN** Netclaw stores `AuthMethod: None` and no provider secret
- **AND** chat, discovery, and probe requests send no authentication header

#### Scenario: Existing no-auth OpenAI-compatible configuration

- **GIVEN** an existing provider entry of type `openai-compatible` with
  `AuthMethod: None` and no stored secret
- **WHEN** the daemon starts or the doctor check runs
- **THEN** the entry remains valid and sends no authentication header

## ADDED Requirements

### Requirement: Google Vertex provider

The system SHALL support Vertex AI as a selectable provider profile with the
type key `google-vertex`. The provider SHALL use
`Microsoft.Extensions.AI.IChatClient` and Vertex AI's OpenAI-compatible
endpoint.

The provider SHALL accept the Google Cloud service-account credential in
one of two forms, resolved in order: the full service-account JSON string
in `Providers.<name>.ServiceAccountJson` through the encrypted secrets
path, or the standard `GOOGLE_APPLICATION_CREDENTIALS` environment variable
pointing to the service-account JSON file. The provider SHALL read the
environment variable explicitly and SHALL NOT fall back to ambient
application-default credentials. It SHALL NOT accept a static API key. The
credential SHALL be scoped to `https://www.googleapis.com/auth/cloud-platform`.

The provider SHALL resolve the GCP project in this order:
`VendorOptions.ProjectId`, the `GOOGLE_CLOUD_PROJECT` environment variable,
then the credential JSON `project_id` field. The provider SHALL resolve the
Vertex location in this order: `VendorOptions.Location`, the
`GOOGLE_CLOUD_LOCATION` environment variable, then the default `global`.

Vertex's OpenAI-compatible endpoint SHALL receive publisher-qualified model
IDs. When the configured model ID carries no publisher prefix, the provider
SHALL send it as `google/<model>`; IDs that already carry a publisher SHALL
be sent unchanged.

The provider SHALL mint OAuth2 Bearer access tokens from the credential
through Google's auth library and SHALL send the current token on every chat
and model-discovery request. Netclaw SHALL NOT persist access tokens. The
default endpoint SHALL be `https://aiplatform.googleapis.com` for the
`global` location and `https://{location}-aiplatform.googleapis.com`
otherwise. Chat requests SHALL use
`/v1/projects/{project}/locations/{location}/endpoints/openapi/chat/completions`,
and model discovery SHALL use
`/v1/projects/{project}/locations/{location}/endpoints/openapi/models`.

#### Scenario: Operator configures the Vertex provider

- **GIVEN** a service-account JSON string held in the encrypted secrets file
  under `Providers.<name>.ServiceAccountJson`
- **WHEN** the operator declares the provider with
  `Type: google-vertex` and `AuthMethod: ServiceAccount` and selects a Gemini
  model ID
- **THEN** runtime resolves the provider through `IChatClient`
- **AND** the project and location derive from the credential and the
  `global` default without duplicate configuration

#### Scenario: Vertex request carries a live Bearer token

- **GIVEN** a configured `google-vertex` provider
- **WHEN** Netclaw sends a chat completion or model-discovery request
- **THEN** the request carries `Authorization: Bearer` with a token minted
  from the service-account credential
- **AND** the request targets the project-derived Vertex OpenAI-compatible
  path for the resolved location

#### Scenario: Configure the Vertex provider through the environment

- **GIVEN** `GOOGLE_APPLICATION_CREDENTIALS` points to a service-account
  JSON file and `GOOGLE_CLOUD_PROJECT` plus `GOOGLE_CLOUD_LOCATION` are set
- **WHEN** the provider builds requests or runs model discovery with no
  inline `ServiceAccountJson` configured
- **THEN** the credential loads from that file and the request targets the
  project and location from the environment
- **AND** no ambient application-default credential source is consulted

#### Scenario: Missing service-account credential

- **GIVEN** a provider entry with `AuthMethod: ServiceAccount`, no stored
  `ServiceAccountJson`, and no `GOOGLE_APPLICATION_CREDENTIALS` set
- **WHEN** configuration validation or the doctor check runs
- **THEN** validation fails with guidance that names both credential forms
- **AND** Netclaw does not select a real chat client

#### Scenario: Malformed service-account credential

- **GIVEN** a `ServiceAccountJson` value that is not valid JSON or lacks
  `project_id` with no override
- **WHEN** the provider builds its endpoint or token source
- **THEN** construction fails with an error that names the credential problem
- **AND** no network request is sent

#### Scenario: Vertex model discovery

- **GIVEN** a configured `google-vertex` provider with a valid credential
- **WHEN** model discovery or a probe runs
- **THEN** Netclaw calls the Vertex `models` endpoint with a live Bearer
  token
- **AND** Netclaw returns the model IDs from the live response

#### Scenario: Secret material stays out of logs

- **GIVEN** any provider request, probe, or error path
- **WHEN** Netclaw logs or renders provider diagnostics
- **THEN** the service-account JSON and derived access tokens never appear in
  logs, exceptions, or doctor output
