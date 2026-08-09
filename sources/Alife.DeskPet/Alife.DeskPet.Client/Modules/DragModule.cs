using System;
using System.Text.Json;

namespace Alife.DeskPet;

public class DragModule : IPetModule
{
    public event Action? DragEnded;
    public bool IsDragging => isDragging;

    readonly PetBridge bridge;
    readonly MainWindow window;
    bool isDragging;
    double lastMouseX, lastMouseY;

    public DragModule(PetBridge bridge, MainWindow window)
    {
        this.bridge = bridge;
        this.window = window;
        bridge.OnMessage += OnBridgeMessage;
    }

    public void Dispose()
    {
        bridge.OnMessage -= OnBridgeMessage;
    }

    void OnBridgeMessage(string type, JsonElement data)
    {
        switch (type)
        {
            case "drag_start":
                isDragging = true;
                break;
            case "drag_end":
                isDragging = false;
                DragEnded?.Invoke();
                break;
        }
    }

    public void OnMouseMove(double windowMouseX, double windowMouseY)
    {
        if (isDragging)
        {
            window.Left += windowMouseX - lastMouseX;
            window.Top += windowMouseY - lastMouseY;
        }
        lastMouseX = windowMouseX;
        lastMouseY = windowMouseY;
    }
}
