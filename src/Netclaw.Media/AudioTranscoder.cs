// -----------------------------------------------------------------------
// <copyright file="AudioTranscoder.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Concentus;
using Concentus.Oggfile;
using System.Text;

namespace Netclaw.Media;

/// <summary>
/// Expected failure converting an Opus-in-OGG file to WAV: the input is not
/// Opus, the decoded output exceeds the byte budget, or it contains no audio.
/// Callers treat this as a path-only fallback, not an error.
/// </summary>
public class AudioTranscodeException : Exception
{
    public AudioTranscodeException(string message) : base(message)
    {
    }
}

/// <summary>
/// The Opus stream decoded successfully but every sample is zero: the
/// recording carries no audible content. Callers must not inline this audio;
/// the user needs to know their recording is silent.
/// </summary>
public sealed class AudioSilenceException : AudioTranscodeException
{
    public AudioSilenceException(string message) : base(message)
    {
    }
}

/// <summary>
/// Transcodes Opus-in-OGG audio to mono 16 kHz PCM16 WAV for direct model
/// input. Uses the pure-managed Concentus codec; no native library is needed.
/// </summary>
public static class AudioTranscoder
{
    // Concentus 2.2.2 decodes hybrid-mode (SILK) streams to all-zero PCM when
    // the decoder output rate is 16 kHz; 48 kHz output is unaffected. Telegram
    // voice notes are hybrid streams, so the decode rate must stay at 48 kHz.
    private const int DecodeSampleRate = 48000;
    private const int DecodeChannels = 1;
    private const int BitsPerSample = 16;
    private const int BytesPerSample = BitsPerSample / 8;

    // maxDecodedBytes is expressed against the historical 16 kHz output rate.
    // 48 kHz yields 3x the samples per second, so scale the caller's budget to
    // keep the same source-duration capacity.
    private const int BudgetRateScaleFactor = 3;

    static AudioTranscoder()
    {
        // Require the managed codec so there is no runtime native dependency
        // and no silent probe/fallback for a platform native lib.
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
    }

    /// <summary>
    /// Decodes <paramref name="oggBytes"/> as Opus-in-OGG and returns a mono
    /// 48 kHz PCM16 WAV. Throws <see cref="AudioSilenceException"/> when the
    /// input decodes to pure silence, <see cref="AudioTranscodeException"/>
    /// when the input is not Opus or the output exceeds the decoded-byte
    /// budget. Any other exception is an unexpected converter failure.
    /// </summary>
    public static byte[] TranscodeOpusOggToWav(byte[] oggBytes, long maxDecodedBytes)
    {
        var decoder = OpusCodecFactory.CreateDecoder(DecodeSampleRate, DecodeChannels, null);
        using var input = new MemoryStream(oggBytes);
        var reader = new OpusOggReadStream(decoder, input);
        try
        {
            var pcm = new List<short>();
            var scaledBudget = maxDecodedBytes * BudgetRateScaleFactor;
            while (reader.HasNextPacket)
            {
                if (reader.DecodeNextPacket() is not { } packet)
                    break;

                var decodedBytes = (long)(pcm.Count + packet.Length) * BytesPerSample;
                if (decodedBytes > scaledBudget)
                {
                    throw new AudioTranscodeException(
                        $"decoded wav ({decodedBytes} bytes) exceeds the {scaledBudget}-byte budget");
                }

                pcm.AddRange(packet);
            }

            if (pcm.Count == 0)
                throw new AudioTranscodeException("no opus audio decoded");

            if (IsSilent(pcm))
                throw new AudioSilenceException(
                    $"decoded wav ({pcm.Count} samples) is silent: every sample is zero");

            return BuildWav(pcm);
        }
        finally
        {
            reader.Close();
        }
    }

    private static bool IsSilent(List<short> pcm)
    {
        foreach (var sample in pcm)
        {
            if (sample != 0)
                return false;
        }

        return true;
    }

    private static byte[] BuildWav(List<short> pcm)
    {
        var dataSize = pcm.Count * BytesPerSample;
        using var stream = new MemoryStream(44 + dataSize);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)DecodeChannels);
        writer.Write(DecodeSampleRate);
        writer.Write(DecodeSampleRate * DecodeChannels * BytesPerSample);
        writer.Write((short)(DecodeChannels * BytesPerSample));
        writer.Write((short)BitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        foreach (var sample in pcm)
            writer.Write(sample);
        return stream.ToArray();
    }
}
