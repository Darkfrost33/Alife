namespace Alife.Function.DeskPet;

public record DeskPetLayoutServiceConfig
{
    /// <summary>启动时恢复上次保存的位置与大小。</summary>
    public bool RestoreOnStart { get; set; } = true;

    /// <summary>拖动或缩放后自动保存布局。</summary>
    public bool SaveOnChange { get; set; } = true;

    /// <summary>鼠标穿透：开启后点击会穿过桌宠窗口。</summary>
    public bool ClickThrough { get; set; }

    public bool HasSavedLayout { get; set; }

    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; } = 360;
    public double Height { get; set; } = 540;
}
