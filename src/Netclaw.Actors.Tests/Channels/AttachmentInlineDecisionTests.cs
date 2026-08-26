// -----------------------------------------------------------------------
// <copyright file="AttachmentInlineDecisionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Channels;
using Netclaw.Configuration;
using Netclaw.Media;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class AttachmentInlineDecisionTests
{
    [Theory]
    [InlineData("image/png")]
    [InlineData("image/jpeg")]
    [InlineData("image/gif")]
    [InlineData("image/webp")]
    public void Model_input_image_types_inline_when_model_accepts_images(string mimeType)
    {
        var (action, note) = AttachmentInlineDecision.Resolve(
            new MimeType(mimeType), AttachmentCategory.Image, ModelModality.Image);

        Assert.Equal(AttachmentInlineDecision.AttachmentInlineAction.Inline, action);
        Assert.Null(note);
    }

    [Theory]
    [InlineData("image/bmp")]
    [InlineData("image/tiff")]
    public void Image_types_the_provider_cannot_ingest_are_path_only(string mimeType)
    {
        // bmp/tiff are accepted as images but must NOT be inlined as DataContent,
        // or they would hit the image-only provider serialization guardrail.
        var (action, note) = AttachmentInlineDecision.Resolve(
            new MimeType(mimeType), AttachmentCategory.Image, ModelModality.Image);

        Assert.Equal(AttachmentInlineDecision.AttachmentInlineAction.PathOnly, action);
        Assert.NotNull(note);
    }

    [Fact]
    public void Images_are_path_only_when_model_lacks_image_modality()
    {
        var (action, note) = AttachmentInlineDecision.Resolve(
            new MimeType("image/png"), AttachmentCategory.Image, ModelModality.None);

        Assert.Equal(AttachmentInlineDecision.AttachmentInlineAction.PathOnly, action);
        Assert.NotNull(note);
        Assert.Equal(AttachmentNotes.ModelMissingImage, note);
    }

    [Theory]
    [InlineData("audio/mpeg")]
    [InlineData("audio/wav")]
    public void Input_audio_types_inline_when_model_accepts_audio(string mimeType)
    {
        var (action, note) = AttachmentInlineDecision.Resolve(
            new MimeType(mimeType), AttachmentCategory.Media, ModelModality.Audio);

        Assert.Equal(AttachmentInlineDecision.AttachmentInlineAction.Inline, action);
        Assert.Null(note);
    }

    [Fact]
    public void Audio_types_the_provider_cannot_ingest_are_path_only()
    {
        // m4a is accepted as audio but must NOT be inlined as DataContent,
        // or it would hit the audio serialization guardrail. The OpenAI wire
        // format only supports wav and mp3.
        var (action, note) = AttachmentInlineDecision.Resolve(
            new MimeType("audio/mp4"), AttachmentCategory.Media, ModelModality.Audio);

        Assert.Equal(AttachmentInlineDecision.AttachmentInlineAction.PathOnly, action);
        Assert.NotNull(note);
        Assert.Equal(AttachmentNotes.FormatNotInlineable, note);
    }

    [Fact]
    public void Ogg_audio_is_transcoded_when_model_accepts_audio()
    {
        // OGG is not directly serializable as input_audio, but the model can
        // still hear it after a WAV transcode at model-input time.
        var (action, note) = AttachmentInlineDecision.Resolve(
            new MimeType("audio/ogg"), AttachmentCategory.Media, ModelModality.Audio);

        Assert.Equal(AttachmentInlineDecision.AttachmentInlineAction.TranscodeAndInline, action);
        Assert.Equal(AttachmentNotes.AudioTranscodedToWav, note);
    }

    [Fact]
    public void Ogg_audio_is_path_only_when_model_lacks_audio_modality()
    {
        var (action, note) = AttachmentInlineDecision.Resolve(
            new MimeType("audio/ogg"), AttachmentCategory.Media, ModelModality.None);

        Assert.Equal(AttachmentInlineDecision.AttachmentInlineAction.PathOnly, action);
        Assert.NotNull(note);
        Assert.Equal(AttachmentNotes.ModelMissingAudio, note);
    }

    [Fact]
    public void Audio_is_path_only_when_model_lacks_audio_modality()
    {
        var (action, note) = AttachmentInlineDecision.Resolve(
            new MimeType("audio/mpeg"), AttachmentCategory.Media, ModelModality.None);

        Assert.Equal(AttachmentInlineDecision.AttachmentInlineAction.PathOnly, action);
        Assert.NotNull(note);
        Assert.Equal(AttachmentNotes.ModelMissingAudio, note);
    }
}
