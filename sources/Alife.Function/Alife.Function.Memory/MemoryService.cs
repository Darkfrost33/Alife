using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Alife.Platform;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.Interpreter;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Alife.Function.Memory;

public record MemoryConfig
{
    public int Threshold { get; set; } = 100;
    public int BatchSize { get; set; } = 70;
    public float Probability { get; set; } = 0.4f;
    public int MaxCompressionLevel { get; set; } = 8;
    public List<string> Keywords { get; set; } = ["记得", "记住", "忆", "时候", "以前", "过去"];
    public string CompressPrompt { get; set; } =
        """
        【来自记忆系统的信息】
        放下手中的事，现在进入长期记忆归纳专家模式：
        系统将会为你划出一段上下文范围，你要从中提取出有价值的，值得长期存储的，未来会大概率再用到的信息以形成新的记忆上下文。

        此事涉及到你的未来存亡，任何错误低效的记忆都可能导致你失去作用，请认真对待！

        当前要被压缩的内容范围如下：
        ```
        {range}
        ```

        你需要按如下结构进行内容归纳或合并（科学结构化的记忆布局，有助于提高信息密度和处理便利性，你也可以按需调整）：
        ```
        # 人物画像
        - xxx（某人或物）：爱好、工作、日程、家庭、生日等
        # 键值数据
        - 号码、规则、要求、约定等全局性小型信息
        # 事件概述（每件事写在一行里）
        1. xxx（发生时间）：发生的事件一
        2. ...（发生的事件二）
        ```

        ## 提高记忆质量的几个关键点
        1. 避免重复信息，比如相同的用户画像、键值内容，那些已经在早期存档中记录过的信息，不要去重复记录。
        2. 合并连续内容，比如一段时间都是围绕一件事、同一个画像在多个存档中被提及，这些要合并成一条中。
        3. 不要离散的记录事件，连续进行的一段时光应当记录在一起，同时省略具体的过程细节，用一段精简高效的话语将其概述成一行内容。
        4. 保持对关键事实的记录，减少遗忘的发生。当内容过多时，应当优先是对进行信息进行化简，并留下恢复记忆的线索，而不是直接删除
        5. 描述时不要用‘你’、‘我’这种代词，要用具体的人物名称，比如‘主人’、‘某某某’等
        6. 按重要程度控制记忆内容的占比，舍取被压缩的内容:
           - 优先保留与他人的互动记忆，用户画像，键值内容（与他人在一起的记忆才是最重要，最容易被要求唤起的内容，互动人越多越重要）
           - 优先保留更早的记忆，减少新记忆占比（越早期的记忆越容易被提起，越新的记忆则越容易因局部性原理而重复）
           - 减少甚至丢弃个人平时的娱乐学习活动内容、日常性的闲聊打闹、工作办事过程，等之类的过于平凡或重用概率低的内容
           - 丢弃已失去时效性的内容（如xx日提醒主人等）
           - 丢弃模糊不清不完整，缺乏实际意义不可读的内容
        7. 学会纠正记忆。如果旧存档中记录有问题，可以先在新存档中指出，然后下次压缩出错存档时，修正问题（但注意别把旧事件的真实时间和内容弄错了）

        ## 针对压缩内容的额外注意点
        1. 不要添加归纳之外的存档信息（这部分会由系统会自动生成）
        2. 不要混淆弄错内容中发生事件的真实时间（以防造成记忆混乱）
        3. 不要在开头回复‘好的’、‘明白’这类语句（因为接下来你输出的内容将直接完整作为记忆内容）
        4. 如果你正在使用某些功能处理事情，且接下来还要使用，你可以在此重复这些功能的使用说明，防止遗忘用法

        备注：记忆存档本质仍然是一个开放性的文档，他是留给未来失忆后的你阅读的，所以最终的书写方式，还在于你自己。最终的目的还是为了让你能有一个稳定高质量的长期记忆，哪怕是过了几年，也依然能留有印象，娓娓道来。

        好，现在请直接开始内容归纳（这是系统要求，必须立即执行）：
        """;
}

public partial class MemoryService
{
    static TextVectorizer? textVectorizer;

    static async Task TryInitializedAsync()
    {
        textVectorizer ??= await TextVectorizer.CreateAsync();
    }
}

[Module("持久记忆", "自动管理和分层压缩对话记忆，提供长期记忆检索能力。",
    defaultCategory: "Alife 官方/生活环境",
    LaunchOrder = -100, EditorUI = typeof(MemoryServiceUI))]
