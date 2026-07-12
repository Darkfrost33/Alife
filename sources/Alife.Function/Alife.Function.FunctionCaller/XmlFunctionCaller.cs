using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alife.Framework;
using Alife.Function.Interpreter;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;

namespace Alife.Function.FunctionCaller;

public class XmlFunctionCallerConfig
{
    [Description("触发子句分隔的字符标记。调整子句会对字幕、语音生成等流式输出的功能产生影响")]
    public List<string> Separators { get; set; } = ["，", "。", "！", "？", "......", "~", "…"];

    [Description("触发子句分隔的最短文本长度（字符数）")]
    public int MinBreakingLength { get; set; } = 23;
}

public enum DocumentMode
{
    Not,
    Implicit,
    Explicit,
}

[Module("Xml函数执行器", "提供一种Xml函数调用框架，可以将注册其中的函数，暴露给AI，并指导其用Xml标签调用。",
    defaultCategory: "Alife 官方/功能底座",
    launchOrder: -1000)]
public class XmlFunctionCaller(ILogger<XmlFunctionCaller> logger) : InteractiveModule<XmlFunctionCaller>, IConfigurable<XmlFunctionCallerConfig>
{
    public XmlFunctionCallerConfig? Configuration { get; set; }
    public bool IsIdle => executor.IsInactive;

    /// <summary>
    /// 当前系统中的函数调用注册信息。
    /// XmlHandlerTable支持你禁用其中的部分函数，从而实现拦截或手动调用的需求
    /// </summary>
    public XmlHandlerTable HandlerTable => handlerTable;

    public string GetExplicitDocumentTag(string handlerName)
    {
        return $"[显式文档({handlerName})]";
    }

