using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.Interpreter;
using Microsoft.SemanticKernel;

namespace Alife.Function.ChatRoom;

public class ChatRoomConfig
{
    [Description("你（用户）在聊天室中的名称")]
    public string UserName { get; set; } = "管理员";

    [Description("是否将你对本角色的发言自动转播给聊天室其他成员")]
    public bool ShareUserMessages { get; set; } = true;

    [Description("哪些消息来源视为用户发言（转播用），用逗号分隔")]
    public string UserSources { get; set; } = "ChatWindow,DeskPetService,SpeechService,AuditoryService";

    [Description("哪些消息来源属于麦克风语音（面向全场发言，只转播给没有语音识别能力的成员），用逗号分隔")]
    public string VoiceSources { get; set; } = "AuditoryService";

    [Description("用户离场期间，成员间最多连续对话的条数，超过后自动静音话题，防止无限循环")]
    public int MaxAITalkRounds { get; set; } = 8;

    [Description("响应顺位：数字越小越先响应用户发言，相同时按激活顺序排。语音发言只有顺位最高的带麦角色会立即回应")]
    public int ResponseOrder { get; set; } = 0;

    [Description("响应顺位间的延迟秒数：用户发言会按顺位依次延迟转播给各成员，让前一位先说完")]
    public float ResponseDelaySeconds { get; set; } = 6f;
}

[Module("桌宠聊天室", "让同时激活的多个角色进入同一个聊天室：用户的发言全员可见，角色用<speak>说的话其他角色也能听到，并自带防无限对话机制。",
    url: "https://github.com/Darkfrost33/Alife",
    defaultCategory: "Darkfrost的神秘插件")]
[Description("聊天室功能：所有同时激活的角色共处一室，管理员和成员说的话全员可闻。")]
public class ChatRoomService(XmlFunctionCaller functionService) : InteractiveModule<ChatRoomService>, IConfigurable<ChatRoomConfig>
{
    public ChatRoomConfig? Configuration { get; set; }

    public string MemberName => Character.Name;
    public int MaxAITalkRounds => Configuration?.MaxAITalkRounds ?? 8;
    public int ResponseOrder => Configuration?.ResponseOrder ?? 0;
    public float ResponseDelaySeconds => Configuration?.ResponseDelaySeconds ?? 6f;

    /// <summary>本角色是否启用了语音识别类模块（即能直接听到用户的麦克风发言）。</summary>
    public bool CanHearVoice => ChatActivity.EventModules
        .Any(module => IsInSourceList(Configuration?.VoiceSources, module.GetType().Name));

