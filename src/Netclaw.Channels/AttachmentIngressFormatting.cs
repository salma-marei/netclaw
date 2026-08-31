// -----------------------------------------------------------------------
// <copyright file="AttachmentIngressFormatting.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;
using System.Text;

namespace Netclaw.Channels;

public static class AttachmentIngressFormatting
{
    public static string BuildAttachmentLine(
        string name,
        string mimeType,
        long size,
        string relativePath,
        bool inlined,
        string? note)
    {
        var inlinedWire = inlined ? "true" : "false";
        var sb = new StringBuilder(128);
        sb.Append("[attachment] name=\"").Append(EscapeQuoted(name)).Append('"');
        sb.Append(" mime=\"").Append(EscapeQuoted(mimeType)).Append('"');
        sb.Append(" size=").Append(size);
        sb.Append(" path=\"").Append(EscapeQuoted(relativePath)).Append('"');
        sb.Append(" inlined=\"").Append(inlinedWire).Append('"');
        if (!string.IsNullOrEmpty(note))
            sb.Append(" note=\"").Append(EscapeQuoted(note)).Append('"');
        return sb.ToString();
    }

    public static string EscapeQuoted(string value)
    {
        var needsProcessing = false;
        foreach (var c in value)
        {
            if (c < ' ' || c == '\\' || c == '"')
            {
                needsProcessing = true;
                break;
            }
        }

        if (!needsProcessing)
            return value;

        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c < ' ')
                sb.Append(' ');
            else if (c == '\\')
                sb.Append("\\\\");
            else if (c == '"')
                sb.Append("\\\"");
            else
                sb.Append(c);
        }

        return sb.ToString();
    }

    public static (AttachmentInlineDecision.AttachmentInlineAction Action, string? Note) ResolveInlineDecision(
        MimeType mimeType,
        AttachmentCategory category,
        ModelModality inputModalities)
        => AttachmentInlineDecision.Resolve(mimeType, category, inputModalities);

    public static async Task<AttachmentIngressProjection> BuildAcceptedProjectionAsync(
        string inboxPath,
        string filename,
        string mimeType,
        AttachmentCategory category,
        ModelModality inputModalities,
        long size,
        long maxDecodedBytes,
        CancellationToken cancellationToken)
    {
        var relativePath = $"{SessionDirectoryHelper.InboxSubdirectory}/{Path.GetFileName(inboxPath)}";
        var (action, note) = ResolveInlineDecision(new MimeType(mimeType), category, inputModalities);

        if (action == AttachmentInlineDecision.AttachmentInlineAction.PathOnly)
        {
            var line = BuildAttachmentLine(filename, mimeType, size, relativePath, inlined: false, note);
            return new AttachmentIngressProjection(line, InlineContent: null, Inlined: false);
        }

        if (action == AttachmentInlineDecision.AttachmentInlineAction.TranscodeAndInline)
        {
            var line = BuildAttachmentLine(filename, mimeType, size, relativePath, inlined: true, note);
            var bytes = await File.ReadAllBytesAsync(inboxPath, cancellationToken);
            try
            {
                var wav = AudioTranscoder.TranscodeOpusOggToWav(bytes, maxDecodedBytes);
                return new AttachmentIngressProjection(line, new DataContent(wav, MimeTypeCatalog.AudioWav), Inlined: true);
            }
            catch (AudioTranscodeException)
            {
                // Expected failure (non-Opus OGG or budget exceeded): keep the
                // attachment path-only, same as other non-inlineable audio.
                var fallbackLine = BuildAttachmentLine(
                    filename, mimeType, size, relativePath, inlined: false, AttachmentNotes.FormatNotInlineable);
                return new AttachmentIngressProjection(fallbackLine, InlineContent: null, Inlined: false);
            }
            catch (Exception ex)
            {
                // Unexpected converter error: keep the file on disk, surface a
                // note, and let the caller log the underlying exception.
                var fallbackLine = BuildAttachmentLine(
                    filename, mimeType, size, relativePath, inlined: false, AttachmentNotes.AudioTranscodeFailed);
                return new AttachmentIngressProjection(
                    fallbackLine, InlineContent: null, Inlined: false, UnexpectedTranscodeError: ex.Message);
            }
        }

        var inlineLine = BuildAttachmentLine(filename, mimeType, size, relativePath, inlined: true, note);
        var inlineBytes = await File.ReadAllBytesAsync(inboxPath, cancellationToken);
        return new AttachmentIngressProjection(inlineLine, new DataContent(inlineBytes, mimeType), Inlined: true);
    }

    public static async Task<IReadOnlyList<AIContent>> BuildAcceptedContentsAsync(
        string inboxPath,
        string filename,
        string mimeType,
        AttachmentCategory category,
        ModelModality inputModalities,
        long size,
        long maxDecodedBytes,
        CancellationToken cancellationToken,
        Action<string> onUnexpectedTranscodeFailure)
    {
        var projection = await BuildAcceptedProjectionAsync(
            inboxPath, filename, mimeType, category, inputModalities, size, maxDecodedBytes, cancellationToken);

        if (projection.UnexpectedTranscodeError is { } error)
            onUnexpectedTranscodeFailure(error);

        var line = new TextContent(projection.Line);
        return projection.InlineContent is null
            ? [line]
            : [line, projection.InlineContent];
    }

    public static string FormatBytes(long size)
        => ByteSizeFormatter.Format(size);
}

public readonly record struct AttachmentIngressProjection(
    string Line,
    DataContent? InlineContent,
    bool Inlined,
    string? UnexpectedTranscodeError = null);
