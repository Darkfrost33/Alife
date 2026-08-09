using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Alife.Function.AIModelUtility;

namespace Alife.Function.Speech.FishAudio;

[Module("Fish Audio语音合成", "基于Fish Audio API的在线语音合成引擎",
    defaultCategory: "Alife 官方/模型接入/语音模型",
    EditorUI = typeof(FishAudioSpeechModelUI))]
public class FishAudioSpeechModel :
    ISpeechModel,
    IDisposable,
    IConfigurable<FishAudioSpeechModelConfig>
{
    public FishAudioSpeechModelConfig Configuration { get; set; } = null!;

    public async Task<string?> GenerateSpeechFileAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        FishAudioSpeechModelConfig configuration = Configuration;
        string apiKey = string.IsNullOrWhiteSpace(configuration.ApiKey)
            ? Environment.GetEnvironmentVariable("FISH_API_KEY") ?? ""
            : configuration.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException(
                "Fish Audio API Key 未配置。请在模块配置中填写，或设置 FISH_API_KEY 环境变量。");

        string format = NormalizeFormat(configuration.Format);
        string outputPath = Path.Combine(
            AlifePath.TempFolderPath,
            $"fish_audio_{CreateCacheKey(text, configuration, format)}.{format}");
        if (File.Exists(outputPath))
            return outputPath;

        string? referenceId = NormalizeReferenceId(configuration.ReferenceId);
        var requestBody = new Dictionary<string, object?> {
            ["text"] = text,
            ["format"] = format,
            ["prosody"] = new {
                speed = Math.Clamp(configuration.Speed, 0.5, 2.0),
                volume = Math.Clamp(configuration.Volume, -20, 20),
            },
        };
        if (referenceId != null)
            requestBody["reference_id"] = referenceId;

        using var request = new HttpRequestMessage(HttpMethod.Post, configuration.BaseUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Headers.Add("model", configuration.Model);
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
            await using (var outputStream = new FileStream(
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

    readonly HttpClient httpClient = new();

    static string NormalizeFormat(string format)
    {
        string normalized = format.Trim().ToLowerInvariant();
        return normalized is "mp3" or "wav"
            ? normalized
            : throw new InvalidOperationException(
                $"当前播放器不支持 Fish Audio 输出格式“{format}”，请选择 mp3 或 wav。");
    }

    static string? NormalizeReferenceId(string? referenceId)
    {
        if (string.IsNullOrWhiteSpace(referenceId))
            return null;

        string trimmed = referenceId.Trim();
        Match urlMatch = Regex.Match(
            trimmed,
            @"[A-Za-z0-9_-]{8,128}",
            RegexOptions.CultureInvariant);
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

    public void Dispose()
    {
        httpClient.Dispose();
    }
}
