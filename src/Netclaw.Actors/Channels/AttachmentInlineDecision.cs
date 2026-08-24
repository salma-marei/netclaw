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
    /// Image-only convenience overload used by the tool-driven model-input
    /// path. Keeps the file-read call sites byte-identical.
    /// </summary>
    public static (bool Inlined, string? Note) Resolve(
        MimeType mimeType, AttachmentCategory category, bool inlineImages)
        => Resolve(mimeType, category, inlineImages ? ModelModality.Image : ModelModality.None);

    public static (bool Inlined, string? Note) Resolve(
        MimeType mimeType, AttachmentCategory category, ModelModality inputModalities)
    {
        if (category == AttachmentCategory.Image)
        {
            if (!inputModalities.HasFlag(ModelModality.Image))
                return (false, AttachmentNotes.ModelMissingImage);

            // Only inline image types the provider can actually ingest as model
            // input (png/jpeg/gif/webp). Other image formats (e.g. bmp/tiff) are
            // accepted but delivered path-only, so they never reach the
            // image-only provider serialization path.
            return MimeTypeCatalog.IsModelInputSupported(mimeType)
                ? (true, null)
                : (false, AttachmentNotes.FormatNotInlineable);
        }

        if (category == AttachmentCategory.Media
            && MimeTypeCatalog.GetMediaKind(mimeType) == MediaKind.Audio)
        {
            if (!inputModalities.HasFlag(ModelModality.Audio))
                return (false, AttachmentNotes.ModelMissingAudio);

            // Only mp3/wav can be serialized as OpenAI-compatible input_audio.
            // Other audio formats (e.g. ogg/m4a) are accepted but delivered
            // path-only, so they never reach the audio serialization path.
            return MimeTypeCatalog.TryGetInputAudioFormat(mimeType, out _)
                ? (true, null)
                : (false, AttachmentNotes.FormatNotInlineable);
        }

        return category switch
        {
            AttachmentCategory.Pdf => (false, AttachmentNotes.ModelMissingPdf),
            _ => (false, AttachmentNotes.FormatNotInlineable)
        };
    }
}
