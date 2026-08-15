using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Microsoft.Extensions.Logging;

namespace Alife.Function.AutoSpeak;

/// <summary>
/// 启用后：把本轮 AI 的纯文本输出自动包进 &lt;speak&gt;，走现有 Speech / DeskPet 路径。
/// 若模型自己已经写了 &lt;speak&gt;，则不再包一层。
/// </summary>
[Module("自动朗读",
    "启用后，AI 全部对外输出都会自动包进 <speak>，无需模型自己写标签。请同时启用「语音说话」和/或「桌宠交互」。",
    defaultCategory: "Alife 官方/交互方式",
    LaunchOrder = 50)]
public class AutoSpeakService(
    XmlFunctionCaller functionCaller,
    IInteractor<AutoSpeakService> interactor,
    ILogger<AutoSpeakService> logger) :
    ChatBehaviour
{
    bool wrapThisTurn;
    bool decidedThisTurn;

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
            表情与动作仍可用 <expression/>、<motion/> 等标签穿插在正文中。
            """);

        return Task.CompletedTask;
    }

    protected override Task OnStart()
    {
        // MessageFilter 可能比本模块更晚构造；OnStart 时实例已齐。
        DisableSpeakCorrectionRules();
        return Task.CompletedTask;
    }

    protected override Task OnDestroy()
    {
        ChatBot.ChatSent -= OnChatSent;
        ChatBot.ChatReceived -= OnChatReceived;
        ChatBot.ChatOver -= OnChatOver;
        return Task.CompletedTask;
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
    /// MessageFilter 检查的是模型原文（不含我们注入的标签），会误催加 speak；启用本模块时关掉相关规则。
    /// </summary>
    void DisableSpeakCorrectionRules()
    {
        object? filter = ChatActivity.Container.Instances
            .FirstOrDefault(instance => instance.GetType().Name == "MessageFilterService");
        if (filter == null)
            return;

        PropertyInfo? configProperty = filter.GetType().GetProperty("Configuration");
        object? configuration = configProperty?.GetValue(filter);
        if (configuration == null)
            return;

        PropertyInfo? rulesProperty = configuration.GetType().GetProperty("MessageReplyRules");
        if (rulesProperty?.GetValue(configuration) is not IEnumerable rules)
            return;

        int disabled = 0;
        foreach (object? rule in rules)
        {
            if (rule == null)
                continue;

            PropertyInfo? outputRegexProperty = rule.GetType().GetProperty("OutputRegex");
            string? outputRegex = outputRegexProperty?.GetValue(rule) as string;
            if (string.IsNullOrEmpty(outputRegex) ||
                outputRegex.Contains("speak", StringComparison.OrdinalIgnoreCase) == false)
                continue;

            PropertyInfo? enabledProperty = rule.GetType().GetProperty("Enabled");
            if (enabledProperty == null || enabledProperty.PropertyType != typeof(bool))
                continue;

            enabledProperty.SetValue(rule, false);
            disabled++;
        }

        if (disabled > 0)
            logger.LogInformation("自动朗读：已关闭 {Count} 条 MessageFilter 的 speak 纠正规则。", disabled);
    }
}
