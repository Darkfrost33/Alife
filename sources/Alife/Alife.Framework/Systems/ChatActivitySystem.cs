using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Alife.Framework;

public class ChatActivitySystem
{
    /// <summary>角色激活进度更新（taskDescription, progressValue）</summary>
    public event Action<Character>? Activating;
    public event Action<Character, (string Task, float Progress)>? ActivatingProcess;
    public event Action<Character, Exception>? ActivationFailed;
    public event Action<ChatActivity>? ActivatingCreated;
    public event Action<ChatActivity>? Activated;
    public event Action<ChatActivity>? Destroying;
    public event Action<ChatActivity>? Destroyed;

    public IEnumerable<ChatActivity> GetAllChatActivities()
    {
        return activities.Values;
    }

    public bool IsActivated(Character character)
    {
        return activities.ContainsKey(character.Name);
    }

    public ChatActivity? GetChatActivity(Character character)
    {
        return activities.GetValueOrDefault(character.Name);
    }

    /// <summary>
    /// 激活角色。UI 应通过订阅 Activating/Activated/ActivationFailed 事件来感知流程。
    /// </summary>
    public async Task Activate(Character character)
    {
        try
        {
            Progress<(string, float)> progress = new(tuple => {
                ActivatingProcess?.Invoke(character, tuple);
            });

            characterSystem.LoadCharacter(character);

            Activating?.Invoke(character);
            ChatActivity chatActivity = await ChatActivity.Create(
                character, configurationSystem, moduleSystem, progress,
                appendObjects.ToArray()
            );
            ActivatingCreated?.Invoke(chatActivity);
            await chatActivity.Launch(progress);
            activities.Add(character.Name, chatActivity);
            Activated?.Invoke(chatActivity);
        }
        catch (Exception ex)
        {
            ActivationFailed?.Invoke(character, ex);
        }
    }

    /// <summary>
    /// 销毁角色。UI 应通过订阅 Destroying/Destroyed 事件来感知流程。
    /// 重复调用会等待同一次销毁完成（单飞），不会并发触发两次销毁。
    /// </summary>
    public async Task Deactivate(Character character)
    {
        Task? inflight;
        lock (deactivatingTasks)
            inflight = deactivatingTasks.GetValueOrDefault(character.Name);
        if (inflight != null)
        {
            await inflight;
            return;
        }

        if (!activities.TryGetValue(character.Name, out ChatActivity? chatActivity))
            return;

        Task deactivation = DeactivateCore(character.Name, chatActivity);
        lock (deactivatingTasks)
            deactivatingTasks[character.Name] = deactivation;
        try
        {
            await deactivation;
        }
        finally
        {
            lock (deactivatingTasks)
                deactivatingTasks.Remove(character.Name);
        }
    }

    async Task DeactivateCore(string name, ChatActivity chatActivity)
    {
        Destroying?.Invoke(chatActivity);
        await chatActivity.DisposeAsync();
        activities.Remove(name);
        Destroyed?.Invoke(chatActivity);
    }

    /// <summary>
    /// 强制关闭所有角色：先打断所有角色当前的LLM生成与功能执行，再限时等待正常销毁；
    /// 超时仍未完成的活动会被直接移除（残留任务被放弃），保证系统一定能回到全部停止的状态。
    /// </summary>
    public async Task ForceDeactivateAll(TimeSpan? timeout = null)
    {
        TimeSpan waitBudget = timeout ?? TimeSpan.FromSeconds(20);

        //先统一打断，让卡在LLM生成、语音播放上的任务尽快退出，为正常销毁扫清障碍
        foreach (ChatActivity activity in activities.Values.ToArray())
        {
            try
            {
                activity.ChatBot.ChatBreakTokenSource.Cancel();
            }
            catch
            {
                //已销毁或状态异常的活动忽略即可
            }
        }

        foreach ((string name, ChatActivity chatActivity) in activities.ToArray())
        {
            Task? inflight;
            lock (deactivatingTasks)
                inflight = deactivatingTasks.GetValueOrDefault(name);

            //复用已在进行的销毁，否则发起新的销毁
            Task disposal = inflight ?? DisposeSilently(chatActivity);
            if (inflight == null)
                Destroying?.Invoke(chatActivity);

            await Task.WhenAny(disposal, Task.Delay(waitBudget));

            //无论销毁是否完成，都强制注销该活动。未完成的销毁任务被放弃（其异常已被吞掉）
            activities.Remove(name);
            lock (deactivatingTasks)
                deactivatingTasks.Remove(name);
            Destroyed?.Invoke(chatActivity);
        }

        static async Task DisposeSilently(ChatActivity chatActivity)
        {
            try
            {
                await chatActivity.DisposeAsync();
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
            }
        }
    }

    public ChatActivitySystem(
        CharacterSystem characterSystem,
        ConfigurationSystem configurationSystem,
        ModuleSystem moduleSystem,
        StorageSystem storageSystem)
    {
        appendObjects.Add(characterSystem);
        appendObjects.Add(configurationSystem);
        appendObjects.Add(moduleSystem);
        appendObjects.Add(storageSystem);
        appendObjects.Add(this);
        this.characterSystem = characterSystem;
        this.moduleSystem = moduleSystem;
        this.configurationSystem = configurationSystem;
    }

    readonly CharacterSystem characterSystem;
    readonly ModuleSystem moduleSystem;
    readonly ConfigurationSystem configurationSystem;
    readonly List<object> appendObjects = new();
    readonly Dictionary<string, ChatActivity> activities = new();
    readonly Dictionary<string, Task> deactivatingTasks = new();
}
