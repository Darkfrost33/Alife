# Alife.DeskPet.Client

基于 WPF + WebView2 + pixi-live2d-display 的桌面宠物客户端，支持 Live2D Cubism 3+ 模型。

## 架构

```
[AI Server]  DeskPetService.cs
    |  JSON-line IPC (stdin/stdout)
[Child Process]  Alife.DeskPet.Client.exe
    |  PetActivity.cs        ← 交互调度、手势检测
    |  PetBridge.cs          ← C# ↔ WebView2 消息中继
    |  MotionDetector.cs     ← 摇晃/快速移动/鼠标转圈识别
    |
[WebView2]  pet.js → pixi-live2d-display → Live2D 模型
```

## 模型配置

模型文件放在 `wwwroot/model/{模型名}/`，配置写在 `{模型名}.model3.json` 中。

模型自带的字段（`FileReferences`、`Textures`、`Physics` 等）由 pixi-live2d-display 解析，无需手动配置。**我们只关心以下三个需要自定义的部分。**

### 1. HitAreas — 碰撞区域

决定点击模型哪个部位触发什么交互。

```json
"HitAreas": [
  { "Id": "HitAreaHead", "Name": "Head" },
  { "Id": "HitAreaBody", "Name": "Body" }
]
```

- `Id`：必须匹配模型实际的 drawable ID。启动后打开 F12 Console 查看 `Drawable IDs` 列表确认
- `Name`：必须包含 `"Head"` 或 `"Body"`（不区分大小写），否则点击无法触发交互

### 2. Motions → Idle 组 — 待机动作

pixi-live2d-display 加载模型后会**自动随机轮播** `Idle` 组内所有动作。

```json
"Motions": {
  "Idle": [
    { "Name": "待机", "File": "motions/idle.motion3.json" }
  ]
}
```

- **只放循环动画**。非循环动画（如 login、wedding）放进去会反复播放
- 多个动作 = 随机切换，更自然

### 3. Interaction — 交互池（DeskPet 扩展）

在 `model3.json` 顶层添加 `"Interaction"` 字段，每种交互类型配一个池，触发时随机选一个。

```json
"Interaction": {
  "startup": [
    { "text": "主人回来啦！", "exp": "", "mtn": { "group": "Idle", "index": 0 } }
  ],
  "head": [
    { "text": "别揉了~", "exp": "委屈", "mtn": { "group": "TapHead", "index": 0 } }
  ],
  "body": [
    { "text": "不要乱碰！", "exp": "害羞", "mtn": { "group": "TapBody", "index": 0 } }
  ]
}
```

**交互类型：**

| 类型 | 触发条件 |
|------|---------|
| `head` | 点击头部 |
| `body` | 点击身体 |
| `startup` | 模型加载完成 |
| `window_shake` | 窗口被剧烈摇晃 |
| `window_move` | 窗口被快速直线拖动 |
| `mouse_shake` | 鼠标在桌宠周围快速转圈 |
| `mouse_combo` | 2.5 秒内连续点击 3+ 次 |

**InteractionItem 字段：**

| 字段 | 说明 |
|------|------|
| `text` | 气泡文字，留空不显示 |
| `exp` | 表情名，必须匹配 `Expressions[].Name`，留空不切换 |
| `mtn` | `{ "group": "组名", "index": 0 }`，引用 Motions 中的动作 |

## 注意事项

- **HitAreas.Id** 不是固定写法，必须与模型实际 drawable ID 匹配（不同模型不同）
- **HitAreas.Name** 必须包含 `Head` 或 `Body`，代码硬编码检查
- **Mtn.group** 必须匹配 `Motions` 中的组名，**index** 是组内数组索引（从 0 开始）
- **exp** 必须匹配 `Expressions[].Name`，不匹配不会播放
- **C# 用 FORCE 优先级触发动作**，会立即打断 Idle，播完后自动恢复 Idle
- **Expression 3 秒后自动重置**，Bubble 6 秒后自动隐藏

## 添加自定义模型

1. 在 `wwwroot/model/` 下创建文件夹，放入模型文件
2. 编写 `{模型名}.model3.json`，配置 HitAreas、Idle、Interaction
3. 启动后 F12 Console 确认 `HitArea definitions` 中 index 不为 -1
4. 在 `DeskPetServiceConfig.ModelName` 中填入模型文件夹名

## IPC 协议

**C# → JS：** `load` / `expression` / `motion` / `bubble` / `hide-bubble` / `look` / `status`

**JS → C#：** `ready` / `loaded` / `poke` / `input` / `drag_start` / `drag_end` / `resize_delta`

**Host ↔ Client（stdin/stdout JSON-line）：** `window-move` / `get-position` / `get-layout` / `set-layout` / `set-click-through` / `bubble` / `expression` / `motion` / `hide-bubble` / `status` ↔ `ready` / `input` / `interaction` / `position` / `layout`
