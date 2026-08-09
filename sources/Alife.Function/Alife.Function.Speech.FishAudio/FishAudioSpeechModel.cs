using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Foundation;
using Alife.Function.AIModelUtility;

namespace Alife.Function.Speech.FishAudio;

[Module("Fish Audio语音合成",
    "基于Fish Audio API的在线语音合成引擎",
    defaultCategory: "Alife 官方/模型接入/语音模型",
    EditorUI = typeof(FishAudioSpeechModelUI))]
public class FishAudioSpeechModel :
    ISpeechModel,
    IStreamingSpeechModel,
    ISpeechLatencyDiagnostics,
    IDisposable,
    IConfigurable<FishAudioSpeechModelConfig>
{
    public FishAudioSpeechModelConfig Configuration { get; set; } = null!;
    public bool LatencyDiagnosticsEnabled => Configuration.EnableLatencyDiagnostics;
    public string LatencyDiagnosticsName => "Fish Audio";

    public async Task<string?> GenerateSpeechFileAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        string apiKey = GetApiKey();
        string format = NormalizeFileFormat(Configuration.Format);
        string outputPath = Path.Combine(
            AlifePath.TempFolderPath,
            $"fish_audio_{CreateCacheKey(text, Configuration, format)}.{format}");
        if (File.Exists(outputPath))
            return outputPath;

        string? referenceId = NormalizeReferenceId(Configuration.ReferenceId);
        var requestBody = new Dictionary<string, object?> {
            ["text"] = text,
            ["format"] = format,
            ["prosody"] = new {
                speed = Math.Clamp(Configuration.Speed, 0.5, 2.0),
                volume = Math.Clamp(Configuration.Volume, -20, 20),
            },
        };
        if (referenceId != null)
            requestBody["reference_id"] = referenceId;

        using HttpRequestMessage request = new(HttpMethod.Post, Configuration.BaseUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Add("model", Configuration.Model);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"Fish Audio API 请求失败 ({(int)response.StatusCode} {response.ReasonPhrase}): {error}");
        }

        Directory.CreateDirectory(AlifePath.TempFolderPath);
        string temporaryPath = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using Stream responseStream =
                await response.Content.ReadAsStreamAsync(cancellationToken);
            await using (FileStream outputStream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await responseStream.CopyToAsync(outputStream, cancellationToken);
            }

            File.Move(temporaryPath, outputPath, overwrite: true);
            return outputPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<IStreamingSpeechSession> StartStreamingSessionAsync(
        CancellationToken cancellationToken = default)
    {
        int sampleRate = Math.Clamp(Configuration.StreamingSampleRate, 8000, 48000);
        int chunkLength = Math.Clamp(Configuration.StreamingChunkLength, 100, 300);
        FishAudioStreamingSession session = new(
            new Uri(Configuration.StreamingUrl),
            GetApiKey(),
            Configuration.Model,
            NormalizeReferenceId(Configuration.ReferenceId),
            Math.Clamp(Configuration.Speed, 0.5, 2.0),
            Math.Clamp(Configuration.Volume, -20, 20),
            sampleRate,
            chunkLength,
            NormalizeLatency(Configuration.StreamingLatency));
        await session.ConnectAsync(cancellationToken);
        return session;
    }

    readonly HttpClient httpClient = new();

    string GetApiKey()
    {
        string apiKey = string.IsNullOrWhiteSpace(Configuration.ApiKey)
            ? Environment.GetEnvironmentVariable("FISH_API_KEY") ?? ""
            : Configuration.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Fish Audio API Key 未配置。请在模块配置中填写，或设置 FISH_API_KEY 环境变量。");
        return apiKey.Trim();
    }

    static string NormalizeFileFormat(string format)
    {
        string normalized = format.Trim().ToLowerInvariant();
        return normalized is "mp3" or "wav"
            ? normalized
            : throw new InvalidOperationException(
                $"当前文件播放器不支持 Fish Audio 输出格式“{format}”，请选择 mp3 或 wav。");
    }

    static string NormalizeLatency(string latency)
    {
        string normalized = latency.Trim().ToLowerInvariant();
        return normalized is "normal" or "balanced" ? normalized : "balanced";
    }

    internal static string? NormalizeReferenceId(string? referenceId)
    {
        if (string.IsNullOrWhiteSpace(referenceId))
            return null;

        string trimmed = referenceId.Trim();
        Match urlMatch = Regex.Match(trimmed, @"[A-Za-z0-9_-]{8,128}", RegexOptions.CultureInvariant);
        string candidate = urlMatch.Success ? urlMatch.Value : trimmed;
        if (!Regex.IsMatch(candidate, @"^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException(
                "音色模型 ID 格式不正确。请填写 Fish Audio 音色详情页里的 Model ID" +
                "（仅允许字母、数字、下划线和短横线），不要填写中文名称。");
        }

        return candidate;
    }

    static string CreateCacheKey(
        string text,
        FishAudioSpeechModelConfig configuration,
        string format)
    {
        string cacheInput = string.Join(
            "\n",
            text,
            configuration.Model,
            NormalizeReferenceId(configuration.ReferenceId) ?? configuration.ReferenceId,
            format,
            configuration.Speed.ToString(CultureInfo.InvariantCulture),
            configuration.Volume.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheInput)));
    }

    public void Dispose() => httpClient.Dispose();

    sealed class FishAudioStreamingSession : IStreamingSpeechSession
    {
        public FishAudioStreamingSession(
            Uri url,
            string apiKey,
            string model,
            string? referenceId,
            double speed,
            double volume,
            int sampleRate,
            int chunkLength,
            string latency)
        {
            this.url = url;
            this.referenceId = referenceId;
            this.speed = speed;
            this.volume = volume;
            this.chunkLength = chunkLength;
            this.latency = latency;
            AudioFormat = new SpeechStreamFormat(sampleRate);
            socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}");
            socket.Options.SetRequestHeader("model", model);
        }

        public SpeechStreamFormat AudioFormat { get; }

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            await socket.ConnectAsync(url, cancellationToken);
            byte[] startEvent = FishAudioMessagePack.SerializeStart(
                AudioFormat.SampleRate,
                chunkLength,
                latency,
                referenceId,
                speed,
                volume);
            await SendAsync(startEvent, cancellationToken);
        }

        public Task SendTextAsync(string text, CancellationToken cancellationToken = default) =>
            SendAsync(FishAudioMessagePack.SerializeText(text), cancellationToken);

        public Task FlushAsync(CancellationToken cancellationToken = default) =>
            SendAsync(FishAudioMessagePack.SerializeEvent("flush"), cancellationToken);

        public async Task CompleteAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref completed, 1) != 0)
                return;
            await SendAsync(FishAudioMessagePack.SerializeEvent("stop"), cancellationToken);
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAudioAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            byte[] receiveBuffer = new byte[32 * 1024];
            while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
            {
                using MemoryStream message = new();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(receiveBuffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close)
                        yield break;
                    message.Write(receiveBuffer, 0, result.Count);
                } while (!result.EndOfMessage);

                FishAudioMessagePack.Response response =
                    FishAudioMessagePack.DeserializeResponse(message.ToArray());
                switch (response.Event)
                {
                    case "audio" when response.Audio is { Length: > 0 }:
                        yield return response.Audio;
                        break;
                    case "finish" when response.Reason == "error":
                        throw new InvalidOperationException("Fish Audio 实时语音会话返回错误。");
                    case "finish":
                        yield break;
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Completed", timeout.Token);
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException)
            {
                socket.Abort();
            }
            finally
            {
                socket.Dispose();
            }
        }

        readonly Uri url;
        readonly string? referenceId;
        readonly double speed;
        readonly double volume;
        readonly int chunkLength;
        readonly string latency;
        readonly ClientWebSocket socket = new();
        readonly SemaphoreSlim sendLock = new(1, 1);
        int completed;

        async Task SendAsync(byte[] payload, CancellationToken cancellationToken)
        {
            await sendLock.WaitAsync(cancellationToken);
            try
            {
                await socket.SendAsync(payload, WebSocketMessageType.Binary, true, cancellationToken);
            }
            finally
            {
                sendLock.Release();
            }
        }
    }
}
