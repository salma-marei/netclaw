// -----------------------------------------------------------------------
// <copyright file="AttachmentIngressPipelineAudioTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Event;
using Netclaw.Channels;
using Netclaw.Configuration;
using Netclaw.Media;
using Netclaw.Security;
using Xunit;

namespace Netclaw.Actors.Tests.Channels;

public sealed class AttachmentIngressPipelineAudioTests
{
    private static readonly byte[] WavBytes =
    [
        0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00,
        0x57, 0x41, 0x56, 0x45, 0x66, 0x6D, 0x74, 0x20,
        0x10, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00,
        0x80, 0x3E, 0x00, 0x00, 0x00, 0x7D, 0x00, 0x00,
        0x02, 0x00, 0x10, 0x00, 0x64, 0x61, 0x74, 0x61,
        0x00, 0x00, 0x00, 0x00
    ];

    [Fact]
    public async Task Audio_without_model_support_is_rejected_before_model_handoff()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netclaw-audio-reject-{Guid.NewGuid():N}");
        var inbox = Path.Combine(root, "inbox");
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(staging);

        try
        {
            var outcome = await AttachmentIngressPipeline.IngestAsync(
                new AttachmentIngressRequest("voice.wav", MimeTypeCatalog.AudioWav, WavBytes.Length),
                TrustAudience.Team,
                new ChannelAttachmentPolicy
                {
                    AllowedCategories = [AttachmentCategory.Media],
                    MaxFileBytes = 1024 * 1024,
                    MaxFilesPerMessage = 1
                },
                ModelModality.Text,
                inbox,
                staging,
                TimeSpan.FromSeconds(5),
                new MagicByteContentScanner(new ContentPolicy()),
                NoLogger.Instance,
                DownloadAsync,
                TestContext.Current.CancellationToken);

            var rejected = Assert.IsType<AttachmentIngestOutcome.Rejected>(outcome);
            Assert.Contains("does not support audio input", rejected.UserFacingReason, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(inbox));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // CELT-encoded pure silence: decodes (by design of the silence guard) to
    // an all-zero PCM stream, so the pipeline must reject it as inaudible.
    private const string SilentOggBase64 =
        "T2dnUwACAAAAAAAAAABB1TxOAAAAAEfjNIoBE09wdXNIZWFkAQEAAIC7AAAAAABPZ2dTAAAAAAAAAAAAAEHVPE4BAAAA0vQPXQEfT3B1c1RhZ3MPAAAAQ29uY2VudHVzIDIuMS4yAAAAAE9nZ1MAAEC/AAAAAAAAQdU8TgIAAADnPNeiMwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDAwMDA/j//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//vj//k9nZ1MABEC/AAAAAAAAQdU8TgMAAACrWzzGAQA=";

    private static readonly byte[] SilentOggBytes = Convert.FromBase64String(SilentOggBase64);

    [Fact]
    public async Task Silent_voice_ogg_is_rejected_as_inaudible()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netclaw-audio-silent-{Guid.NewGuid():N}");
        var inbox = Path.Combine(root, "inbox");
        var staging = Path.Combine(root, "staging");
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(staging);

        try
        {
            var outcome = await AttachmentIngressPipeline.IngestAsync(
                new AttachmentIngressRequest("voice.ogg", MimeTypeCatalog.AudioOgg, SilentOggBytes.Length),
                TrustAudience.Team,
                new ChannelAttachmentPolicy
                {
                    AllowedCategories = [AttachmentCategory.Media],
                    MaxFileBytes = 1024 * 1024,
                    MaxFilesPerMessage = 1
                },
                ModelModality.Text | ModelModality.Audio,
                inbox,
                staging,
                TimeSpan.FromSeconds(5),
                new MagicByteContentScanner(new ContentPolicy()),
                NoLogger.Instance,
                DownloadSilentOggAsync,
                TestContext.Current.CancellationToken);

            var rejected = Assert.IsType<AttachmentIngestOutcome.Rejected>(outcome);
            Assert.Contains("couldn't hear anything", rejected.UserFacingReason, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(inbox));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<AttachmentDownloadResult> DownloadSilentOggAsync(
        string stagingDirectory,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        Assert.True(SilentOggBytes.Length <= maxBytes);
        var path = Path.Combine(stagingDirectory, "voice.download");
        await File.WriteAllBytesAsync(path, SilentOggBytes, cancellationToken);
        return new AttachmentDownloadResult(path, SilentOggBytes.Length);
    }

    private static async Task<AttachmentDownloadResult> DownloadAsync(
        string stagingDirectory,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        Assert.True(WavBytes.Length <= maxBytes);
        var path = Path.Combine(stagingDirectory, "voice.download");
        await File.WriteAllBytesAsync(path, WavBytes, cancellationToken);
        return new AttachmentDownloadResult(path, WavBytes.Length);
    }
}
