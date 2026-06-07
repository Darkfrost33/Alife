using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alife.Platform;
using Alife.Framework;
using Alife.Function.FunctionCaller;
using Alife.Function.Interpreter;
using Microsoft.SemanticKernel;

namespace Alife.Function.DeskPet;

[Module("桌宠交互", @"将Live2D桌宠接入AI系统，实现表现力同步和互动反馈（仅支持Cubism 3及以上版本的live2D模型）
可选模型下载地址：
https://github.com/imuncle/live2d",
    defaultCategory: "Alife 官方/交互方式",
    EditorUI = typeof(DeskPetServiceUI))]
public class DeskPetService(XmlFunctionCaller functionService) : InteractiveModule<DeskPetService>, IAsyncDisposable, IConfigurable<DeskPetServiceConfig>
{
    [XmlFunction(FunctionMode.Content)]
    [Description("显示一段气泡文本")]
    public async Task Speak(XmlExecutorContext context, [XmlContent] string content, CancellationToken cancellationToken)
    {
        switch (context.CallMode)
        {
            case CallMode.Closing:
            {
                if (DateTimeOffset.Now.ToUnixTimeMilliseconds() < lastBubbleEndTime)
                    await Task.Delay(TimeSpan.FromMilliseconds(lastBubbleEndTime - DateTimeOffset.Now.ToUnixTimeMilliseconds()));
                client!.HideBubble();
                break;
            }
            case CallMode.Content:
            {
                content = content.Trim();
                if (string.IsNullOrWhiteSpace(content))
                    break;
                if (cancellationToken.IsCancellationRequested)
                    break;

                if (DateTimeOffset.Now.ToUnixTimeMilliseconds() < lastBubbleEndTime)
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(lastBubbleEndTime - DateTimeOffset.Now.ToUnixTimeMilliseconds()));
                client!.ShowBubble(content);
                lastBubbleEndTime = DateTimeOffset.Now.ToUnixTimeMilliseconds() + content.Length * 150;
                break;
            }
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("表演一个表情（具体选项见附加说明）")]
    public void Expression(string option)
    {
        option = option.Trim();
        if (string.IsNullOrWhiteSpace(option))
            return;
        if (client!.SupportedExpressions.Contains(option) == false)
            throw new Exception("选项不存在");

        client!.PlayExpression(option);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("表演一个动作（具体选项见附加说明）")]
    public void Motion(string option)
    {
        option = option.Trim();
        if (string.IsNullOrWhiteSpace(option))
            return;
        if (client!.SupportedMotions.TryGetValue(option, out (string Group, int Index) motion) == false)
            throw new Exception("选项不存在");

        client.PlayMotion(motion.Group, motion.Index);
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("获取当前屏幕位置（使用后需等待结果返回）")]
    public async Task Position()
    {
        try
        {
            (double x, double y) = await client!.GetPositionAsync();
            Poke($"当前位置: x={x}, y={y}");
        }
        catch (TimeoutException)
        {
            Poke("获取坐标超时");
        }
    }

    [XmlFunction(FunctionMode.OneShot)]
    [Description("在屏幕上进行相对移动（注意！该移动方式为相对位置移动，使用前最好先确认当前位置）")]
    public async Task Move(double x = 0, double y = 0, int duration = 1000)
    {
        await client!.MoveAsync(x, y, duration);
        (x, y) = await client!.GetPositionAsync();
        Poke($"移动成功，当前位置: x={x}, y={y}");
    }

    public DeskPetServiceConfig? Configuration { get; set; }

    public event Action<PetLayout>? LayoutChanged;

    public void SetLayout(PetLayout layout) => client!.SetLayout(layout);

    public void SetClickThrough(bool enabled) => client!.SetClickThrough(enabled);

    public Task<PetLayout> GetLayoutAsync() => client!.GetLayoutAsync();

    PetServer? client;
    long lastBubbleEndTime;

    public override async Task AwakeAsync(AwakeContext context)
    {
        await base.AwakeAsync(context);

        // 确保 DeskPet.Client 存在
        string clientPath = Path.Combine(AlifePath.RuntimeFolderPath, "Alife.DeskPet.Client");
        if (!Directory.Exists(clientPath))
        {
            string zipUrl = "https://github.com/BDFFZI/Alife.OfficialPluginStorage/raw/refs/heads/main/Alife.DeskPet.Client/1.0.0.zip";
            await AlifePlatform.DownloadZipFileAsync(clientPath, zipUrl);
        }

        string? modelName = Configuration?.ModelName;
        if (string.IsNullOrWhiteSpace(modelName))
            modelName = "Mao";
        client = new PetServer(clientPath, modelName);
        client.LayoutChanged += layout => LayoutChanged?.Invoke(layout);
        string supportedExpressionsDescription = string.Join(", ", client.SupportedExpressions);
        if (string.IsNullOrEmpty(supportedExpressionsDescription)) supportedExpressionsDescription = $"当前不支持<{nameof(Expression)}>功能";
        string supportedMotionsDescription = string.Join(", ", client.SupportedMotions.Keys);
        if (string.IsNullOrEmpty(supportedMotionsDescription)) supportedMotionsDescription = $"当前不支持<{nameof(Motion)}>功能";

        XmlHandler xmlHandler = new(this) {
            Description = "此服务让你获得一副交互性的Live2D身体。这是你主要的对外输出表情动作等外观信息的工具，需要积极使用。",
            Explanation = $"""
                           ## 支持选项
                           - 支持的 {nameof(Expression)} 选项：{supportedExpressionsDescription}
                           - 支持的 {nameof(Motion)} 选项：{supportedMotionsDescription}

                           ## 其他信息
                           - 当前屏幕分辨率：{AlifePlatform.GetResolution()}
                           """
        };
        functionService.RegisterHandler(xmlHandler, DocumentMode.Explicit);
    }

    public override async Task StartAsync(Kernel kernel, ChatActivity chatActivity)
    {
        await base.StartAsync(kernel, chatActivity);

        await client!.WaitReadyAsync();
        client.OnInput += Chat;
        client.OnInteracted += text => Chat("交互：" + text);

        // 启动状态轮询
        _ = UpdateStatusLoop(chatActivity.ChatBot);
    }

    async Task UpdateStatusLoop(ChatBot chatBot)
    {
        bool lastStatus = false;
        while (!isDisposed)
        {
            try
            {
                bool currentStatus = chatBot.IsChatting;
                if (currentStatus != lastStatus)
                {
                    lastStatus = currentStatus;
                    client?.SendStatus(currentStatus);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }

            await Task.Delay(250);
        }
    }

    public async ValueTask DisposeAsync()
    {
        isDisposed = true;
        if (client != null)
            await client.DisposeAsync();
    }

    bool isDisposed;
}
