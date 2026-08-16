using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.MessageFilter;
using Alife.Function.Speech;
using Microsoft.Extensions.Logging;

namespace Alife.Function.AutoSpeak;

/// <summary>
/// 启用后：把本轮 AI 的纯文本输出自动包进 &lt;speak&gt;，走现有 Speech / DeskPet 路径。
/// 若模型自己已经写了 &lt;speak&gt;，则不再包一层。
/// 括号 () / （） 内的内容不会送入语音合成（气泡仍可显示）。
/// </summary>
[Module("自动朗读",
    "启用后，AI 全部对外输出都会自动包进 <speak>，无需模型自己写标签。括号内文字不朗读。请同时启用「语音说话」和/或「桌宠交互」。",
    defaultCategory: "Alife 官方/交互方式",
    LaunchOrder = 50)]
public class AutoSpeakService(
    XmlFunctionCaller functionCaller,
    Interactor<AutoSpeakService> interactor,
    ILogger<AutoSpeakService> logger) :
    ChatBehaviour
{
    bool wrapThisTurn;
    bool decidedThisTurn;
    List<MessageReplyRule>? disabledSpeakRules;
    SpeechService? speechService;
    bool restoredSpeechFilter;

    protected override Task OnAwake()
    {
        // 在 FunctionCaller.OnStart 订阅之前挂上，保证先注入开标签再喂正文。
        ChatBot.ChatSent += OnChatSent;
        ChatBot.ChatReceived += OnChatReceived;
        ChatBot.ChatOver += OnChatOver;

        interactor.Prompt(
            """
            【自动朗读已启用】系统会把你的对外回复自动包进 <speak> 并朗读/显示气泡。
            请直接输出要对用户说的正文，不要自己写 <speak> 或 </speak>。
            括号 () / （） 里的内容不会被朗读，可用来写表情说明或旁白。
            表情与动作仍可用 <expression/>、<motion/> 等标签穿插在正文中。
            """);

        return Task.CompletedTask;
    }

    protected override Task OnStart()
    {
        // MessageFilter / Speech 可能比本模块更晚构造；OnStart 时实例已齐。
        DisableSpeakCorrectionRules();
        EnableParentheticalSpeechFilter();
        return Task.CompletedTask;
    }

    protected override Task OnDestroy()
    {
        ChatBot.ChatSent -= OnChatSent;
        ChatBot.ChatReceived -= OnChatReceived;
        ChatBot.ChatOver -= OnChatOver;
        RestoreSpeakCorrectionRules();
        RestoreParentheticalSpeechFilter();
        return Task.CompletedTask;
    }

    void EnableParentheticalSpeechFilter()
    {
        speechService = ChatActivity.Container.Instances.OfType<SpeechService>().FirstOrDefault();
        if (speechService == null)
            return;

        speechService.OmitParentheticalText = true;
        restoredSpeechFilter = false;
    }

    void RestoreParentheticalSpeechFilter()
    {
        if (restoredSpeechFilter || speechService == null)
            return;

        speechService.OmitParentheticalText = false;
        restoredSpeechFilter = true;
        speechService = null;
    }

    void OnChatSent(string _)
    {
        wrapThisTurn = false;
        decidedThisTurn = false;
    }

    void OnChatReceived(string chunk)
    {
        if (decidedThisTurn)
            return;

        decidedThisTurn = true;

        if (LooksLikeSpeakStart(chunk))
        {
            wrapThisTurn = false;
            return;
        }

        if (!HasSpeakHandler())
        {
            logger.LogWarning("自动朗读已启用，但当前角色未注册 <speak> 处理（请启用「语音说话」或「桌宠交互」）。");
            wrapThisTurn = false;
            return;
        }

        wrapThisTurn = true;
        functionCaller.Feed("<speak>");
    }

    void OnChatOver()
    {
        if (!wrapThisTurn)
            return;

        wrapThisTurn = false;
        functionCaller.Feed("</speak>");
    }

    bool HasSpeakHandler()
    {
        var handlers = functionCaller.HandlerTable.GetHandlersOfFunction("speak");
        return handlers is { Count: > 0 };
    }

    static bool LooksLikeSpeakStart(string text)
    {
        ReadOnlySpan<char> span = text.AsSpan().TrimStart();
        return span.StartsWith("<speak", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// MessageFilter 检查的是模型原文（不含我们注入的标签），会误催加 speak；启用本模块时从生效列表里拿掉相关规则。
    /// </summary>
    void DisableSpeakCorrectionRules()
    {
        MessageFilterService? filter = ChatActivity.Container.Instances
            .OfType<MessageFilterService>()
            .FirstOrDefault();
        if (filter == null)
            return;

        disabledSpeakRules = filter.RemoveMessageReplyRules(IsSpeakCorrectionRule);
        if (disabledSpeakRules.Count > 0)
            logger.LogInformation("自动朗读：已关闭 {Count} 条 MessageFilter 的 speak 纠正规则。", disabledSpeakRules.Count);
    }

    void RestoreSpeakCorrectionRules()
    {
        if (disabledSpeakRules is not { Count: > 0 })
            return;

        MessageFilterService? filter = ChatActivity.Container.Instances
            .OfType<MessageFilterService>()
            .FirstOrDefault();
        if (filter != null)
        {
            foreach (MessageReplyRule rule in disabledSpeakRules)
                filter.AddMessageReplyRule(rule);
        }

        disabledSpeakRules = null;
    }

    static bool IsSpeakCorrectionRule(MessageReplyRule rule)
    {
        if (rule.Name.Contains("speak", StringComparison.OrdinalIgnoreCase) ||
            rule.Name.Contains("DeskPet", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            return rule.CorrectionMessage().Contains("speak", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
