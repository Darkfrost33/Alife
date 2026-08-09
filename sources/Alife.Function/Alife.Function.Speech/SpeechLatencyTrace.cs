using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Alife.Function.Speech;

/// <summary>
/// 单轮实时语音的轻量延迟追踪。所有时间只保存在内存中，并在首音播放时写一条运行日志。
/// </summary>
internal sealed class SpeechLatencyTrace(ILogger logger, string diagnosticsName)
{
    public void MarkFirstLlmChunk() => MarkOnce(ref firstLlmChunk);
    public void MarkSpeakOpened() => MarkOnce(ref speakOpened);
    public void MarkWebSocketConnecting() => MarkOnce(ref webSocketConnecting);
    public void MarkWebSocketConnected() => MarkOnce(ref webSocketConnected);
    public void MarkFirstTextSent() => MarkOnce(ref firstTextSent);
    public void MarkFirstFlushSent() => MarkOnce(ref firstFlushSent);
    public void MarkFirstAudioReceived() => MarkOnce(ref firstAudioReceived);

    public void MarkPlaybackStarted()
    {
        MarkOnce(ref playbackStarted);
        if (Interlocked.Exchange(ref logged, 1) != 0)
            return;

        logger.LogInformation(
            "[{DiagnosticsName} TTS延迟] 首个LLM片段={FirstLlmMs}ms | 进入speak={SpeakOpenedMs}ms | WS建连+Start={WebSocketConnectMs}ms | 首文本发送={FirstTextSentMs}ms | 首次Flush={FirstFlushMs}ms | Flush到首音频={FlushToAudioMs}ms | 首音播放={PlaybackStartedMs}ms",
            diagnosticsName,
            FromRequest(firstLlmChunk),
            FromRequest(speakOpened),
            Between(webSocketConnecting, webSocketConnected),
            FromRequest(firstTextSent),
            FromRequest(firstFlushSent),
            Between(firstFlushSent, firstAudioReceived),
            FromRequest(playbackStarted));
    }

    readonly long requestStarted = Stopwatch.GetTimestamp();
    long firstLlmChunk;
    long speakOpened;
    long webSocketConnecting;
    long webSocketConnected;
    long firstTextSent;
    long firstFlushSent;
    long firstAudioReceived;
    long playbackStarted;
    int logged;

    static void MarkOnce(ref long timestamp)
    {
        long now = Stopwatch.GetTimestamp();
        Interlocked.CompareExchange(ref timestamp, now, 0);
    }

    double? FromRequest(long timestamp) => ElapsedMilliseconds(requestStarted, timestamp);

    static double? Between(long start, long end) => ElapsedMilliseconds(start, end);

    static double? ElapsedMilliseconds(long start, long end)
    {
        if (start == 0 || end == 0 || end < start)
            return null;

        return Math.Round(Stopwatch.GetElapsedTime(start, end).TotalMilliseconds, 1);
    }
}
