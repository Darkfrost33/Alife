using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;

namespace Alife.Function.ChatRoom;

/// <summary>
/// 进程级聊天室总线。
/// 所有角色的模块程序集由 ModuleSystem 统一加载，因此静态成员天然被所有 ChatActivity 共享，
/// 无需额外的跨活动通讯手段。
/// </summary>
public static class ChatRoomHub
{
    public static string[] GetMemberNames()
    {
        lock (Lock)
            return Members.Select(member => member.MemberName).ToArray();
    }

    public static void Join(ChatRoomService member)
    {
        ChatRoomService[] others;
        lock (Lock)
        {
            if (Members.Contains(member))
                return;
            Members.Add(member);
            others = Members.Where(m => m != member).ToArray();
        }

        foreach (ChatRoomService other in others)
            other.ReceiveRoomMessage($"[聊天室] {member.MemberName} 进入了聊天室。（不必刻意打招呼，除非你想）");
    }

    public static void Leave(ChatRoomService member)
    {
        ChatRoomService[] others;
        lock (Lock)
        {
            if (Members.Remove(member) == false)
                return;
            if (ReferenceEquals(currentTurnHolder, member))
                currentTurnHolder = null;
            others = Members.ToArray();
        }

        foreach (ChatRoomService other in others)
            other.ReceiveRoomMessage($"[聊天室] {member.MemberName} 离开了聊天室。");
    }

    /// <summary>房间对该成员而言是否安静（没有其他成员正在回复或说话）。</summary>
    public static bool IsRoomQuietFor(ChatRoomService member)
    {
        lock (Lock)
            return Members.All(m => m == member || m.IsBusyReplying == false);
    }

    /// <summary>
    /// 音频通道对该成员是否放行：没有其他成员正在出声，
    /// 且当前发言权持有者为空、就是该成员，或持有者已经说完。
    /// 成员数 &lt; 2 时恒为 true。
    /// </summary>
    public static bool IsAudioChannelFreeFor(ChatRoomService member)
    {
        ChatRoomService[] others;
        ChatRoomService? holder;
        lock (Lock)
        {
            if (Members.Count < 2)
                return true;
            holder = currentTurnHolder;
            others = Members.Where(m => m != member).ToArray();
        }

        if (others.Any(m => m.IsAudibleNow))
            return false;
        if (holder == member)
            return true;

        //持有者空闲时把通道交给当前请求者，避免 stay_silent / 顺位链结束后卡住其他成员的语音。
        //必须认领而不是清空持有者，否则多个等待者会同时看到通道空闲一起出声。
        if (holder != null && (holder.IsAudibleNow || holder.IsBusyReplying))
            return false;

        lock (Lock)
        {
            if (Members.Count < 2)
                return true;
            ChatRoomService? latest = currentTurnHolder;
            if (latest != null && latest != member && latest != holder)
                return false;
            currentTurnHolder = member;
            return true;
        }
    }

    /// <summary>将音频通道发言权记到该成员（即时回复或交棒投递时调用）。</summary>
    public static void ClaimAudioTurn(ChatRoomService member)
    {
        lock (Lock)
            currentTurnHolder = member;
    }

    static void ReleaseAudioTurn(ChatRoomService? holder)
    {
        if (holder == null)
            return;
        lock (Lock)
        {
            if (ReferenceEquals(currentTurnHolder, holder))
                currentTurnHolder = null;
        }
    }

    /// <summary>
    /// 判断该成员是否为语音发言的首位响应者（当前在场、有听力的成员中顺位最高者）。
    /// 其他带麦成员应放弃即时回应，等待Hub按顺位推送发言提示。
    /// </summary>
    public static bool IsPrimaryVoiceResponder(ChatRoomService member)
    {
        lock (Lock)
        {
            ChatRoomService? primary = SortByRankUnlocked(Members.Where(m => m.CanHearVoice)).FirstOrDefault();
            return primary == null || primary == member;
        }
    }

