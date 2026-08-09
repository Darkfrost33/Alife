using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Alife.Function.AIModelUtility;

public interface ISpeechModel
{
    Task<string?> GenerateSpeechFileAsync(string text, CancellationToken cancellationToken = default);
}

/// <summary>
/// 流式语音模型输出的原始 PCM 格式。
/// </summary>
public record SpeechStreamFormat(int SampleRate, int BitsPerSample = 16, int Channels = 1);

/// <summary>
/// 单次实时语音合成会话。发送和接收允许同时进行。
/// </summary>
public interface IStreamingSpeechSession : IAsyncDisposable
{
    SpeechStreamFormat AudioFormat { get; }

    Task SendTextAsync(string text, CancellationToken cancellationToken = default);
    Task FlushAsync(CancellationToken cancellationToken = default);
    Task CompleteAsync(CancellationToken cancellationToken = default);

    IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAudioAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 可逐步接收文本并返回音频块的实时语音模型。
/// </summary>
public interface IStreamingSpeechModel
{
    Task<IStreamingSpeechSession> StartStreamingSessionAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// 由具体语音模型决定是否记录实时语音延迟，避免把模型专属诊断开关放在通用语音调度中。
/// </summary>
public interface ISpeechLatencyDiagnostics
{
    bool LatencyDiagnosticsEnabled { get; }
    string LatencyDiagnosticsName { get; }
}
