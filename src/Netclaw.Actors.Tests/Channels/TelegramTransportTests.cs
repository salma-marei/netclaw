// -----------------------------------------------------------------------
// <copyright file="TelegramTransportTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Channels.Telegram;
using Netclaw.Media;
using Telegram.Bot.Types;

namespace Netclaw.Actors.Tests.Channels;

public sealed class TelegramTransportTests
{
    [Fact]
    public void MapFiles_maps_voice_note_as_ogg_audio()
    {
        var message = new Message
        {
            Id = 42,
            Voice = new Voice
            {
                FileId = "voice-file",
                FileUniqueId = "voice-unique",
                Duration = 3,
                FileSize = 1234
            }
        };

        var file = Assert.Single(TelegramTransport.MapFiles(message));

        Assert.Equal("voice-file", file.FileId);
        Assert.Equal("telegram-voice-42.ogg", file.Name);
        Assert.Equal(MimeTypeCatalog.AudioOgg, file.MimeType);
        Assert.Equal(1234, file.Size);
    }

    [Fact]
    public void MapFiles_preserves_audio_metadata()
    {
        var message = new Message
        {
            Id = 43,
            Audio = new Audio
            {
                FileId = "audio-file",
                FileUniqueId = "audio-unique",
                Duration = 4,
                FileName = "recording.m4a",
                MimeType = MimeTypeCatalog.AudioMp4,
                FileSize = 5678
            }
        };

        var file = Assert.Single(TelegramTransport.MapFiles(message));

        Assert.Equal("audio-file", file.FileId);
        Assert.Equal("recording.m4a", file.Name);
        Assert.Equal(MimeTypeCatalog.AudioMp4, file.MimeType);
        Assert.Equal(5678, file.Size);
    }

    [Fact]
    public void MapFiles_uses_stable_audio_fallback_metadata()
    {
        var message = new Message
        {
            Id = 44,
            Audio = new Audio
            {
                FileId = "audio-file",
                FileUniqueId = "audio-unique",
                Duration = 5
            }
        };

        var file = Assert.Single(TelegramTransport.MapFiles(message));

        Assert.Equal("telegram-audio-44.mp3", file.Name);
        Assert.Equal(MimeTypeCatalog.AudioMpeg, file.MimeType);
        Assert.Equal(0, file.Size);
    }
}
