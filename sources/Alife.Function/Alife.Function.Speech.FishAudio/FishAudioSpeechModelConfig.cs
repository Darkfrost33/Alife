namespace Alife.Function.Speech.FishAudio;

public class FishAudioSpeechModelConfig
{
    public string BaseUrl { get; set; } = "https://api.fish.audio/v1/tts";
    public string StreamingUrl { get; set; } = "wss://api.fish.audio/v1/tts/live";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "s2.1-pro-free";
    public string ReferenceId { get; set; } = "";
    public string Format { get; set; } = "mp3";
    public double Speed { get; set; } = 1.0;
    /// <summary>音量调整，单位 dB。0 为不变，正数更响，负数更轻。</summary>
    public double Volume { get; set; }
    public int StreamingSampleRate { get; set; } = 24000;
    public int StreamingChunkLength { get; set; } = 100;
    public string StreamingLatency { get; set; } = "balanced";
    public bool EnableLatencyDiagnostics { get; set; }
}
