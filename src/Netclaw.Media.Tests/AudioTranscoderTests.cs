// -----------------------------------------------------------------------
// <copyright file="AudioTranscoderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Concentus;
using Concentus.Enums;
using Concentus.Oggfile;
using System.Text;
using Netclaw.Media;
using Xunit;

namespace Netclaw.Media.Tests;

public sealed class AudioTranscoderTests
{
    private static byte[] SynthesizeOgg(
        int sampleRate,
        int channels,
        int durationSeconds,
        OpusMode? forceMode = null,
        int? bitrate = null,
        bool silent = false)
    {
        var encoder = OpusCodecFactory.CreateEncoder(sampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        if (forceMode is { } mode)
            encoder.ForceMode = mode;
        if (bitrate is { } rate)
            encoder.Bitrate = rate;
        var output = new MemoryStream();
        var writer = new OpusOggWriteStream(encoder, output, new OpusTags(), sampleRate, 5, leaveOpen: false);
        var samplesPerPacket = sampleRate / 50; // 20ms frames
        var samples = new short[samplesPerPacket * 50 * durationSeconds];
        if (!silent)
        {
            var amplitude = (short)(short.MaxValue * 0.25);
            for (var i = 0; i < samples.Length; i++)
                samples[i] = (short)(amplitude * Math.Sin(2 * Math.PI * 440 * i / sampleRate));
        }
        writer.WriteSamples(samples, 0, samples.Length);
        writer.Finish();
        return output.ToArray();
    }

    private static (int Total, int NonZero) CountNonZeroSamples(byte[] wav)
    {
        var dataBytes = BitConverter.ToInt32(wav, 40);
        var nonZero = 0;
        for (var i = 44; i < 44 + dataBytes; i += 2)
        {
            if (BitConverter.ToInt16(wav, i) != 0)
                nonZero++;
        }

        return (dataBytes / 2, nonZero);
    }

    private static (ushort Format, ushort Channels, int SampleRate, ushort BitsPerSample, int DataBytes)
        ParseWavHeader(byte[] wav)
    {
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        var format = BitConverter.ToUInt16(wav, 20);
        var channels = BitConverter.ToUInt16(wav, 22);
        var sampleRate = BitConverter.ToInt32(wav, 24);
        var bitsPerSample = BitConverter.ToUInt16(wav, 34);
        var dataBytes = BitConverter.ToInt32(wav, 40);
        return (format, channels, sampleRate, bitsPerSample, dataBytes);
    }

    [Fact]
    public void Telegram_style_ogg_opus_transcodes_to_wav()
    {
        var ogg = SynthesizeOgg(sampleRate: 16000, channels: 1, durationSeconds: 1);

        var wav = AudioTranscoder.TranscodeOpusOggToWav(ogg, maxDecodedBytes: 25 * 1024 * 1024);

        Assert.NotNull(wav);
        Assert.StartsWith("RIFF", Encoding.ASCII.GetString(wav, 0, 4), StringComparison.Ordinal);
        var header = ParseWavHeader(wav);
        Assert.Equal(1, header.Format);
        Assert.Equal(1, header.Channels);
        Assert.Equal(48000, header.SampleRate);
        Assert.Equal(16, header.BitsPerSample);
        Assert.True(header.DataBytes > 0, "decoded WAV must contain PCM samples");
    }

    [Fact]
    public void Hybrid_speech_stream_decodes_to_audible_samples()
    {
        // Regression: Concentus 2.2.2 decodes hybrid (SILK) Opus streams to
        // pure zero PCM when the decoder output rate is 16 kHz. Telegram voice
        // notes are hybrid streams, so the 48 kHz output rate is required and
        // the decoded samples must carry real audio energy.
        var ogg = SynthesizeOgg(sampleRate: 48000, channels: 1, durationSeconds: 1,
            forceMode: OpusMode.MODE_HYBRID, bitrate: 32000);

        var wav = AudioTranscoder.TranscodeOpusOggToWav(ogg, maxDecodedBytes: 25 * 1024 * 1024);

        var header = ParseWavHeader(wav);
        Assert.Equal(48000, header.SampleRate);
        var (total, nonZero) = CountNonZeroSamples(wav);
        Assert.True(nonZero > total / 2,
            $"expected audible PCM but only {nonZero}/{total} samples are non-zero");
    }

    [Fact]
    public void Silent_stream_throws_silence_exception()
    {
        // CELT re-encodes pure zero input to packets that decode back to zero
        // PCM. Silence must surface as a loud rejection, never as a
        // "successful" WAV that would be inlined to the model.
        var ogg = SynthesizeOgg(sampleRate: 48000, channels: 1, durationSeconds: 1,
            forceMode: OpusMode.MODE_CELT_ONLY, silent: true);

        var ex = Assert.Throws<AudioSilenceException>(
            () => AudioTranscoder.TranscodeOpusOggToWav(ogg, maxDecodedBytes: 25 * 1024 * 1024));

        Assert.Contains("silent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decoded_wav_exceeding_budget_throws()
    {
        var ogg = SynthesizeOgg(sampleRate: 16000, channels: 1, durationSeconds: 1);

        var ex = Assert.Throws<AudioTranscodeException>(
            () => AudioTranscoder.TranscodeOpusOggToWav(ogg, maxDecodedBytes: 1000));

        Assert.Contains("exceeds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Non_opus_ogg_throws_expected_conversion_failure()
    {
        // A RIFF-wrapped garbage payload is not Opus; must fail cleanly, not crash.
        var notOgg = Encoding.ASCII.GetBytes("RIFF\x10\x00\x00\x00WAVEgarbagegarbagegarbage");

        var ex = Assert.Throws<AudioTranscodeException>(
            () => AudioTranscoder.TranscodeOpusOggToWav(notOgg, maxDecodedBytes: 25 * 1024 * 1024));

        Assert.False(string.IsNullOrEmpty(ex.Message));
    }
}
