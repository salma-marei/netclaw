// -----------------------------------------------------------------------
// <copyright file="LlmSessionMediaFailureCleanupTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Regression: media attached to a user message must not replay into later
/// requests when the model/provider request fails.
///
/// <see cref="ChatMessageConverter.ToAiMessage"/> re-attaches every persisted
/// <see cref="SerializableChatMessage.MediaReferences"/> on each subsequent
/// turn. When a provider rejects the media payload (Z.ai: 400 "Base64 format
/// error in input_audio"), <see cref="LlmSessionActor.FailCurrentTurn"/> must
/// strip the failed turn's media refs so the same rejected payload is not
/// re-sent on every later text-only message.
/// </summary>
public class LlmSessionMediaFailureCleanupTests : LlmSessionTestBase
{
    private readonly Netclaw.Tests.Utilities.FakeChatClient _fakeChatClient = new();
    private readonly string _basePath = Path.Combine(
        Path.GetTempPath(), $"netclaw-media-failure-{Guid.NewGuid():N}");

    public LlmSessionMediaFailureCleanupTests(ITestOutputHelper output) : base(output) { }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        // Pinned base path so the test can pre-place the media file that the
        // assembler hydrates into DataContent before the failing LLM call.
        services.AddSingleton(new NetclawPaths(_basePath));

        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_fakeChatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "vision-model",
            ContextWindowTokens = 128_000,
            InputModalities = ModelModality.Text | ModelModality.Image,
        });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning
            {
                TitleGenerationInterval = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant."));
    }

    [Fact]
    public async Task Failed_media_turn_does_not_replay_media_on_later_text_turn()
    {
        var sessionId = new SessionId("test-channel/media-failure-cleanup");

        // The actor hydrates media refs from {session_dir}/media/{RelativePath},
        // so the file must exist before the failing LLM call fires.
        var sessionDir = SessionDirectoryHelper.GetSessionDirectory(
            sessionId, new NetclawPaths(_basePath).SessionsDirectory);
        var mediaDir = Path.Combine(sessionDir, SessionDirectoryHelper.MediaSubdirectory);
        Directory.CreateDirectory(mediaDir);
        await File.WriteAllBytesAsync(
            Path.Combine(mediaDir, "photo.png"),
            TestImages.SmallPng(),
            TestContext.Current.CancellationToken);

        try
        {
            // First LLM call fails (provider rejects the media payload).
            _fakeChatClient.Failure = new HttpRequestException("Base64 format error in input_audio");

            var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
            var subscriber = CreateTestProbe("media-failure-sub");

            await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
            {
                SessionId = sessionId,
                Filter = OutputFilter.Full
            }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

            await sessionManager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = sessionId,
                Content = "Describe this image",
                MediaReferences =
                [
                    new SerializableMediaReference
                    {
                        RelativePath = "photo.png",
                        MimeType = new Netclaw.Media.MimeType("image/png"),
                        Modality = (int)MediaModality.Image
                    }
                ]
            }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            var error = await subscriber.ExpectMsgAsync<ErrorOutput>(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(ErrorCategory.ProviderFailure, error.Category);

            var failed = await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(TurnOutcome.Failed, failed.Outcome);

            // The failing call DID carry the media — makes the no-replay
            // assertion on the later call non-vacuous.
            var failingCall = Assert.Single(_fakeChatClient.ReceivedMessagesByCall);
            Assert.Contains(
                failingCall.SelectMany(m => m.Contents).OfType<DataContent>(),
                dc => dc.Data.Length > 0);

            // Second call succeeds — must NOT replay the failed media.
            _fakeChatClient.Failure = null;

            await sessionManager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = sessionId,
                Content = "hello"
            }, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            var textOutput = await subscriber.ExpectMsgAsync<TextOutput>(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("fake", textOutput.Text, StringComparison.OrdinalIgnoreCase);

            var succeeded = await subscriber.ExpectMsgAsync<TurnCompleted>(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(TurnOutcome.Completed, succeeded.Outcome);

            var calls = _fakeChatClient.ReceivedMessagesByCall;
            Assert.Equal(2, calls.Count);
            Assert.DoesNotContain(
                calls[1].SelectMany(m => m.Contents).OfType<DataContent>(),
                dc => dc.Data.Length > 0);
        }
        finally
        {
            if (Directory.Exists(_basePath))
                Directory.Delete(_basePath, recursive: true);
        }
    }
}