    /// <summary>
    /// 将用户的发言按响应顺位依次转播给其他成员，用户发言会重置成员间的连续对话计数。
    /// directed 为 true 表示发言有明确对象（如打字输入给某角色），目标已即时回应；
    /// 为 false 表示面向全场的语音，首位带麦成员已即时回应。
    /// 其余成员按顺位延迟收到消息，从而形成一个先后有序的响应队列。
    /// </summary>
    public static void BroadcastUserMessage(ChatRoomService target, string userName, string content, bool directed)
    {
        ChatRoomService[] recipients;
        CancellationToken deliveryToken;
        bool useModerator;
        ChatRoomService? provider;
        CancellationToken moderatorToken = CancellationToken.None;
        lock (Lock)
        {
            aiTalkStreak = 0;
            streakNoticeSent = false;
            lastSpeaker = null;
            wrapUpPending = false;

            //短时间内的相同用户发言只转播一次。
            //防止用户在多个角色上同时启用语音识别时，同一句话被每个角色重复转播。
            DateTime now = DateTime.Now;
            if (content == lastUserContent && (now - lastUserContentTime).TotalSeconds < UserContentDedupeSeconds)
                return;
            lastUserContent = content;
            lastUserContentTime = now;

            //用户有了新发言，还未送达的旧排队消息全部作废，进行中的主持人决策也取消
            pendingDeliveries.Cancel();
            pendingDeliveries = new CancellationTokenSource();
            deliveryToken = pendingDeliveries.Token;
            moderatorToken = RenewModeratorSessionUnlocked();

            AppendTranscriptUnlocked(userName, content);

            recipients = SortByRankUnlocked(Members.Where(m => m != target));
            provider = GetModeratorProviderUnlocked();
            useModerator = Members.Count >= 2 && provider != null;
        }

        if (useModerator == false)
        {
            int slot = 1;
            foreach (ChatRoomService other in recipients)
            {
                string message;
                if (directed)
                {
                    message = $"[聊天室] {userName} 对 {target.MemberName} 说：{content}\n" +
                              "(你也在场并听到了这句话。想插话就正常发言；与你无关的话题保持沉默不回复即可)";
                }
                else if (other.CanHearVoice)
                {
                    //带麦成员已直接听到内容（在其上下文中），只需按顺位提示它现在可以发言
                    message = $"[聊天室] 关于 {userName} 刚才的语音发言，现在轮到你回应了\n" +
                              "(前面的成员可能已经回答过，注意衔接不要重复；与你无关或没有补充时保持沉默即可)";
                }
                else
                {
                    message = $"[聊天室] {userName}（语音，面向全场）说：{content}\n" +
                              "(你也在场并听到了这句话。想插话就正常发言；与你无关的话题保持沉默不回复即可)";
                }

                ScheduleDelivery(other, message, other.ResponseDelaySeconds * slot, deliveryToken);
                slot++;
            }
            return;
        }

        foreach (ChatRoomService other in recipients)
        {
            //带麦成员已从听力模块听到原话，再写入会变成同一句用户发言出现两次
            if (directed == false && other.CanHearVoice)
                continue;
            string silentMessage = directed
                ? $"[聊天室] {userName} 对 {target.MemberName} 说：{content}"
                : $"[聊天室] {userName}（语音，面向全场）说：{content}";
            other.ReceiveRoomContext(silentMessage);
        }

        RunModeratorAndDeliver(target, recipients, provider!, isClosingRound: false, moderatorToken);
    }

