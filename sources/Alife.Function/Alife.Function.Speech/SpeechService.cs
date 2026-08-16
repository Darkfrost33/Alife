using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.AIModelUtility;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using SpeechModel = Alife.Function.AIModelUtility.ISpeechModel;

namespace Alife.Function.Speech;

[Module("语音说话",
    "为AI增加语音转文字输出的能力。",
    defaultCategory: "Alife 官方/交互方式",
    EditorUI = typeof(SpeechServiceUI))]
[Description("此服务让你获得能将文字以语音形式输出的能力。")]
public class SpeechService(
    XmlFunctionCaller functionService,
    SpeechModel speechModel,
    ILogger<SpeechService> logger) :
    ChatBehaviour,
    IConfigurable<SpeechServiceConfig>
{
    public SpeechServiceConfig Configuration { get; set; } = null!;
    public bool IsSpeaking => activeTurn is { Completion.IsCompleted: false };

    /// <summary>当前是否真的在向声卡输出音频（区别于 IsSpeaking：等待门控/尚未收到首块音频时为 false）。</summary>
    public bool IsPlayingAudio
    {
        get
        {
            lock (turnLock)
                return activeTurn is { Playback.IsPlayingAudio: true };
        }
    }

    /// <summary>
    /// 估算当前这轮语音的剩余播放秒数。
    /// 只有在“后续不会再有新音频”确定后才返回数值，否则返回 null（未知）。
    /// </summary>
    public double? EstimatedRemainingSpeechSeconds
    {
        get
        {
            lock (turnLock)
                return activeTurn?.Playback.EstimatedRemainingSpeechSeconds;
        }
    }

    /// <summary>
    /// 为 true 时，() / （） 内的内容不送入语音合成（气泡等其它 speak 订阅方不受影响）。
    /// </summary>
    public bool OmitParentheticalText { get; set; }

    /// <summary>
    /// 播放门控：开始向声卡输出音频前轮询此委托，返回 true 才放行。
    /// 为 null 时不生效。由外部模块（如 ChatRoom）反射注入。
    /// </summary>
    public Func<bool>? SpeechPlaybackGate { get; set; }

    [XmlFunction(FunctionMode.Content, order: -10)]
    [Description("将文本以语音方式输出（这应该是你默认对外的交互方式）")]
    public Task Speak(XmlExecutorContext context, CancellationToken cancellationToken)
    {
        try
        {
            switch (context.CallMode)
            {
                case CallMode.Opening:
                    parentheticalDepth = 0;
                    GetOrCreateLatencyTrace()?.MarkSpeakOpened();
                    EnsureTurn(cancellationToken);
                    break;
                case CallMode.Closing:
                    parentheticalDepth = 0;
                    if (activeTurn is StreamingSpeechTurn streamingTurn)
                        streamingTurn.Flush();
                    break;
                case CallMode.Content:
                {
                    string content = FilterParenthetical(context.Content).Trim();
                    if (!string.IsNullOrWhiteSpace(content) && activeTurn is BufferedSpeechTurn bufferedTurn)
                        bufferedTurn.AddSegment(content);
                    break;
                }
            }
        }
        catch (OperationCanceledException) {}

        // 内容只负责入队，不能阻塞 XML token 的继续解析。
        return Task.CompletedTask;
    }

    protected override Task OnAwake()
    {
        XmlHandler xmlHandler = new(this);
        functionService.RegisterHandler(xmlHandler, cancellationToken: DestroyCancellationToken);
        functionService.ContentStreaming += OnContentStreaming;
        functionService.ChatCalledAsync += OnChatCalledAsync;
        ChatBot.ChatSent += OnChatSent;
        ChatBot.ChatReceived += OnChatReceived;
        return Task.CompletedTask;
    }

    protected override async Task OnDestroy()
    {
        functionService.ContentStreaming -= OnContentStreaming;
        functionService.ChatCalledAsync -= OnChatCalledAsync;
        ChatBot.ChatSent -= OnChatSent;
        ChatBot.ChatReceived -= OnChatReceived;

        SpeechTurn? turn;
        lock (turnLock)
        {
            turn = activeTurn;
            activeTurn = null;
        }

        if (turn != null)
        {
            turn.Cancel();
            try
            {
                await turn.Completion;
            }
            catch (OperationCanceledException) {}
        }
    }

    readonly object turnLock = new();
    SpeechTurn? activeTurn;
    SpeechLatencyTrace? activeLatencyTrace;
    int parentheticalDepth;

    bool StreamingRequested => Configuration.StreamingMode != SpeechStreamingMode.BufferedFile;

    void EnsureTurn(CancellationToken cancellationToken)
    {
        lock (turnLock)
        {
            if (activeTurn is { Completion.IsCompleted: false })
                return;

            if (StreamingRequested && speechModel is IStreamingSpeechModel streamingModel)
            {
                activeTurn = new StreamingSpeechTurn(
                    streamingModel,
                    speechModel,
                    logger,
                    Configuration,
                    activeLatencyTrace,
                    Configuration.StreamingMode == SpeechStreamingMode.Auto,
                    () => SpeechPlaybackGate,
                    cancellationToken,
                    DestroyCancellationToken);
            }
            else
            {
                if (Configuration.StreamingMode == SpeechStreamingMode.RealtimeStream)
                    logger.LogWarning("当前语音模型不支持实时串流，已降级为文件模式。");

                activeTurn = new BufferedSpeechTurn(
                    speechModel,
                    logger,
                    () => SpeechPlaybackGate,
                    cancellationToken,
                    DestroyCancellationToken);
            }
        }
    }

    void OnContentStreaming(XmlStreamingContent content)
    {
        if (!content.CallChain.Contains("speak"))
            return;

        if (!ShouldSpeakCharacter(content.Character))
            return;

        StreamingSpeechTurn? turn;
        lock (turnLock)
            turn = activeTurn as StreamingSpeechTurn;

        if (turn == null)
            return;

        turn.AddText(content.Character.ToString());
        if (IsStrongPunctuation(content.Character))
            turn.Flush();
    }

    string FilterParenthetical(string text)
    {
        if (!OmitParentheticalText || string.IsNullOrEmpty(text))
            return text;

        StringBuilder result = new(text.Length);
        foreach (char ch in text)
        {
            if (ShouldSpeakCharacter(ch))
                result.Append(ch);
        }

        return result.ToString();
    }

    bool ShouldSpeakCharacter(char ch)
    {
        if (!OmitParentheticalText)
            return true;

        if (ch is '(' or '（')
        {
            parentheticalDepth++;
            return false;
        }

        if ((ch is ')' or '）') && parentheticalDepth > 0)
        {
            parentheticalDepth--;
            return false;
        }

        return parentheticalDepth == 0;
    }

    void OnChatSent(string _)
    {
        lock (turnLock)
            activeLatencyTrace = CreateLatencyTrace();
    }

    void OnChatReceived(string _)
    {
        lock (turnLock)
            activeLatencyTrace?.MarkFirstLlmChunk();
    }

    SpeechLatencyTrace? GetOrCreateLatencyTrace()
    {
        lock (turnLock)
            return activeLatencyTrace ??= CreateLatencyTrace();
    }

    SpeechLatencyTrace? CreateLatencyTrace()
    {
        if (speechModel is not ISpeechLatencyDiagnostics {
                LatencyDiagnosticsEnabled: true
            } diagnostics)
            return null;

        return new SpeechLatencyTrace(logger, diagnostics.LatencyDiagnosticsName);
    }

    async Task OnChatCalledAsync()
    {
        SpeechTurn? turn;
        lock (turnLock)
            turn = activeTurn;

        if (turn == null)
            return;

        using (ChatBot.ResourceOccupiedReason.Rent("等待语音结束"))
        {
            turn.Complete();
            try
            {
                await turn.Completion;
            }
            catch (OperationCanceledException) {}
            catch (Exception e)
            {
                logger.LogWarning(e, "语音播放失败");
            }
            finally
            {
                lock (turnLock)
                {
                    if (ReferenceEquals(activeTurn, turn))
                        activeTurn = null;
                }
            }
        }
    }

    static bool IsStrongPunctuation(char ch) =>
        ch is '。' or '！' or '？' or '!' or '?' or '；' or ';' or '\n';

    abstract class SpeechTurn
    {
        protected SpeechTurn(
            Func<Func<bool>?> getPlaybackGate,
            CancellationToken requestCancellation,
            CancellationToken destroyCancellation,
            PlaybackProgress? playback = null)
        {
            this.getPlaybackGate = getPlaybackGate;
            Playback = playback ?? new PlaybackProgress();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                requestCancellation,
                destroyCancellation);
        }

        public PlaybackProgress Playback { get; }
        public abstract Task Completion { get; }
        public abstract void Complete();

        public void Cancel() => cancellation.Cancel();

        protected readonly Func<Func<bool>?> getPlaybackGate;
        protected readonly CancellationTokenSource cancellation;
    }

    sealed class BufferedSpeechTurn : SpeechTurn
    {
        public BufferedSpeechTurn(
            SpeechModel speechModel,
            ILogger logger,
            Func<Func<bool>?> getPlaybackGate,
            CancellationToken requestCancellation,
            CancellationToken destroyCancellation,
            PlaybackProgress? playback = null) :
            base(getPlaybackGate, requestCancellation, destroyCancellation, playback)
        {
            this.speechModel = speechModel;
            this.logger = logger;
            Completion = RunAsync(cancellation.Token);
        }

        public override Task Completion { get; }

        public void AddSegment(string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
                textQueue.Writer.TryWrite(text);
        }

        public override void Complete() => textQueue.Writer.TryComplete();

        readonly SpeechModel speechModel;
        readonly ILogger logger;
        readonly Channel<string> textQueue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions {
            SingleReader = true,
            SingleWriter = false,
        });

        async Task RunAsync(CancellationToken cancellationToken)
        {
            Channel<string> audioQueue = Channel.CreateBounded<string>(new BoundedChannelOptions(3) {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.Wait,
            });

            Task generator = GenerateAsync(audioQueue.Writer, cancellationToken);
            Task player = PlayGeneratedAudioAsync(audioQueue.Reader, cancellationToken);
            await Task.WhenAll(generator, player);
        }

        async Task GenerateAsync(ChannelWriter<string> output, CancellationToken cancellationToken)
        {
            try
            {
                await foreach (string text in textQueue.Reader.ReadAllAsync(cancellationToken))
                {
                    try
                    {
                        string? audioFile = await speechModel.GenerateSpeechFileAsync(text, cancellationToken);
                        if (audioFile != null)
                            await output.WriteAsync(audioFile, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception e)
                    {
                        logger.LogWarning(e, "语音合成失败：{Text}", text);
                    }
                }
            }
            finally
            {
                output.TryComplete();
            }
        }

        async Task PlayGeneratedAudioAsync(
            ChannelReader<string> input,
            CancellationToken cancellationToken)
        {
            await foreach (string audioFile in input.ReadAllAsync(cancellationToken))
                await PlayAudioFileAsync(audioFile, input, cancellationToken);
        }

        async Task PlayAudioFileAsync(
            string filePath,
            ChannelReader<string> audioQueue,
            CancellationToken cancellationToken)
        {
            TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

            await using AudioFileReader reader = new(filePath);
            SpeechSilenceTrimmer silenceTrimmer = new(reader);
            using WaveOutEvent speaker = new();
            speaker.Init(silenceTrimmer);
            speaker.PlaybackStopped += OnPlaybackStopped;

            await WaitForPlaybackGateAsync(getPlaybackGate, logger, cancellationToken);

            try
            {
                speaker.Play();
                Playback.IsPlayingAudio = true;
                Playback.SetRemainingSecondsProvider(() => {
                    if (textQueue.Reader.Completion.IsCompleted == false)
                        return null;
                    if (audioQueue.Completion.IsCompleted == false)
                        return null;
                    if (audioQueue.CanCount == false || audioQueue.Count != 0)
                        return null;
                    return silenceTrimmer.RemainingSeconds;
                });

                await using CancellationTokenRegistration registration =
                    cancellationToken.Register(() => speaker.Stop());
                await completion.Task;
            }
            finally
            {
                Playback.IsPlayingAudio = false;
                Playback.SetRemainingSecondsProvider(null);
            }

            void OnPlaybackStopped(object? _, StoppedEventArgs e)
            {
                if (e.Exception != null)
                    completion.TrySetException(e.Exception);
                else if (cancellationToken.IsCancellationRequested)
                    completion.TrySetCanceled(cancellationToken);
                else
                    completion.TrySetResult();
            }
        }
    }

    sealed class StreamingSpeechTurn : SpeechTurn
    {
        public StreamingSpeechTurn(
            IStreamingSpeechModel streamingModel,
            SpeechModel fallbackModel,
            ILogger logger,
            SpeechServiceConfig configuration,
            SpeechLatencyTrace? latencyTrace,
            bool allowInitialFallback,
            Func<Func<bool>?> getPlaybackGate,
            CancellationToken requestCancellation,
            CancellationToken destroyCancellation) :
            base(getPlaybackGate, requestCancellation, destroyCancellation)
        {
            this.streamingModel = streamingModel;
            this.fallbackModel = fallbackModel;
            this.logger = logger;
            this.configuration = configuration;
            this.latencyTrace = latencyTrace;
            this.allowInitialFallback = allowInitialFallback;
            Completion = RunAsync(cancellation.Token);
        }

        public override Task Completion { get; }

        public void AddText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            // 模型常把 speak 标签排版成独立行；不要把标签后的换行当成第一段语音提交。
            if (!segmentHasContent && string.IsNullOrWhiteSpace(text))
                return;

            segmentHasContent = true;
            commands.Writer.TryWrite(new StreamingCommand(StreamingCommandType.Text, text));
        }

        public void Flush()
        {
            if (!segmentHasContent)
                return;
            segmentHasContent = false;
            commands.Writer.TryWrite(new StreamingCommand(StreamingCommandType.Flush));
        }

        public override void Complete()
        {
            commands.Writer.TryWrite(new StreamingCommand(StreamingCommandType.Complete));
            commands.Writer.TryComplete();
        }

        readonly IStreamingSpeechModel streamingModel;
        readonly SpeechModel fallbackModel;
        readonly ILogger logger;
        readonly SpeechServiceConfig configuration;
        readonly SpeechLatencyTrace? latencyTrace;
        readonly bool allowInitialFallback;
        bool segmentHasContent;
        readonly Channel<StreamingCommand> commands = Channel.CreateUnbounded<StreamingCommand>(new UnboundedChannelOptions {
            SingleReader = true,
            SingleWriter = false,
        });

        async Task RunAsync(CancellationToken cancellationToken)
        {
            IStreamingSpeechSession session;
            try
            {
                latencyTrace?.MarkWebSocketConnecting();
                session = await streamingModel.StartStreamingSessionAsync(cancellationToken);
                latencyTrace?.MarkWebSocketConnected();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (allowInitialFallback)
            {
                logger.LogWarning(e, "实时语音连接失败，本轮已降级为文件模式。");
                await RunInitialFallbackAsync(cancellationToken);
                return;
            }

            await using (session)
            {
                using CancellationTokenSource operationCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task playback = PlayPcmStreamAsync(
                    session.ReceiveAudioAsync(operationCancellation.Token),
                    session.AudioFormat,
                    latencyTrace,
                    Playback,
                    getPlaybackGate,
                    logger,
                    operationCancellation.Token);
                Task sender = SendCommandsAsync(session, operationCancellation.Token);

                try
                {
                    Task firstFinished = await Task.WhenAny(sender, playback);
                    if (firstFinished.IsFaulted || firstFinished.IsCanceled)
                        await operationCancellation.CancelAsync();
                    await Task.WhenAll(sender, playback);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {}
            }
        }

        async Task SendCommandsAsync(
            IStreamingSpeechSession session,
            CancellationToken cancellationToken)
        {
            StringBuilder pendingText = new();
            int charactersSinceFlush = 0;
            int idleMilliseconds = Math.Clamp(configuration.IdleFlushMilliseconds, 100, 2000);
            int minimumIdleCharacters = Math.Clamp(configuration.MinimumIdleFlushCharacters, 1, 100);

            while (true)
            {
                Task<bool> waitForCommand = commands.Reader.WaitToReadAsync(cancellationToken).AsTask();
                Task? idleFlush = charactersSinceFlush >= minimumIdleCharacters
                    ? Task.Delay(idleMilliseconds, cancellationToken)
                    : null;

                if (idleFlush != null && await Task.WhenAny(waitForCommand, idleFlush) == idleFlush)
                {
                    await session.FlushAsync(cancellationToken);
                    latencyTrace?.MarkFirstFlushSent();
                    charactersSinceFlush = 0;
                }

                if (!await waitForCommand)
                    break;

                bool flush = false;
                bool complete = false;
                while (commands.Reader.TryRead(out StreamingCommand command))
                {
                    switch (command.Type)
                    {
                        case StreamingCommandType.Text:
                            pendingText.Append(command.Text);
                            charactersSinceFlush += command.Text.Length;
                            break;
                        case StreamingCommandType.Flush:
                            flush = true;
                            break;
                        case StreamingCommandType.Complete:
                            complete = true;
                            break;
                    }
                }

                if (pendingText.Length != 0)
                {
                    await session.SendTextAsync(pendingText.ToString(), cancellationToken);
                    latencyTrace?.MarkFirstTextSent();
                    pendingText.Clear();
                }

                if (flush)
                {
                    await session.FlushAsync(cancellationToken);
                    latencyTrace?.MarkFirstFlushSent();
                    charactersSinceFlush = 0;
                }

                if (complete)
                {
                    if (charactersSinceFlush != 0)
                    {
                        await session.FlushAsync(cancellationToken);
                        latencyTrace?.MarkFirstFlushSent();
                    }
                    await session.CompleteAsync(cancellationToken);
                    return;
                }
            }
        }

        async Task RunInitialFallbackAsync(CancellationToken cancellationToken)
        {
            BufferedSpeechTurn fallback = new(
                fallbackModel,
                logger,
                getPlaybackGate,
                cancellationToken,
                cancellationToken,
                Playback);
            StringBuilder segment = new();

            await foreach (StreamingCommand command in commands.Reader.ReadAllAsync(cancellationToken))
            {
                if (command.Type == StreamingCommandType.Text)
                {
                    segment.Append(command.Text);
                    continue;
                }

                if (segment.Length != 0)
                {
                    fallback.AddSegment(segment.ToString().Trim());
                    segment.Clear();
                }

                if (command.Type == StreamingCommandType.Complete)
                    break;
            }

            fallback.Complete();
            await fallback.Completion;
        }
    }

    enum StreamingCommandType
    {
        Text,
        Flush,
        Complete,
    }

    readonly record struct StreamingCommand(StreamingCommandType Type, string Text = "");

    static async Task WaitForPlaybackGateAsync(
        Func<Func<bool>?> getPlaybackGate,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        Func<bool>? gate = getPlaybackGate();
        if (gate == null)
            return;

        DateTime start = DateTime.Now;
        while (gate() == false)
        {
            if ((DateTime.Now - start).TotalSeconds >= PlaybackGateTimeoutSeconds)
            {
                logger.LogWarning("语音播放门控等待超时，已强制放行。");
                return;
            }
            await Task.Delay(PlaybackGatePollMilliseconds, cancellationToken);
        }
    }

    static bool TryPassPlaybackGate(
        Func<Func<bool>?> getPlaybackGate,
        DateTime waitStart,
        ILogger logger,
        ref bool timeoutLogged)
    {
        Func<bool>? gate = getPlaybackGate();
        if (gate == null || gate())
            return true;
        if ((DateTime.Now - waitStart).TotalSeconds < PlaybackGateTimeoutSeconds)
            return false;
        if (timeoutLogged == false)
        {
            timeoutLogged = true;
            logger.LogWarning("语音播放门控等待超时，已强制放行。");
        }
        return true;
    }

    static async Task PlayPcmStreamAsync(
        IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        SpeechStreamFormat format,
        SpeechLatencyTrace? latencyTrace,
        PlaybackProgress playback,
        Func<Func<bool>?> getPlaybackGate,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (format.BitsPerSample != 16)
            throw new NotSupportedException($"实时播放仅支持 16-bit PCM，实际为 {format.BitsPerSample}-bit。");

        BufferedWaveProvider buffer = new(new WaveFormat(
            format.SampleRate,
            format.BitsPerSample,
            format.Channels)) {
            BufferDuration = TimeSpan.FromSeconds(90),
            DiscardOnBufferOverflow = false,
            ReadFully = true,
        };
        using WaveOutEvent speaker = new() { DesiredLatency = 100, NumberOfBuffers = 3 };
        speaker.Init(buffer);

        bool started = false;
        bool timeoutLogged = false;
        DateTime gateWaitStart = DateTime.MinValue;
        await using CancellationTokenRegistration registration =
            cancellationToken.Register(() => speaker.Stop());

        try
        {
            await foreach (ReadOnlyMemory<byte> chunk in chunks.WithCancellation(cancellationToken))
            {
                if (chunk.IsEmpty)
                    continue;

                latencyTrace?.MarkFirstAudioReceived();
                byte[] bytes = chunk.ToArray();
                buffer.AddSamples(bytes, 0, bytes.Length);
                if (started)
                    continue;

                if (gateWaitStart == DateTime.MinValue)
                    gateWaitStart = DateTime.Now;
                if (TryPassPlaybackGate(getPlaybackGate, gateWaitStart, logger, ref timeoutLogged) == false)
                    continue;

                speaker.Play();
                latencyTrace?.MarkPlaybackStarted();
                playback.IsPlayingAudio = true;
                started = true;
            }

            if (started == false && buffer.BufferedBytes > 0)
            {
                await WaitForPlaybackGateAsync(getPlaybackGate, logger, cancellationToken);
                speaker.Play();
                latencyTrace?.MarkPlaybackStarted();
                playback.IsPlayingAudio = true;
                started = true;
            }

            if (started)
            {
                playback.SetRemainingSecondsProvider(() =>
                    buffer.BufferedBytes / (double)(format.SampleRate * format.Channels * 2));
            }

            while (started && buffer.BufferedBytes > 0)
                await Task.Delay(20, cancellationToken);

            if (started)
                speaker.Stop();
        }
        finally
        {
            playback.IsPlayingAudio = false;
            playback.SetRemainingSecondsProvider(null);
        }
    }

    const double PlaybackGateTimeoutSeconds = 15;
    const int PlaybackGatePollMilliseconds = 100;

    sealed class PlaybackProgress
    {
        public bool IsPlayingAudio
        {
            get => isPlayingAudio;
            set => isPlayingAudio = value;
        }

        public double? EstimatedRemainingSpeechSeconds
        {
            get
            {
                Func<double?>? provider;
                lock (remainingLock)
                    provider = remainingSecondsProvider;
                try
                {
                    return provider?.Invoke();
                }
                catch
                {
                    return null;
                }
            }
        }

        public void SetRemainingSecondsProvider(Func<double?>? provider)
        {
            lock (remainingLock)
                remainingSecondsProvider = provider;
        }

        volatile bool isPlayingAudio;
        Func<double?>? remainingSecondsProvider;
        readonly object remainingLock = new();
    }
}
