using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Alife.Function.DeskPet;

/// <summary>
/// 负责跨进程管道的透明收发（双向流收发器）
/// </summary>
public class PetProcess : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public event Action<IpcCommand>? InputReceived;
    public event Action<IpcEvent>? OutputReceived;

    public PetProcess(TextWriter writer, TextReader reader)
    {
        this.writer = writer;
        this.reader = reader;
    }

    public void SendInput(IpcCommand cmd)
    {
        Send(cmd);
    }

    public void SendOutput(IpcEvent ev)
    {
        Send(ev);
    }

    public void ListenInput()
    {
        StartListening(InputReceived);
    }

    public void ListenOutput()
    {
        StartListening(OutputReceived);
    }

    public void Dispose()
    {
        listeningCancellation?.Cancel();
    }

    void Send(object msg)
    {
        string json = JsonSerializer.Serialize(msg, JsonOptions);
        writer.WriteLine(json);
        writer.Flush();
    }

    void StartListening<T>(Action<T>? callback)
    {
        SynchronizationContext? syncContext = SynchronizationContext.Current;

        listeningCancellation = new CancellationTokenSource();
        CancellationToken token = listeningCancellation.Token;

        Task.Run(async () => {
            while (token.IsCancellationRequested == false)
            {
                try
                {
                    string? line = await reader.ReadLineAsync(token);
                    if (string.IsNullOrEmpty(line)) break;

                    T? msg = JsonSerializer.Deserialize<T>(line, JsonOptions);
                    if (msg == null) break;

                    if (syncContext != null)
                        syncContext.Post(_ => callback?.Invoke(msg), null);
                    else
                        _ = Task.Run(() => callback?.Invoke(msg), token);
                    
                    await File.AppendAllTextAsync("pet.log", line + Environment.NewLine, token);
                }
                catch (Exception e)
                {
                    await File.AppendAllTextAsync("pet.log", e + Environment.NewLine, token);
                }
            }
        }, token);
    }

    readonly TextWriter writer;
    readonly TextReader reader;
    CancellationTokenSource? listeningCancellation;
}

// --- IPC Protocol ---
[JsonDerivedType(typeof(WindowMoveCommand), "window-move")]
[JsonDerivedType(typeof(GetPositionCommand), "get-position")]
[JsonDerivedType(typeof(GetLayoutCommand), "get-layout")]
[JsonDerivedType(typeof(SetLayoutCommand), "set-layout")]
[JsonDerivedType(typeof(SetClickThroughCommand), "set-click-through")]
[JsonDerivedType(typeof(BubbleCommand), "bubble")]
[JsonDerivedType(typeof(PlayExpressionCommand), "expression")]
[JsonDerivedType(typeof(MotionCommand), "motion")]
[JsonDerivedType(typeof(HideBubbleCommand), "hide-bubble")]
[JsonDerivedType(typeof(StatusCommand), "status")]
public abstract record IpcCommand;

public record WindowMoveCommand(double X, double Y, int Duration) : IpcCommand;

public record GetPositionCommand : IpcCommand;

public record GetLayoutCommand : IpcCommand;

public record SetLayoutCommand(double Left, double Top, double Width, double Height) : IpcCommand;

public record SetClickThroughCommand(bool Enabled) : IpcCommand;

public record BubbleCommand(string Text) : IpcCommand;

public record PlayExpressionCommand(string? Id) : IpcCommand;

public record MotionCommand(string Group, int Index) : IpcCommand;

public record HideBubbleCommand : IpcCommand;

public record StatusCommand(bool Working) : IpcCommand;

[JsonDerivedType(typeof(ReadyEvent), "ready")]
[JsonDerivedType(typeof(InputEvent), "input")]
[JsonDerivedType(typeof(InteractionEvent), "interaction")]
[JsonDerivedType(typeof(PositionEvent), "position")]
[JsonDerivedType(typeof(LayoutEvent), "layout")]
public abstract record IpcEvent;

public record ReadyEvent : IpcEvent;

public record InputEvent(string Text) : IpcEvent;

public record InteractionEvent(string Interaction) : IpcEvent;

public record PositionEvent(double X, double Y) : IpcEvent;

public record LayoutEvent(double Left, double Top, double Width, double Height) : IpcEvent;

public record PetLayout(double Left, double Top, double Width, double Height);
