using System;
using System.Threading.Tasks;
using Alife.Framework;

namespace Alife.Function.DeskPet;

[Module("桌宠布局", "保存并恢复 Live2D 桌宠窗口位置、大小与鼠标穿透设置。需同时启用「桌宠交互」模块。",
    defaultCategory: "Alife 官方/交互方式",
    LaunchOrder = 10,
    EditorUI = typeof(DeskPetLayoutServiceUI))]
public class DeskPetLayoutService(
    ConfigurationSystem configurationSystem,
    DeskPetService deskPetService) :
    ChatBehaviour,
    IConfigurable<DeskPetLayoutServiceConfig>
{
    public DeskPetLayoutServiceConfig Configuration { get; set; } = null!;

    public event Action? LayoutConfigChanged;

    protected override Task OnStart()
    {
        deskPetService.LayoutChanged += OnLayoutChanged;
        ApplySavedSettings();
        return Task.CompletedTask;
    }

    protected override Task OnDestroy()
    {
        deskPetService.LayoutChanged -= OnLayoutChanged;
        return Task.CompletedTask;
    }

    public void ApplySavedSettings()
    {
        if (Configuration.RestoreOnStart && Configuration.HasSavedLayout)
        {
            deskPetService.SetLayout(new PetLayout(
                Configuration.Left,
                Configuration.Top,
                Configuration.Width,
                Configuration.Height));
        }

        deskPetService.SetClickThrough(Configuration.ClickThrough);
    }

    public async Task SaveCurrentLayoutAsync()
    {
        PetLayout layout = await deskPetService.GetLayoutAsync();
        UpdateConfiguration(layout);
        PersistConfiguration();
    }

    public void SetClickThrough(bool enabled)
    {
        Configuration.ClickThrough = enabled;
        deskPetService.SetClickThrough(enabled);
        PersistConfiguration();
    }

    void OnLayoutChanged(PetLayout layout)
    {
        if (Configuration.SaveOnChange == false)
            return;

        UpdateConfiguration(layout);
        PersistConfiguration();
    }

    void UpdateConfiguration(PetLayout layout)
    {
        Configuration.Left = layout.Left;
        Configuration.Top = layout.Top;
        Configuration.Width = layout.Width;
        Configuration.Height = layout.Height;
        Configuration.HasSavedLayout = true;
    }

    void PersistConfiguration()
    {
        configurationSystem.SetConfiguration(GetType(), Configuration!, Character.StorageKey);
        LayoutConfigChanged?.Invoke();
    }
}