    /// <summary>
    /// 按顺位延迟投递消息，延迟期间若用户有新发言则取消（cancellationToken）。
    /// 到点后还会继续等待场上所有其他成员安静下来（回复结束、语音播放完毕）才真正交棒，
    /// 防止上一位角色话说到一半时下一位就开口，导致语音重叠。
    /// </summary>
    static async void ScheduleDelivery(ChatRoomService member, string message, double delaySeconds, CancellationToken cancellationToken)
    {
        try
        {
            if (delaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cancellationToken);

            DateTime waitStart = DateTime.Now;
            while ((DateTime.Now - waitStart).TotalSeconds < MaxTurnWaitSeconds)
            {
                ChatRoomService[] currentMembers;
                lock (Lock)
                    currentMembers = Members.ToArray();
                if (currentMembers.Any(m => m != member && m.IsBusyReplying) == false)
                    break;

                await Task.Delay(500, cancellationToken);
            }

            //收件人可能在排队期间离开了聊天室（角色被关闭），此时不能再投递，
            //否则会在销毁流程中触发新的对话，导致关闭卡住
            lock (Lock)
            {
                if (Members.Contains(member) == false)
                    return;
                currentTurnHolder = member;
            }

            member.MarkPendingReply();
            member.ReceiveRoomMessage(message);
        }
        catch (OperationCanceledException) {}
    }

    static ChatRoomService[] SortByRank(IEnumerable<ChatRoomService> members)
    {
        lock (Lock)
            return SortByRankUnlocked(members);
    }

    static ChatRoomService[] SortByRankUnlocked(IEnumerable<ChatRoomService> members)
    {
        return members.OrderBy(m => m.ResponseOrder).ThenBy(m => Members.IndexOf(m)).ToArray();
    }

    /// <summary>
    /// 成员公开发言。带防无限对话机制：用户长期不发言时，成员间连续对话超过上限后将停止分发。
    /// implicitSpeech 表示这是对 &lt;speak&gt; 等输出的旁听转播（而非主动的&lt;say&gt;），
    /// 没有听众时静默跳过，避免单开场景下的无意义反馈。
    /// </summary>
    public static void BroadcastMemberSay(ChatRoomService speaker, string content, bool implicitSpeech = false)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;

        int maxRounds = speaker.MaxAITalkRounds;
        ChatRoomService[] others;
        bool isClosingRound;
        bool useModerator;
        bool skipAfterWrapUp;
        ChatRoomService? provider;
        CancellationToken moderatorToken = CancellationToken.None;
        lock (Lock)
        {
            others = Members.Where(m => m != speaker).ToArray();
            if (others.Length == 0)
            {
                if (implicitSpeech == false)
                    speaker.ReceiveRoomMessage("聊天室里当前没有其他成员，你的发言没人听到。");
                return;
            }

            //同一成员的连续发言（如一次回复里的多个<speak>子句）只算一轮对话
            if (lastSpeaker != speaker)
            {
                lastSpeaker = speaker;
                aiTalkStreak++;
            }
            if (aiTalkStreak > maxRounds)
            {
                if (streakNoticeSent == false)
                {
                    streakNoticeSent = true;
                    speaker.ReceiveRoomMessage(
                        "（聊天室提示：你们已经连续交谈太久，本话题已被系统静音，你刚才的发言没有送达。请安静等待管理员回来，不要再尝试发言）");
                }
                return;
            }
            isClosingRound = aiTalkStreak == maxRounds;

            AppendTranscriptUnlocked(speaker.MemberName, content);

            provider = GetModeratorProviderUnlocked();
            useModerator = Members.Count >= 2 && provider != null;
            if (useModerator)
                moderatorToken = RenewModeratorSessionUnlocked();
            skipAfterWrapUp = wrapUpPending;
        }

        if (useModerator == false)
        {
            string hint = isClosingRound
                ? "(系统提示：本话题已持续很久，请就此自然收尾，收到本条后不要再回复)"
                : "(想回应正常用<speak>说话即可，在场的人都听得到；与你无关或无话可说时保持沉默，不必每条都回)";

            //成员发言同样按顺位排队送达（等发言者说完再交棒），但不因用户新发言而作废——说出口的话都应该被听到
            int slot = 0;
            foreach (ChatRoomService other in SortByRank(others))
            {
                ScheduleDelivery(other, $"[聊天室] {speaker.MemberName} 说：{content}\n{hint}", other.ResponseDelaySeconds * slot, CancellationToken.None);
                slot++;
            }
            return;
        }

