// -----------------------------------------------------------------------
// <copyright file="AttachmentInlineDecision.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Media;

namespace Netclaw.Actors.Channels;

/// <summary>
/// Shared inline-vs-path-only decision for channel attachments and local file inspection.
/// </summary>
public static class AttachmentInlineDecision
{
    /// <summary>
    /// Inline disposition for an attachment: path-only, inline the original
    /// bytes, or inline a transcoded copy.
    /// </summary>
    public enum AttachmentInlineAction
    {
        PathOnly,
        Inline,
        TranscodeAndInline
    }

    /// <summary>
    /// Image-only convenience overload used by the tool-driven model-input
    /// path. Keeps the file-read call sites byte-identical.
    /// </summary>
    public static (bool Inlined, string? Note) Resolve(
        MimeType mimeType, AttachmentCategory category, bool inlineImages)
    {
        var (action, note) = Resolve(
            mimeType, category, inlineImages ? ModelModality.Image : ModelModality.None);
        return (action == AttachmentInlineAction.Inline, note);
    }

    public static (AttachmentInlineAction Action, string? Note) Resolve(
        MimeType mimeType, AttachmentCategory category, ModelModality inputModalities)
    {
        if (category == AttachmentCategory.Image)
        {
            if (!inputModalities.HasFlag(ModelModality.Image))
                return (AttachmentInlineAction.PathOnly, AttachmentNotes.ModelMissingImage);

            // Only inline image types the provider can actually ingest as model
            // input (png/jpeg/gif/webp). Other image formats (e.g. bmp/tiff) are
            // accepted but delivered path-only, so they never reach the
            // image-only provider serialization path.
            return MimeTypeCatalog.IsModelInputSupported(mimeType)
                ? (AttachmentInlineAction.Inline, null)
                : (AttachmentInlineAction.PathOnly, AttachmentNotes.FormatNotInlineable);
        }

        if (category == AttachmentCategory.Media
            && MimeTypeCatalog.GetMediaKind(mimeType) == MediaKind.Audio)
        {
            if (!inputModalities.HasFlag(ModelModality.Audio))
                return (AttachmentInlineAction.PathOnly, AttachmentNotes.ModelMissingAudio);

            // Only mp3/wav can be serialized directly as OpenAI-compatible
            // input_audio.
            if (MimeTypeCatalog.TryGetInputAudioFormat(mimeType, out _))
                return (AttachmentInlineAction.Inline, null);

            // OGG is not directly serializable, but can be transcoded to WAV
            // at model-input time. Other audio (e.g. m4a) stays path-only.
            if (MimeTypeCatalog.CanTranscodeToInputAudio(mimeType))
                return (AttachmentInlineAction.TranscodeAndInline, AttachmentNotes.AudioTranscodedToWav);

            return (AttachmentInlineAction.PathOnly, AttachmentNotes.FormatNotInlineable);
        }

        return category switch
        {
            AttachmentCategory.Pdf => (AttachmentInlineAction.PathOnly, AttachmentNotes.ModelMissingPdf),
            _ => (AttachmentInlineAction.PathOnly, AttachmentNotes.FormatNotInlineable)
        };
    }
}