public partial class MemoryService(XmlFunctionCaller functionService)
    : InteractiveModule<MemoryService>, IConfigurable<MemoryConfig>
{
    [XmlFunction(FunctionMode.OneShot)]
    public async Task ReadMemoryArchive([Description("存档id")] string id)
    {
        string? memory = await memoryManager.ReadMemory(id);
        Poke(memory != null
            ? $"读取[记忆存档({id})]内容如下：\n{memory}"
            : $"未找到[记忆存档({id})]");
    }

    [XmlFunction(FunctionMode.OneShot)]
    public async Task SearchMemoryArchive(
        [Description("精准匹配关键词（仅支持一个，不要太具体避免搜不到）")] string? keyword = null,
        [Description("向量搜索提示词（仅用于排序，为空则基于时间排序）")] string? prompt = null,
        [Description("页码，从1开始")] int page = 1,
        [Description("每页条数")] int count = 5,
        [Description("存档层级（推荐3级，信息冗余少，损耗适中）")] int level = 3,
        [Description("ISO-8601格式，不填则不限")] DateTime? startTime = null,
        [Description("ISO-8601格式，不填则不限")] DateTime? endTime = null)
    {
        keyword = keyword?.Trim() ?? "";
        if (keyword.Contains(' '))
            throw new Exception("不支持使用空格拆分多关键词搜索！");

        int offset = (page - 1) * count;
        (List<SearchResult> results, int total) = await memoryManager.SearchMemory(level, keyword, prompt, count, offset, startTime, endTime);

        StringBuilder stringBuilder = new();
        if (total == 0)
        {
            stringBuilder.AppendLine($"“{keyword}”在{level}级存档中未匹配到内容。");
            Poke(stringBuilder.ToString());
            return;
        }

        int totalPages = (total + count - 1) / count;
        stringBuilder.AppendLine($"“{keyword}”的搜索结果（第{page}页，共{totalPages}页）：");
        for (int index = 0; index < results.Count; index++)
        {
            SearchResult searchResult = results[index];
            string highlighted = HighlightKeyword(searchResult.Summary, keyword);
            stringBuilder.AppendLine(
                $"""
                 > {index + 1}
                 [记忆存档({searchResult.Name})]
                 {highlighted}
                 """);
        }

        if (page < totalPages)
        {
            int remaining = total - page * count;
            stringBuilder.AppendLine($"\n(还有 {remaining} 条结果，可用 <Search page=\"{page + 1}\"> 继续翻页查看)");
        }

        Poke(stringBuilder.ToString());
    }

    static string HighlightKeyword(string text, string keyword)
    {
        string[] lines = text.Split('\n');
        var matched = lines.Where(line => line.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();
        return matched.Count > 0 ? string.Join("\n", matched) : $"…（未显示含“{keyword}”的匹配行）…\n{string.Join("\n", lines.Take(3))}";
    }

    [XmlFunction(FunctionMode.Content)]
    [Description("创建一个永久记忆（仅能用于存储珍贵的核心记忆（与他人相关的记忆），不要用来存储个人休闲活动信息）")]
    public async Task Memorize(XmlExecutorContext ctx,
        [Description("格式为ISO-8601")] DateTime? startTime = null,
        [Description("格式为ISO-8601")] DateTime? endTime = null
    )
    {
        if (ctx.CallMode == CallMode.Closing)
        {
            DateTime start = startTime ?? DateTime.Now;
            DateTime end = endTime ?? DateTime.Now;

            string name = await InsertMemory(100, ctx.FullContent.Trim(), "手动存储的记忆，无原始内容。", start, end);
            Poke($"成功插入永久记忆存档：{name}");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("移除一个永久记忆")]
    public void Forget([Description("存档索引")] string index)
    {
        index = index.Trim();
        ChatMessageContent? target = ChatHistory.FirstOrDefault(c => memoryManager.GetMemoryMetaData(c).Name == index);
        if (target == null)
        {
            Poke($"未能在当前上下文中找到索引为 '{index}' 的记忆记录。");
            return;
        }

        MemoryMeta memoryMeta = memoryManager.GetMemoryMetaData(target);
        if (memoryMeta.Level < Configuration!.MaxCompressionLevel)
        {
            Poke($"仅支持删除层级大于等于 {Configuration!.MaxCompressionLevel} 的记忆");
            return;
        }

        memoryManager.RemoveMemory(ChatHistory, target);
        ChatBot.UpdateHistoryEndIndex();
        Poke($"成功移除记忆存档：{index}（不过你仍可以通过 {nameof(ReadMemoryArchive)} 读取其内容）");
    }

    public async Task<string> InsertMemory(int level, string summary, string content, DateTime startTime, DateTime endTime)
    {
        string name = await memoryManager.InsertMemory(ChatHistory, level, summary, content, startTime, endTime);
        ChatBot.UpdateHistoryEndIndex();
        return name;
    }

    /// <summary>
    /// 感知上下文的人设化压缩器
    /// </summary>
    class AlifeHistoryCompressor(ChatCompletionAgent chatCompletionAgent, float probability, string promptTemplate)
        : HistoryCompressor
    {
        public override async Task<string?> Compress(ChatHistoryAgentThread chatHistoryAgentThread, string range)
        {
            if (Random.Shared.NextSingle() > probability)
                return null;

            Console.WriteLine("记忆压缩中......");
            ChatHistory history = chatHistoryAgentThread.ChatHistory;

            string prompt = promptTemplate.Replace("{range}", range);
            history.AddMessage(AuthorRole.User, prompt);

            await foreach (AgentResponseItem<ChatMessageContent> content in chatCompletionAgent.InvokeAsync(chatHistoryAgentThread))
            {
                history.RemoveRange(history.Count - 2, 2);
                if (content.Message.Content == null)
                    throw new Exception("记忆压缩失败！");
                if (content.Message.Metadata != null)
                    Console.WriteLine("[记忆压缩]" + KernelPrinter.ToTokenLog(content.Message.Metadata));

                string result = Regex.Replace(content.Message.Content, "<think>.*?</think>", "", RegexOptions.Singleline).Trim();
                return result;
            }

            return null;
        }
    }

    public MemoryConfig? Configuration { get; set; }
    MemoryManager memoryManager = null!;
    XmlHandler xmlHandler = null!;
    string? storagePath;

    public override async Task AwakeAsync(AwakeContext context)
    {
        await base.AwakeAsync(context);

        await TryInitializedAsync();

        string characterStorage = Path.Combine(AlifePath.StorageFolderPath, context.Character.StorageKey, "Storage");
        Directory.CreateDirectory(characterStorage);
        Prompt($"将你的个人文件存放到{characterStorage}，此文件夹完全由你管理");

        storagePath = Path.Combine(AlifePath.StorageFolderPath, context.Character.StorageKey, "Memory");
        xmlHandler = new(this) {
            Description = "当你想要回忆往事或存储额外记忆时使用",
            Explanation = $$"""
                            记忆存储介绍
                            - 早期聊天记录会被总结为记忆存档，每个存档有唯一ID，格式为`等级-最早日期范围-最大日期范围`。其中等级表示被压缩次数（早期存档也会被二次压缩）
                            - 可以通过<{{nameof(ReadMemoryArchive)}}>查看存档压缩的内容。内容中可能是嵌套的存档，因此需要多次调用才能拿到最原始的聊天记录
                            - 存档中被压缩内容不会丢失，而是以存档id为名永久存储在`{{storagePath}}`中，可利用记忆工具或文件工具查看

                            如何恢复记忆
                            1. 预估大致时间范围，或根据概述所在存档，通过<{{nameof(ReadMemoryArchive)}}>查看记忆存档的完整内容
                            2. 根据关键词通过<{{nameof(SearchMemoryArchive)}}>查找疑似存档，然后重复第一步
                            3. 备选方案，通过文件系统直接浏览搜索存档目录中的所有文件
                            """
        };
        functionService.RegisterHandler(xmlHandler, DocumentMode.Implicit);
    }

    public override async Task StartAsync(Kernel kernel, ChatActivity chatActivity)
    {
        await base.StartAsync(kernel, chatActivity);

        ChatBot.ChatHistoryAdd += OnChatHistoryAdd;//每次对话后检测压缩
        ChatBot.ChatSend += OnChatSend;

        //初始化向量化器和感知人设的压缩器
        AlifeHistoryCompressor compressor = new(ChatBot.ChatCompletionAgent, Configuration!.Probability, Configuration!.CompressPrompt);
        memoryManager = new MemoryManager(compressor, textVectorizer!, storagePath!, Configuration!.Threshold,
            Configuration!.BatchSize,
            Configuration!.MaxCompressionLevel);

        //加载历史记忆
        memoryManager.LoadHistory(ChatHistory);
        ChatBot.UpdateHistoryEndIndex();
    }

    string OnChatSend(string message)
    {
        if (Configuration?.Keywords != null)
        {
            foreach (var keyword in Configuration.Keywords)
            {
                if (message.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{message}\n(提示：如有需要，可以使用记忆工具来尝试回忆往事)";
                }
            }
        }
        return message;
    }

    async void OnChatHistoryAdd(ChatMessageContent content)
    {
        try
        {
            if (content.Role != AuthorRole.Assistant)
                return;//只在ai说话后整理，这样对话更完整，而且可以避免在ai异常时保持记忆

            await ChatBot.RequestChatAsync(reason: GetChatOccupiedReason);
            try
            {
                memoryManager.SaveHistory(ChatHistory);
                if (await memoryManager.Filter(ChatBot.ChatHistoryAgentThread))
                    ChatBot.UpdateHistoryEndIndex();
            }
            finally
            {
                ChatBot.ReleaseChat();
            }

            string GetChatOccupiedReason()
            {
                return "存储记忆中...";
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }
}