        foreach (ChatRoomService other in others)
            other.ReceiveRoomContext($"[聊天室] {speaker.MemberName} 说：{content}");

        //收尾发言已经开口，内容旁听即可，不再决策，避免 wrap_up 之后 stay_silent 又顺位点下一位
        if (skipAfterWrapUp)
            return;

        RunModeratorAndDeliver(speaker, others, provider!, isClosingRound, moderatorToken);
    }

    static ChatRoomService? GetModeratorProviderUnlocked()
    {
        return Members
            .Where(m => m.ModeratorEnabled)
            .OrderBy(m => m.ResponseOrder)
            .ThenBy(m => Members.IndexOf(m))
            .FirstOrDefault();
    }

    static CancellationToken RenewModeratorSessionUnlocked()
    {
        moderatorSession.Cancel();
        moderatorSession = new CancellationTokenSource();
        return moderatorSession.Token;
    }

    static void AppendTranscriptUnlocked(string speakerName, string content)
    {
        transcript.Add(new TranscriptEntry(DateTime.Now, speakerName, content));
        while (transcript.Count > MaxTranscriptEntries)
            transcript.RemoveAt(0);
    }

    static TranscriptEntry[] GetTranscriptWindowUnlocked(int window)
    {
        if (window <= 0 || transcript.Count == 0)
            return [];
        int start = Math.Max(0, transcript.Count - window);
        int count = transcript.Count - start;
        TranscriptEntry[] copy = new TranscriptEntry[count];
        transcript.CopyTo(start, copy, 0, count);
        return copy;
    }

    static async void RunModeratorAndDeliver(
        ChatRoomService currentSpeaker,
        ChatRoomService[] candidates,
        ChatRoomService provider,
        bool isClosingRound,
        CancellationToken cancellationToken)
    {
        try
        {
            if (candidates.Length == 0)
                return;

            TranscriptEntry[] window;
            string[] candidateNames;
            string guidance;
            string userName;
            float timeoutSeconds;
            float leadSeconds;
            lock (Lock)
            {
                window = GetTranscriptWindowUnlocked(provider.ModeratorTranscriptWindow);
                candidateNames = candidates.Select(m => m.MemberName).ToArray();
                guidance = provider.ModeratorGuidance;
                userName = provider.Configuration.UserName;
                timeoutSeconds = provider.ModeratorTimeoutSeconds;
                leadSeconds = provider.ModeratorLeadSeconds;
            }

            using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeoutSeconds > 0)
                timeoutSource.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            ModeratorDecision? decision = await ChatRoomModerator.DecideAsync(
                provider.ChatBot.LanguageModel,
                guidance,
                window,
                candidateNames,
                userName,
                timeoutSource.Token);

            if (cancellationToken.IsCancellationRequested)
                return;

            if (decision == null)
            {
                FallbackToRankDelivery(candidates, isClosingRound, cancellationToken);
                return;
            }

            if (decision.Action == ChatRoomModerator.ActionStaySilent)
            {
                ReleaseAudioTurn(currentSpeaker);
                //收尾轮仍需要有人开口收束；静场的话回退到顺位提示
                if (isClosingRound)
                    FallbackToRankDelivery(candidates, isClosingRound: true, cancellationToken);
                return;
            }

            ChatRoomService? next = FindMemberByName(decision.NextSpeaker, candidates);
            if (next == null)
            {
                FallbackToRankDelivery(candidates, isClosingRound, cancellationToken);
                return;
            }

            await WaitForSpeechLeadAsync(currentSpeaker, leadSeconds, cancellationToken);

            lock (Lock)
            {
                if (Members.Contains(next) == false)
                    return;
                currentTurnHolder = next;
                if (decision.Action == ChatRoomModerator.ActionWrapUp)
                {
                    lastSpeaker = next;
                    aiTalkStreak = next.MaxAITalkRounds;
                    wrapUpPending = true;
                }
            }

            next.MarkPendingReply();
            next.ReceiveRoomMessage(BuildModeratorPoke(decision, isClosingRound));
        }
        catch (OperationCanceledException) {}
        catch (Exception e)
        {
            AlifeLog.LogWarning(e);
            try
            {
                if (cancellationToken.IsCancellationRequested == false)
                    FallbackToRankDelivery(candidates, isClosingRound, cancellationToken);
            }
            catch (Exception fallbackError)
            {
                AlifeLog.LogWarning(fallbackError);
            }
        }
    }

    static async Task WaitForSpeechLeadAsync(
        ChatRoomService speaker,
        float leadSeconds,
        CancellationToken cancellationToken)
    {
        DateTime waitStart = DateTime.Now;
        while ((DateTime.Now - waitStart).TotalSeconds < MaxTurnWaitSeconds)
        {
            if (speaker.IsBusyReplying == false)
                return;
            double? remaining = speaker.RemainingSpeechSeconds;
            if (remaining != null && remaining.Value <= leadSeconds)
                return;
            await Task.Delay(200, cancellationToken);
        }
    }

    static void FallbackToRankDelivery(ChatRoomService[] candidates, bool isClosingRound, CancellationToken cancellationToken)
    {
        ChatRoomService? next = SortByRank(candidates).FirstOrDefault();
        if (next == null)
            return;

        string message = isClosingRound
            ? "[聊天室] 轮到你回应上面的发言了（系统提示：本话题已持续很久，请就此自然收尾，收到本条后不要再回复）"
            : "[聊天室] 轮到你回应上面的发言了（想说就说，无话保持沉默）";
        ScheduleDelivery(next, message, 0, cancellationToken);
    }

    static ChatRoomService? FindMemberByName(string? name, ChatRoomService[] candidates)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        string trimmed = name.Trim();
        return candidates.FirstOrDefault(m => m.MemberName.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    static string BuildModeratorPoke(ModeratorDecision decision, bool isClosingRound)
    {
        string hint = string.IsNullOrWhiteSpace(decision.Hint) ? "" : decision.Hint.Trim();

        if (decision.Action == ChatRoomModerator.ActionWrapUp)
        {
            return $"[聊天室] 主持人：请把当前话题给管理员一句实质性的收束。{hint}\n" +
                   "(只说结论或回答，不要宣布等待、不要说自己会安静待着，说完后不要再回复)";
        }

        string body = hint.Length == 0
            ? "[聊天室] 主持人：现在轮到你发言了。"
            : $"[聊天室] 主持人：现在轮到你发言了。{hint}";
        string closing = isClosingRound
            ? "(给管理员一句实质性的收束即可；不要宣布等待或说自己会安静待着，说完后不要再回复)"
            : "(直接用<speak>说话即可。若无实质内容可说，不要开口，也不要解释为什么不说)";
        return $"{body}\n{closing}";
    }

    static readonly object Lock = new();
    static readonly List<ChatRoomService> Members = new();
    static readonly List<TranscriptEntry> transcript = new();
    static int aiTalkStreak;
    static bool streakNoticeSent;
    static ChatRoomService? lastSpeaker;
    static ChatRoomService? currentTurnHolder;
    static bool wrapUpPending;

    const double UserContentDedupeSeconds = 10;
    const double MaxTurnWaitSeconds = 60;
    const int MaxTranscriptEntries = 40;
    static string? lastUserContent;
    static DateTime lastUserContentTime;
    static CancellationTokenSource pendingDeliveries = new();
    static CancellationTokenSource moderatorSession = new();
}