    /// <summary>
    /// 可选显式或隐射注册一个xml执行器
    /// 显式：初始注入所有信息
    /// 隐射：默认只注入描述并以类名构造一个函数，ai调用该函数后再显示所有内容
    /// </summary>
    public void RegisterHandler(XmlHandler handler, DocumentMode documentMode)
    {
        handlerTable.Register(handler);
        switch (documentMode)
        {
            case DocumentMode.Not:
                break;
            case DocumentMode.Implicit:
                if (handler.Name == null)
                    throw new Exception("不支持没有名称的隐式 XmlHandler");
                implicitHandlers.Add(handler);
                AddImplicitTrigger(handler);
                break;
            case DocumentMode.Explicit:
                explicitHandlers.Add(handler);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(documentMode), documentMode, null);
        }
    }
    /// <summary>
    /// 仅注册函数，不注入任何提示词
    /// </summary>
    /// <param name="handler"></param>
    /// <param name="plainAreas"></param>
    public void RegisterHandlerWithoutDocument(XmlHandler handler, params string[] plainAreas)
    {
        handlerTable.Register(handler);
        this.plainAreas.AddRange(plainAreas);
    }
    /// <summary>
    /// 显式注册一个xml执行器
    /// </summary>
    /// <param name="handler"></param>
    /// <param name="plainAreas"></param>
    public void RegisterHandler(XmlHandler handler, params string[] plainAreas)
    {
        RegisterHandler(handler, DocumentMode.Explicit);
        this.plainAreas.AddRange(plainAreas);
    }
    /// <summary>
    /// 将一个对象转换为xml执行器，然后显式注册
    /// </summary>
    /// <param name="handler"></param>
    /// <param name="plainAreas"></param>
    public void RegisterHandler(object handler, params string[] plainAreas)
    {
        RegisterHandler(new XmlHandler(handler), plainAreas);
    }
    public void UnregisterHandler(XmlHandler handler)
    {
        handlerTable.Unregister(handler);
    }
    public void UnregisterHandler(object handler)
    {
        UnregisterHandler(new XmlHandler(handler));
    }

    /// <summary>
    /// 标记指定的xml标签内容均为生文本，不要参与xml解析，这意味着ai不需要考虑这些xml标签内容的通配问题。
    /// xml函数中被标记为[XmlForm]的参数会自动注册为plainArea
    /// </summary>
    /// <param name="plainAreas"></param>
    public void AddPlainAreas(params string[] plainAreas)
    {
        this.plainAreas.AddRange(plainAreas);
    }

    readonly XmlHandlerTable handlerTable = new();
    readonly List<string> plainAreas = new();
    XmlStreamParser parser = null!;
    XmlStreamExecutor executor = null!;
    readonly List<XmlHandler> explicitHandlers = new();
    readonly List<XmlHandler> implicitHandlers = new();
    string currentProcess = "";
    readonly List<ChatMessageContent> chatHistoryBuffer = new();

    string GetExplicitDocument(XmlHandler handler)
    {
        return $"""
                {GetExplicitDocumentTag(handler.Name)}
                ### {handler.Name}
                {handler.Description} 
                #### 提供函数
                {handler.FunctionDocument()}
                {(string.IsNullOrEmpty(handler.Explanation) ? "" : $"#### 详细说明\n```\n{handler.Explanation}\n```\n")}
                """;
    }
    string GetImplicitDocument(XmlHandler handler)
    {
        return $"""
                - <{handler.Name}/> : {handler.Description}
                """;
    }
    void AddImplicitTrigger(XmlHandler source)
    {
        XmlHandler xmlHandler = new() {
            Name = source.Name + "_Trigger"
        };
        xmlHandler.Functions.Add(new XmlFunction() {
            Name = source.Name.ToLower(),
            Invoker = (_, _) => {
                Poke(GetExplicitDocument(source));
                return Task.CompletedTask;
            }
        });
        handlerTable.Register(xmlHandler);
    }

    public override async Task StartAsync(Kernel kernel, ChatActivity chatActivity)
    {
        await base.StartAsync(kernel, chatActivity);

        IEnumerable<XmlParameter> parameters = handlerTable.GetAllHandlers().SelectMany(handler => handler.Functions.SelectMany(function => function.Parameters));
        plainAreas.AddRange(parameters.Where(parameter => parameter.IsXmlForm).Select(parameter => parameter.Name));

        //创建xml解析执行器等
        parser = new XmlStreamParser(plainAreas.Distinct());
        executor = new XmlStreamExecutor(
            parser,
            handlerTable,
            Configuration!.Separators.ToArray(),
            minBreakingLength: Configuration.MinBreakingLength
        );
        parser.Error += OnError;
        executor.Error += OnError;
        executor.Handling += OnHandling;
        chatActivity.ChatBot.ChatReceived += OnChatReceived;
        chatActivity.ChatBot.ChatSent += OnChatSent;

        //预计算参数

        //注入函数文档
        Prompt($"""
                默认情况下你仅支持输出普通文本，但由于各种插件功能的存在，使得你还拥有通过输出特定的xml标签执行功能调用的能力。

                ## 使用提示
                1. 由于xml的解释器的存在，【" | & | < | >】之类的xml符号都无法直接输出，你需要使用xml转义的方式【&quot; | &amp; | &lt; | &gt;】来输出尖括号。
                2. xml调用方式非常自由，允许你进行嵌套，或一次使用多条。
                3. 很多xml函数拥有调用后返回结果的功能，因此你可以通过多轮对话解决事情（如先调用一下获取手册，然后等到收到结果后，再决定下一步的操作）

                ## 使用示例
                当你的函数足够丰富后，你可以尝试用如下的方式使用他们，这是官方最佳示例（注意，示例中的函数不一定存在）：
                ```
                (可选，未被标签包裹的文字，用户看不到，所以可以在此实现空消息、自言自语、思考等动作)
                <speak> <!-- 默认采用语音方式对外输出，并在文本中穿插表情动作，来实现动态的交互效果 -->
                主人你看我画的好不好看，<expression option="开心" />今天特意给你画的噢！<motion option="摆摆手"/>
                看你每天那么累，给你打打气。
                </speak>
                <python> <!-- 因为python执行需要时间，在结尾调用比较合适。 -->
                show('cheer.png')
                <python>
                ```   

                ## 原始字符串区域
                被如下标签包括的内容可以不用转义，他们会自动保持原始格式
                {string.Join(',', parser.PlainAreas)}

                ## 当前可用功能

                {string.Join("\n", explicitHandlers.Select(GetExplicitDocument))}
                """ + (implicitHandlers.Count == 0
            ? ""
            : $"""
               ## 隐式功能
               有些功能是渐进式加载的，你需要显式阅读他们文档，来学习如何使用。读取隐式服务的文档非常简单，直接输出xml来调用如下标签即可：

               {string.Join("\n", implicitHandlers.Select(GetImplicitDocument))}

               上面这些标签都是开启隐式服务的入口，你要根据实际情况，积极的去调用他们，有很多你需要的功能可能就藏在其中。
               """));
    }

    public override async Task DestroyAsync()
    {
        //限时等待，防止卡死的处理器（如挂起的外部服务调用）让角色永远无法关闭
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        try
        {
            await executor.WaitToInactive(timeout.Token);
        }
        catch (OperationCanceledException) {}
        await executor.DisposeAsync();

        await base.DestroyAsync();
    }

    async void OnChatSent(string _)
    {
        try
        {
            await ChatBot.RequestChatAsync(reason: GetChatOccupiedReason);
            try
            {
                executor.Flush();
                await executor.WaitToInactive(ChatBot.ChatBreakTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                await executor.CancelAsync();
            }
            finally
            {
                ChatBot.ReleaseChat();
            }
        }
        catch (OperationCanceledException) {}
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }
    void OnChatReceived(string obj)
    {
        executor.Feed(obj);
    }
    void OnError(string tag, Exception exception)
    {
        Poke($"执行{tag}标签出错：{exception.Message}");
        logger.LogWarning(exception, $"执行{tag}标签出错");
    }
    void OnHandling(string name, XmlContext context)
    {
        currentProcess = $"执行{name}函数中...";

        if (context.CallMode == CallMode.Opening || context.CallMode == CallMode.OneShot)
        {
            //实现当ai调用隐射函数时自动注入对应的隐式文档
            IReadOnlyList<XmlHandler>? handlers = handlerTable.GetHandlersOfFunction(name);
            if (handlers != null)
            {
                var dependentImplicitHandlers = handlers.Intersect(implicitHandlers).ToArray();
                if (dependentImplicitHandlers.Length != 0)
                {
                    chatHistoryBuffer.Clear();
                    chatHistoryBuffer.AddRange(ChatHistory);

                    foreach (XmlHandler xmlHandler in dependentImplicitHandlers)
                    {
                        string explicitDocumentTag = GetExplicitDocumentTag(xmlHandler.Name);

                        if (chatHistoryBuffer.All(content => !content.Content?.Contains(explicitDocumentTag) ?? false))
                            Poke(GetExplicitDocument(xmlHandler));
                    }
                }
            }
        }
    }
    string GetChatOccupiedReason()
    {
        return currentProcess;
    }
}