    /// <summary>
    /// 本角色当前是否正在回复中：刚被交棒（等待合批生效）、LLM生成中、或语音合成还在播放。
    /// Hub 在交棒给下一位成员前会等待场上所有人安静，防止多个角色同时开口说话。
    /// </summary>
    public bool IsBusyReplying
    {
        get
        {
            //角色销毁过程中模块状态可能已不完整，异常时按"不忙"处理，避免拖住其他成员的排队
            try
            {
                if (DateTime.Now < pendingReplyUntil)
                    return true;
                if (ChatBot.IsChatting)
                    return true;

                //通过反射探测语音类模块的 IsSpeaking 状态（如 SpeechService），避免对具体模块产生硬依赖
                foreach (object module in ChatActivity.EventModules)
                {
                    PropertyInfo? isSpeaking = module.GetType().GetProperty("IsSpeaking", typeof(bool));
                    if (isSpeaking != null && (bool)isSpeaking.GetValue(module)!)
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>标记本角色即将回复：消息送达到LLM实际开始响应之间存在合批间隙，此标记用于填补该窗口。</summary>
    public void MarkPendingReply(float seconds = 8f)
    {
        pendingReplyUntil = DateTime.Now.AddSeconds(seconds);
    }

    DateTime pendingReplyUntil = DateTime.MinValue;

    [XmlFunction(FunctionMode.Content)]
    [Description("以文字形式在聊天室公开发言，所有成员都能看到。（用<speak>说话时无需此标签，说出的话大家自然听得到）")]
    public void Say(XmlExecutorContext context)
    {
        if (context.CallMode == CallMode.Closing)
            ChatRoomHub.BroadcastMemberSay(this, context.FullContent.Trim());
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("查看聊天室当前的在线成员")]
    public void Members()
    {
        string[] names = ChatRoomHub.GetMemberNames();
        Poke(names.Length == 0
            ? "聊天室当前没有在线成员"
            : $"聊天室当前在线成员：{string.Join("、", names)}");
    }

    /// <summary>接收一条聊天室推送（走 Poke 队列，自动合批）。</summary>
    public void ReceiveRoomMessage(string message)
    {
        ChatBot.Poke(message);
    }

    public override async Task AwakeAsync(AwakeContext context)
    {
        await base.AwakeAsync(context);

        XmlHandler xmlHandler = new(this);
        functionService.RegisterHandler(xmlHandler, nameof(Say));

        //旁听<speak>：给已有的speak标签追加一个处理器（order靠后，不影响语音/气泡的正常执行），
        //角色说出口的话自动转播给聊天室其他成员，无需AI额外操作。
        XmlHandler speakRelay = new("ChatRoomSpeakRelay");
        speakRelay.Functions.Add(new XmlFunction {
            Name = "speak",
            Order = 1000,
            Mode = FunctionMode.Content,
            Invoker = (tagContext, _) => {
                if (tagContext.CallMode == CallMode.Closing && tagContext is XmlExecutorContext executorContext)
                    ChatRoomHub.BroadcastMemberSay(this, executorContext.FullContent.Trim(), implicitSpeech: true);
                return Task.CompletedTask;
            }
        });
        functionService.RegisterHandlerWithoutDocument(speakRelay);

        Prompt($"""
                此服务为所有同时激活的角色提供了一个共享聊天室，你已自动入场。

                ## 你的身份
                你在聊天室中的名字是：{Character.Name}。管理员（用户）是：{Configuration?.UserName ?? "管理员"}。

                ## 聊天规则
                1. 这是一个公开场合：管理员和成员说的话（包括你用<speak>说出的话），在场所有人都能听到，不需要任何额外操作。你收到的"[聊天室]"开头的推送就是别人的发言。
                2. 管理员的语音（麦克风）发言面向全场，在场成员可能同时听到。注意根据称呼和上下文判断是在对谁说话：点名了别人就保持沉默，没点名时也要避免全员抢答。
                3. 如果你没有说话能力（无<speak>标签），可用 <say> 标签以文字形式公开发言；普通文本输出只有管理员能看到。
                4. 这是多人场合：与你无关的对话保持沉默即可，不必每条都回复，避免刷屏；发言尽量简短自然。
                5. 系统为成员安排了响应顺位，管理员的发言会按顺位依次通知大家：轮到你时才会收到消息或提示，此时前面的成员可能已经回答过，注意衔接、不要重复别人说过的内容。
                6. 系统会限制成员间连续对话的轮数，收到收尾提示后请自然结束话题，等管理员发言后再继续。
                7. 用 <members/> 可查看当前在线成员。
                """);
    }

    public override async Task StartAsync(Kernel kernel, ChatActivity chatActivity)
    {
        await base.StartAsync(kernel, chatActivity);

        ChatBot.ChatSent += OnChatSent;
        ChatRoomHub.Join(this);

        //给主动事件类模块（如SystemEventService）注入报点门控：
        //其他成员正在发言时推迟本角色的周期报点，避免自主活动打断聊天室的说话顺序。
        //按属性名反射探测，避免硬依赖，对方模块不支持时自动跳过。
        foreach (object module in ChatActivity.EventModules)
        {
            PropertyInfo? gate = module.GetType().GetProperty("ProactivePokeGate", typeof(Func<bool>));
            gate?.SetValue(module, (Func<bool>)(() => ChatRoomHub.IsRoomQuietFor(this)));
        }
    }

    public override async Task DestroyAsync()
    {
        ChatBot.ChatSent -= OnChatSent;
        ChatRoomHub.Leave(this);

        //撤销注入的报点门控，避免残留指向已销毁模块的委托
        foreach (object module in ChatActivity.EventModules)
        {
            PropertyInfo? gate = module.GetType().GetProperty("ProactivePokeGate", typeof(Func<bool>));
            if (gate?.GetValue(module) is Func<bool>)
                gate.SetValue(module, null);
        }

        await base.DestroyAsync();
    }

    /// <summary>
    /// 监听发往本角色的消息，识别出用户发言后转播给聊天室其他成员。
    /// 用户消息的判定依据是各交互模块统一附带的"消息来源:[Xxx]"标记。
    /// 注意 ChatSent 收到的是过滤后的最终消息，MessageFilter 等模块可能已在首尾追加时间戳、提示词。
    /// </summary>
    void OnChatSent(string message)
    {
        if (Configuration is not { ShareUserMessages: true })
            return;

        //系统合批推送（Poke）中也可能夹带"消息来源"标记（如桌宠位置回报），不属于用户发言
        if (message.Contains(ChatBot.PokeMessageTag))
            return;

        Match match = SourceRegex.Match(message);
        if (match.Success == false)
            return;

        string source = match.Groups["src"].Value;
        if (IsInSourceList(Configuration.UserSources, source) == false)
            return;

        //取来源标记之后的正文，并剔除尾部由过滤器追加的"(xxx)"格式提示词
        string content = message.Substring(match.Index + match.Length).Trim();
        content = TrailingHintRegex.Replace(content, "").Trim();
        if (content.Length == 0)
            return;

        //麦克风语音是面向全场的发言，打字输入则是对本角色的定向发言
        bool isVoice = IsInSourceList(Configuration.VoiceSources, source);
        if (isVoice && ChatRoomHub.IsPrimaryVoiceResponder(this) == false)
        {
            //多个角色同时听到语音时，只有顺位最高者立即回应。
            //取消本角色的即时回复（消息已进入上下文，相当于听到了但没抢话），等待Hub按顺位推送发言提示。
            ChatBot.ChatBreakTokenSource.Cancel();
            return;
        }
        ChatRoomHub.BroadcastUserMessage(this, Configuration.UserName, content, directed: isVoice == false);
    }

    static bool IsInSourceList(string? sourceList, string source)
    {
        if (string.IsNullOrWhiteSpace(sourceList))
            return false;
        return sourceList
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(item => item.Equals(source, StringComparison.OrdinalIgnoreCase));
    }

    static readonly Regex SourceRegex = new(@"消息来源:\[(?<src>[^\]]+)\]\s*", RegexOptions.Compiled);
    static readonly Regex TrailingHintRegex = new(@"(?:\n\([^)]*\))+$", RegexOptions.Compiled);
}
