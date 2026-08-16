using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Alife.Foundation;
using Alife.Framework;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;

namespace Alife.Function.ChatRoom;

public readonly record struct TranscriptEntry(DateTime Time, string SpeakerName, string Content);

public sealed class ModeratorDecision
{
    public string Action { get; init; } = "";
    public string? NextSpeaker { get; init; }
    public string? Hint { get; init; }
}

/// <summary>聊天室主持人：借用在场角色的语言模型，决定下一轮谁发言。</summary>
public static class ChatRoomModerator
{
    public const string ActionPassTurn = "pass_turn";
    public const string ActionStaySilent = "stay_silent";
    public const string ActionWrapUp = "wrap_up";

    public static async Task<ModeratorDecision?> DecideAsync(
        ILanguageModel languageModel,
        string guidance,
        IReadOnlyList<TranscriptEntry> transcript,
        string[] candidateNames,
        string userName,
        CancellationToken cancellationToken)
    {
        try
        {
            ChatHistoryAgentThread thread = new();
            thread.ChatHistory.AddUserMessage(BuildPrompt(guidance, transcript, candidateNames, userName));

            Exception? exception = null;
            string response = await languageModel.ChatStreamingAsync(
                thread,
                exceptionThrow: e => exception = e,
                cancellationToken: cancellationToken);

            if (exception != null)
            {
                AlifeLog.LogWarning(exception);
                return null;
            }

            return ParseDecision(response, candidateNames);
        }
        catch (OperationCanceledException)
        {
            AlifeLog.LogWarning("主持人决策已取消或超时。");
            return null;
        }
        catch (Exception e)
        {
            AlifeLog.LogWarning(e);
            return null;
        }
    }

    static string BuildPrompt(
        string guidance,
        IReadOnlyList<TranscriptEntry> transcript,
        string[] candidateNames,
        string userName)
    {
        StringBuilder transcriptBlock = new();
        if (transcript.Count == 0)
        {
            transcriptBlock.Append("（暂无）");
        }
        else
        {
            foreach (TranscriptEntry entry in transcript)
            {
                transcriptBlock.Append(entry.Time.ToString("HH:mm:ss"));
                transcriptBlock.Append(' ');
                transcriptBlock.Append(entry.SpeakerName);
                transcriptBlock.Append('：');
                transcriptBlock.Append(entry.Content);
                transcriptBlock.Append('\n');
            }
        }

        string goal = string.IsNullOrWhiteSpace(guidance) ? "无特定目标，保持对话自然、避免重复。" : guidance.Trim();
        string names = candidateNames.Length == 0 ? "（无人可点名）" : string.Join("、", candidateNames);

        return $$"""
            你是聊天室主持人，负责分配发言权。你自己不出声，只决定谁说话。
            管理员（用户）名字：{{userName}}
            本轮可点名的成员：{{names}}
            引导目标：{{goal}}

            最近对话：
            {{transcriptBlock.ToString().TrimEnd()}}

            只输出一行 JSON，不要输出其他内容：
            {"action":"pass_turn|stay_silent|wrap_up","next_speaker":"成员名或空","hint":"给下一位的一句引导，可空"}

            决策规则：
            - 成员还在对管理员的话做有实质内容的补充、问答或反应时：pass_turn，点最适合的人，hint 写清不要重复别人刚说过的话。
            - 已经在原地打转、互相附和、重复同一句意思：优先 stay_silent。不要再让他们开口。全场安静，等管理员开下一个话题。
            - 打转时大约有一成机会改用 pass_turn，点一个人，hint 必须是一句具体的新话题方向（换角度、换相关小事、向管理员提一个新问题），不要继续旧话。
            - wrap_up 几乎不要用。只有管理员明确在结束、且必须有人把结论说给管理员听时才用。禁止用 wrap_up 来宣布等待或告别。
            - stay_silent 时 next_speaker 留空，hint 留空。不要试图让任何人说「我在旁边待着」「我先安静等你」「那我们先聊到这」之类的话。
            """;
    }

    static ModeratorDecision? ParseDecision(string? response, string[] candidateNames)
    {
        if (string.IsNullOrWhiteSpace(response))
            return null;

        string? json = ExtractFirstJsonObject(response);
        if (json == null)
            return null;

        ModeratorDecisionJson? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ModeratorDecisionJson>(json, JsonOptions);
        }
        catch (JsonException e)
        {
            AlifeLog.LogWarning(e);
            return null;
        }

        if (parsed == null || string.IsNullOrWhiteSpace(parsed.Action))
            return null;

        string action = parsed.Action.Trim();
        if (action.Equals(ActionStaySilent, StringComparison.OrdinalIgnoreCase))
        {
            return new ModeratorDecision {
                Action = ActionStaySilent,
                NextSpeaker = null,
                Hint = parsed.Hint?.Trim(),
            };
        }

        if (action.Equals(ActionPassTurn, StringComparison.OrdinalIgnoreCase) == false &&
            action.Equals(ActionWrapUp, StringComparison.OrdinalIgnoreCase) == false)
            return null;

        string? nextSpeaker = MatchCandidateName(parsed.NextSpeaker, candidateNames);
        if (nextSpeaker == null)
            return null;

        return new ModeratorDecision {
            Action = action.Equals(ActionWrapUp, StringComparison.OrdinalIgnoreCase) ? ActionWrapUp : ActionPassTurn,
            NextSpeaker = nextSpeaker,
            Hint = parsed.Hint?.Trim(),
        };
    }

    static string? MatchCandidateName(string? name, string[] candidateNames)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        string trimmed = name.Trim();
        foreach (string candidate in candidateNames)
        {
            if (candidate.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        return null;
    }

    static string? ExtractFirstJsonObject(string text)
    {
        int start = text.IndexOf('{');
        if (start < 0)
            return null;

        bool inString = false;
        bool escaped = false;
        int depth = 0;
        for (int i = start; i < text.Length; i++)
        {
            char ch = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (ch == '"')
                    inString = false;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }
            if (ch == '{')
                depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                    return text.Substring(start, i - start + 1);
            }
        }
        return null;
    }

    static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNameCaseInsensitive = true,
    };

    sealed class ModeratorDecisionJson
    {
        [JsonPropertyName("action")]
        public string? Action { get; set; }

        [JsonPropertyName("next_speaker")]
        public string? NextSpeaker { get; set; }

        [JsonPropertyName("hint")]
        public string? Hint { get; set; }
    }
}
