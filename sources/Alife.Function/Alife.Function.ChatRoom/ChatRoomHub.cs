using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
    /// 判断该成员是否为语音发言的首位响应者（当前在场、有听力的成员中顺位最高者）。
    /// 其他带麦成员应放弃即时回应，等待Hub按顺位推送发言提示。
    /// </summary>
    public static bool IsPrimaryVoiceResponder(ChatRoomService member)
    {
        lock (Lock)
        {
            ChatRoomService? primary = SortByRank(Members.Where(m => m.CanHearVoice)).FirstOrDefault();
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

            recipients = SortByRank(Members.Where(m => m != target)).ToArray();
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
                          "(你也在场并听到了这句话。想插话就正常发言；与你无关的话题保持沉默不回复即可)";
            }

            ScheduleDelivery(other, message, other.ResponseDelaySeconds * slot, deliveryToken);
            slot++;
        }
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
                }

                member.MarkPendingReply();
                member.ReceiveRoomMessage(message);
        }
        catch (OperationCanceledException) {}
    }

    static IEnumerable<ChatRoomService> SortByRank(IEnumerable<ChatRoomService> members)
    {
        lock (Lock)
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
        }

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
    }

    static readonly object Lock = new();
    static readonly List<ChatRoomService> Members = new();
    static int aiTalkStreak;
    static bool streakNoticeSent;
    static ChatRoomService? lastSpeaker;

    const double UserContentDedupeSeconds = 10;
    const double MaxTurnWaitSeconds = 60;
    static string? lastUserContent;
    static DateTime lastUserContentTime;
    static CancellationTokenSource pendingDeliveries = new();
}
