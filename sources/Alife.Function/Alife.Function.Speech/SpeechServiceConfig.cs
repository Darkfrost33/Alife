namespace Alife.Function.Speech;

public enum SpeechStreamingMode
{
    Auto,
    BufferedFile,
    RealtimeStream,
}

public class SpeechServiceConfig
{
    public SpeechStreamingMode StreamingMode { get; set; } = SpeechStreamingMode.Auto;

    /// <summary>LLM 暂停输出多久后，将当前文本强制提交给实时 TTS。</summary>
    public int IdleFlushMilliseconds { get; set; } = 450;

    /// <summary>空闲自动提交前至少累计的字符数。</summary>
    public int MinimumIdleFlushCharacters { get; set; } = 8;
}
