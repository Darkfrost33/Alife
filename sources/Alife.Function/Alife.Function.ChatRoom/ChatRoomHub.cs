using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;

namespace Alife.Function.ChatRoom;

/// <summary>聊天室对话记录条目。</summary>
public readonly record struct TranscriptEntry(DateTime Time, string SpeakerName, string Content);

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

        //持有者空闲时把通道交给当前请求者，避免顺位链结束后卡住其他成员的语音。
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
        lock (Lock)
        {
            aiTalkStreak = 0;
            streakNoticeSent = false;
            lastSpeaker = null;
            currentRoundDeliverable = true;

            //短时间内的相同用户发言只转播一次。
            //防止用户在多个角色上同时启用语音识别时，同一句话被每个角色重复转播。
            DateTime now = DateTime.Now;
            if (content == lastUserContent && (now - lastUserContentTime).TotalSeconds < UserContentDedupeSeconds)
                return;
            lastUserContent = content;
            lastUserContentTime = now;

            //用户有了新发言，还未送达的旧排队消息全部作废
            pendingDeliveries.Cancel();
            pendingDeliveries = new CancellationTokenSource();
            deliveryToken = pendingDeliveries.Token;

            AppendTranscriptUnlocked(userName, content);

            recipients = SortByRankUnlocked(Members.Where(m => m != target));
        }

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
                          "(以上是语音转文字的内容，个别词可能识别有误。你也在场并听到了这句话。想插话就正常发言；与你无关的话题保持沉默不回复即可)";
            }

            ScheduleDelivery(other, message, other.ResponseDelaySeconds * slot, deliveryToken);
            slot++;
        }
    }

    /// <summary>
    /// 成员公开发言。防回音机制（参考QQ群AI插件的意愿衰减做法）：
    /// 1. 接话概率衰减——成员间每多一轮连续对话，下一位收到"轮到你回应"提示的概率乘以衰减系数，
    ///    话题自然消退；没被提示的成员仍会静默听到内容（写入上下文但不触发回复），信息不丢失。
    /// 2. 点名必达——发言中提到某成员名字时，该成员必定收到提示。
    /// 3. 复读检测——发言与最近对话高度相似时判定为车轱辘话，全员只旁听不提示回应，饿死循环。
    /// 4. 硬兜底——连续轮数超过 MaxAITalkRounds 后静音话题，全场只静默旁听，不再产生任何LLM触发。
    /// implicitSpeech 表示这是对 &lt;speak&gt; 等输出的旁听转播（而非主动的&lt;say&gt;），
    /// 没有听众时静默跳过，避免单开场景下的无意义反馈。
    /// </summary>
    public static void BroadcastMemberSay(ChatRoomService speaker, string content, bool implicitSpeech = false)
    {
        if (string.IsNullOrWhiteSpace(content))
            return;

        int maxRounds = speaker.MaxAITalkRounds;
        ChatRoomService[] others;
        bool isClosingRound = false;
        bool muteOverLimit = false;
        bool muteNoticeNeeded = false;
        bool deliverThisRound = false;
        bool isEcho = false;
        lock (Lock)
        {
            others = Members.Where(m => m != speaker).ToArray();
            if (others.Length == 0)
            {
                if (implicitSpeech == false)
                    speaker.ReceiveRoomMessage("聊天室里当前没有其他成员，你的发言没人听到。");
                return;
            }

            //同一成员的连续发言（如一次回复里的多个<speak>子句）只算一轮对话，
            //且共用同一次接话概率判定，避免多段回复反复摇奖。
            if (lastSpeaker != speaker)
            {
                lastSpeaker = speaker;
                aiTalkStreak++;

                //第一轮必达（用户点到的角色说完总得有人接），之后每轮概率乘以衰减系数
                double decayFactor = Math.Clamp(speaker.AITalkDecayFactor, 0, 1);
                double deliverProbability = Math.Pow(decayFactor, aiTalkStreak - 1);
                currentRoundDeliverable = Random.Shared.NextDouble() < deliverProbability;
            }

            if (aiTalkStreak > maxRounds)
            {
                muteOverLimit = true;
                muteNoticeNeeded = streakNoticeSent == false;
                streakNoticeSent = true;
                AppendTranscriptUnlocked(speaker.MemberName, content);
            }
            else
            {
                isClosingRound = aiTalkStreak == maxRounds;
                isEcho = IsEchoOfRecentUnlocked(content);
                AppendTranscriptUnlocked(speaker.MemberName, content);
                deliverThisRound = currentRoundDeliverable;
            }
        }

        if (muteOverLimit)
        {
            //超限后全场只静默入上下文、不再产生任何触发：给任何成员发 Poke 都必定引发一轮新输出，
            //所以静音提示也只写进发言者的历史，等它下次因用户发言等原因开口时自然看到
            foreach (ChatRoomService other in others)
                other.ReceiveRoomContext($"[聊天室] {speaker.MemberName} 说：{content}");
            if (muteNoticeNeeded)
                speaker.ReceiveRoomContext(
                    "（聊天室提示：刚才的话题因成员间连续对话过久已被系统静音，你的发言大家听到了，但之后成员的发言不再互相转达、也不会有人回应，直到管理员重新发言。请保持安静）");
            return;
        }

        if (isEcho)
            AlifeLog.LogInformation($"聊天室：{speaker.MemberName} 的发言与最近对话高度相似，已按车轱辘话处理，只旁听不提示回应。");

        string hint = isClosingRound
            ? "(系统提示：本话题已持续很久，请就此自然收尾，收到本条后不要再回复)"
            : "(想回应正常用<speak>说话即可，在场的人都听得到；回应要有新内容，不要重复或改写别人刚说过的话；与你无关或无话可说时保持沉默，不必每条都回)";

        int slot = 0;
        foreach (ChatRoomService other in SortByRank(others))
        {
            //复读一律只旁听；点到名字的成员必达；其余按本轮衰减概率决定是提示回应还是仅旁听
            bool mentioned = content.Contains(other.MemberName, StringComparison.OrdinalIgnoreCase);
            bool deliver = isEcho == false && (mentioned || deliverThisRound);
            if (deliver == false)
            {
                other.ReceiveRoomContext($"[聊天室] {speaker.MemberName} 说：{content}");
                continue;
            }

            //成员发言按顺位排队送达（等发言者说完再交棒），但不因用户新发言而作废——说出口的话都应该被听到
            ScheduleDelivery(other, $"[聊天室] {speaker.MemberName} 说：{content}\n{hint}", other.ResponseDelaySeconds * slot, CancellationToken.None);
            slot++;
        }
    }

    /// <summary>
    /// 按顺位延迟投递消息，延迟期间若用户有新发言则取消（cancellationToken）。
    /// 到点后还会继续等待场上所有其他成员安静下来才真正交棒，防止上一位话说到一半时下一位就开口。
    /// 语音尾段允许提前交棒：剩余播放时长足够短时就让下一位开始LLM生成，
    /// 实际出声由音频通道门控放行，既压缩衔接空隙又不会叠声。
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

                bool someoneBusy = currentMembers.Any(m =>
                    m != member &&
                    m.IsBusyReplying &&
                    (m.RemainingSpeechSeconds ?? double.MaxValue) > HandoffLeadSeconds);
                if (someoneBusy == false)
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

    static void AppendTranscriptUnlocked(string speakerName, string content)
    {
        transcript.Add(new TranscriptEntry(DateTime.Now, speakerName, content));
        while (transcript.Count > MaxTranscriptEntries)
            transcript.RemoveAt(0);
    }

    /// <summary>判断发言是否在复读最近的对话内容（车轱辘话检测，基于字符二元组相似度，不依赖LLM）。</summary>
    static bool IsEchoOfRecentUnlocked(string content)
    {
        string normalized = NormalizeForEchoCompare(content);
        if (normalized.Length < EchoMinCompareLength)
            return false;

        int compared = 0;
        for (int i = transcript.Count - 1; i >= 0 && compared < EchoCompareWindow; i--, compared++)
        {
            string candidate = NormalizeForEchoCompare(transcript[i].Content);
            if (candidate.Length < EchoMinCompareLength)
                continue;
            if (BigramSimilarity(normalized, candidate) >= EchoSimilarityThreshold)
                return true;
        }
        return false;
    }

    /// <summary>去掉标点、空白并统一小写，只留下承载语义的字符用于相似度比较。</summary>
    static string NormalizeForEchoCompare(string text)
    {
        return new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    /// <summary>字符二元组的 Dice 相似度（0~1）：对中文车轱辘话（换词不换意的复述）也有较好区分度。</summary>
    static double BigramSimilarity(string first, string second)
    {
        HashSet<int> firstBigrams = CollectBigrams(first);
        HashSet<int> secondBigrams = CollectBigrams(second);
        if (firstBigrams.Count == 0 || secondBigrams.Count == 0)
            return 0;
        int common = firstBigrams.Count(secondBigrams.Contains);
        return 2.0 * common / (firstBigrams.Count + secondBigrams.Count);

        static HashSet<int> CollectBigrams(string text)
        {
            HashSet<int> bigrams = new();
            for (int i = 0; i + 1 < text.Length; i++)
                bigrams.Add(text[i] << 16 | text[i + 1]);
            return bigrams;
        }
    }

    static readonly object Lock = new();
    static readonly List<ChatRoomService> Members = new();
    static readonly List<TranscriptEntry> transcript = new();
    static int aiTalkStreak;
    static bool streakNoticeSent;
    static ChatRoomService? lastSpeaker;
    static ChatRoomService? currentTurnHolder;
    static bool currentRoundDeliverable = true;

    const double UserContentDedupeSeconds = 10;
    const double MaxTurnWaitSeconds = 60;
    const int MaxTranscriptEntries = 40;
    const double HandoffLeadSeconds = 2.5;
    const int EchoCompareWindow = 6;
    const int EchoMinCompareLength = 10;
    const double EchoSimilarityThreshold = 0.75;
    static string? lastUserContent;
    static DateTime lastUserContentTime;
    static CancellationTokenSource pendingDeliveries = new();
}
