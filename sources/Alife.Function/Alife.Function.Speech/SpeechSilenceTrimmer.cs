using System;
using System.Collections.Generic;
using NAudio.Wave;

namespace Alife.Function.Speech;

public class SpeechSilenceTrimmer : ISampleProvider
{
    public WaveFormat WaveFormat { get; }

    /// <summary>按已裁掉首尾静音后的采样，估算还剩多少秒没播完。</summary>
    public double RemainingSeconds
    {
        get
        {
            int remainingSamples = samples.Length - position;
            int samplesPerSecond = WaveFormat.SampleRate * WaveFormat.Channels;
            if (remainingSamples <= 0 || samplesPerSecond <= 0)
                return 0;
            return remainingSamples / (double)samplesPerSecond;
        }
    }

    readonly float[] samples;
    int position;

    public SpeechSilenceTrimmer(ISampleProvider source, float threshold = 0.01f)
    {
        WaveFormat = source.WaveFormat;
        var allSamples = new List<float>();
        float[] tempBuffer = new float[WaveFormat.SampleRate];
        int read;
        while ((read = source.Read(tempBuffer, 0, tempBuffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                allSamples.Add(tempBuffer[i]);
        }

        int start = 0;
        while (start < allSamples.Count && Math.Abs(allSamples[start]) <= threshold)
            start++;

        int end = allSamples.Count - 1;
        while (end > start && Math.Abs(allSamples[end]) <= threshold)
            end--;

        if (start <= end)
        {
            int length = end - start + 1;
            samples = new float[length];
            allSamples.CopyTo(start, samples, 0, length);
        }
        else
        {
            samples = Array.Empty<float>();
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int available = samples.Length - position;
        int toCopy = Math.Min(available, count);
        if (toCopy > 0)
        {
            samples.AsSpan(position, toCopy).CopyTo(buffer.AsSpan(offset, toCopy));
            position += toCopy;
        }
        return toCopy;
    }
}
