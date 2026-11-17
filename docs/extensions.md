# 扩展模块与素材包开发

[开发文档](development.md) · [返回首页](../README.md)

Moongate 0.1.0 支持两种第三方内容：

| 形式 | 用途 | 安装方式 |
|---|---|---|
| 素材包 | 替换模块图标、动效、页面背景、效果／乐器／清单图片 | “模块与扩展”页导入文件夹，或放在工具根的 `desktop\assets` |
| 纯数据扩展模块 | 以 ZIP 分发的图片、文字与数据，可携带一个素材包 | “模块与扩展”页安装 `.zip`，或放进 `data\extensions\inbox` |

带程序的扩展在独立进程里运行，经宿主开放的接口读取数据、提供页面与浮窗元件，见第 3 节。

## 1. 素材包

素材包是一个目录，根目录放 `pack.json`，图片按相对路径放在目录内：

```json
{
  "name": "我的素材包",
  "icons": {
    "module.music-library": { "image": "music.png", "hover": "spin", "selected": "pulse" },
    "module.color": { "hover": "shake" }
  },
  "backgrounds": { "combat-observation": { "image": "bg/combat.webp" } },
  "effects": { "1201": { "image": "effects/1201.png" } },
  "instruments": { "lute": { "image": "instruments/lute.png" } },
  "playlists": { "favorites": { "image": "lists/favorites.png" } }
}
```

| 字段 | 键 | 说明 |
|---|---|---|
| `icons` | 图标槽名 | `image` 可选；`hover`、`selected` 为动效预设。只写动效时保留内建图标 |
| `backgrounds` | 模块标识 | 页面背景，单张不超过 8 MB |
| `effects` | 效果编号 | 效果图标，单张不超过 2 MB |
| `instruments` | 乐器标识 | 乐器图片，单张不超过 2 MB |
| `playlists` | 清单标识 | 清单封面，单张不超过 2 MB |

- **图标槽名**：模块入口 `module.<模块标识>`，面板标题 `panel.<页面>.<面板>`，外壳入口 `shell.<名称>`。
- **动效预设**：`none`、`pop`、`bounce`、`swing`、`spin`、`pulse`、`float`、`shake`。
- **图片格式**：PNG、JPEG、WebP、BMP；图标不超过 2 MB，铺满色块并保留圆角。
- 图片不在包目录内、格式不支持、文件缺失或过大的条目被跳过，其余条目照常生效，界面列出被跳过的“字段.键：原因”。移除素材包即回到内建外观。

调试时可以用 `--assets <目录>` 启动桌面前端直接载入某个素材包，载入顺序为 `--assets`、界面里保存的选择、工具根的 `desktop\assets`。

## 2. 纯数据扩展模块

### 2.1 包结构

扩展模块是一个 ZIP，根目录放清单 `extension.json`，其余文件按相对路径放在包内：

```text
example-pack.zip
  extension.json
  assets/pack.json
  assets/icons/fishing.png
  data/notes.md
```

```json
{
  "schemaVersion": 1,
  "id": "example.fishing-skin",
  "version": "1.0.0",
  "hostApi": { "minimum": "1.0", "maximumExclusive": "2.0" },
  "displayNames": { "zh-Hans": "钓鱼主题", "zh-Hant": "釣魚主題" },
  "descriptions": { "zh-Hans": "钓鱼主题的图标与背景" },
  "provides": [],
  "permissions": [],
  "dependencies": [],
  "highRiskBehaviors": [],
  "assetPack": "assets/pack.json"
}
```

### 2.2 清单规则

清单按严格 JSON 解析，重复字段、未知字段和类型错误都会被拒绝。

| 字段 | 规则 |
|---|---|
| `schemaVersion` | 固定为 1 |
| `id` | `<发布者>.<名称>`，两段都以小写字母开头，最多 32 个小写字母、数字或连字符 |
| `version` | 三段数字，如 `1.2.0` |
| `hostApi` | 必须包含当前宿主 API 1.0 |
| `displayNames` | 至少一种语言（`zh-Hans`、`zh-Hant`），最多 64 字 |
| `descriptions` | 可选，每种语言最多 200 字 |
| `provides`、`permissions`、`highRiskBehaviors` | 纯数据扩展必须为空数组；需要程序的扩展另须声明 `runtime`（第 3 节），否则以 `extension.codeNotSupported` 拒绝 |
| `dependencies` | 最多 16 项，`{ "id", "minimumVersion" }` |
| `assetPack` | 可选，包内一个素材包 `pack.json` 的相对路径 |
| `signature` | 可选，`{ "algorithm", "keyId", "value" }`；当前版本只记录为“已附签名，未验证” |

### 2.3 文件与大小限制

| 项目 | 上限 |
|---|---|
| ZIP 文件 | 32 MiB |
| 解压后总量 | 64 MiB |
| 条目数 | 1,000 |
| 清单 | 64 KiB |

允许的文件类型：`.json`、`.txt`、`.md`、`.csv`、`.png`、`.jpg`、`.jpeg`、`.webp`、`.bmp`。含可执行内容（PE 头，或 `.exe`、`.dll`、`.ps1`、`.bat`、`.cmd`、`.js`、`.py` 等）的包会被拒绝。宿主不会执行包内任何内容。

### 2.4 安装、升级与卸载

1. 用户选择 ZIP 后，宿主先在暂存区检查，返回十五分钟有效的预览：标识、版本、发布者、文件数、展开大小、内容树哈希、签名状态与动作（安装／升级／重新安装）。
2. 用户确认后安装到数据根的 `extensions/<标识>/<版本>/`，安装记录写在 `extensions/<标识>/state.json`。新装的扩展默认停用。
3. 版本更高为升级并沿用原启用状态；同版本同内容为重新安装；同版本不同内容（`extension.versionConflict`）与降级（`extension.downgradeRejected`）被拒绝，需先卸载。
4. 携带素材包的扩展启用后，“扩展”卡片上出现“用作素材包”；扩展停用或卸载时界面回到内建外观。

### 2.5 本机接口

前端与脚本可以通过本机 API 管理扩展（需要 `modules.configure` 权限，读取需要 `system.read`）：

| 接口 | 行为 |
|---|---|
| `GET /api/v1/extensions` | 已安装扩展、状态与待安装包 |
| `POST /api/v1/extensions/previews` | 正文 `uploadId` 或 `inboxName`，返回安装预览 |
| `POST /api/v1/extensions/previews/{id}/install` | 按预览安装 |
| `POST /api/v1/extensions/{id}/enabled` | 正文 `enabled`，启用或停用 |
| `POST /api/v1/extensions/{id}/uninstall` | 卸载 |

本机 API 的认证与客户端 SDK 见[开发文档](development.md#6-接口契约与客户端)。

### 2.6 拒绝原因码

| 原因码 | 含义 |
|---|---|
| `extension.limitExceeded` | 超过大小或数量上限 |
| `extension.packageInvalid` | ZIP 结构损坏 |
| `extension.pathRejected` | 包内路径越界或不合法 |
| `extension.manifestMissing`、`extension.manifestInvalid` | 缺少清单或清单不合规则 |
| `extension.permissionUnknown` | 声明了未登记的权限 |
| `extension.hostApiIncompatible` | 宿主 API 版本不在范围内 |
| `extension.codeNotSupported` | 包含程序或声明了需要程序的字段，但没有声明运行入口 `runtime` |
| `extension.fileTypeRejected` | 文件类型不在允许列表 |
| `extension.versionConflict`、`extension.downgradeRejected` | 同版本内容不同或版本更低 |
| `extension.installDamaged` | 已安装内容缺失或与记录不符 |
| `extension.previewExpired` | 预览已过期，需要重新检查 || `extension.permissionUnavailable` | 申请了不向扩展开放的权限（读取游戏进程内存） |

## 3. 带程序的扩展

带程序的扩展在独立进程里运行，只能经工具开放的接口做事：读取数据、提供页面与浮窗元件、保存自己的数据；会操作游戏或访问外部的能力属于高风险权限，安装后默认关闭，由用户在“模块与扩展”页逐项打开。扩展程序以当前用户身份运行，工具约束的是它经工具所做的事与它的资源用量，请只安装信任来源的扩展。

### 3.1 运行入口

清单增加 `runtime`：

```json
"runtime": { "protocol": 1, "kind": "dotnet", "entry": "KillTimer.dll", "arguments": [] }
```

| `kind` | 启动方式 |
|---|---|
| `executable` | 运行包内的 `.exe`（`entry`） |
| `script` | 用包内的解释器 `interpreter`（`.exe`）运行包内脚本 `entry`；解释器须随包提供，例如嵌入式 Python |
| `dotnet` | 用工具所用的 .NET 运行时运行包内的 `.dll`（需随包带 `.runtimeconfig.json`） |

工具不在 PATH 或其他系统位置查找程序。进程的工作目录是扩展的安装目录；环境变量只有系统目录、`TEMP`／`TMP`（扩展自己的临时目录）与 `MOONGATE_EXTENSION_ID`、`MOONGATE_EXTENSION_VERSION`、`MOONGATE_EXTENSION_DATA`（数据目录）、`MOONGATE_EXTENSION_PROTOCOL`。资源上限：内存 256 MiB、CPU 25%、最多 4 个进程；扩展不能使用其他程序的窗口句柄和剪贴板。

### 3.2 协议

标准输入输出上的 UTF-8 行分隔 JSON-RPC 2.0，每行一条消息，不超过 1 MiB；标准错误的每一行进入扩展日志。

1. 启动后 10 秒内发送握手请求：`{"jsonrpc":"2.0","id":1,"method":"hello","params":{"protocolVersion":1,"extensionId":"example.kill-timer","version":"1.0.0"}}`。结果含 `grants`（已授予的权限）、`settings`（设置值）与 `limits`。
2. 调用宿主方法发请求，宿主回 `result` 或 `error`；`error.data.reason` 是稳定原因码。可以发 `{"method":"$/cancel","params":{"id":…}}` 取消自己的调用。
3. 宿主发来的请求：`ping`（5 秒内回应空结果，连续两次不回应视为挂死）、`shutdown`（回应后退出，3 秒后强制结束）。
4. 宿主发来的通知：`event`（订阅的事件：`type`、`resourceId`、`data`）、`events/resync`、`ui/event`、`settings/changed`、`grants/changed`、`shortcut/triggered`。
5. 写日志：通知 `{"method":"log","params":{"level":"info","message":"…"}}`。

限制：每秒最多 200 条消息（可短暂突发到 400），同时进行的调用最多 16 个。超速、超长或格式错误时扩展被结束且不自动重启；异常退出、挂死或超内存后按 1、5、15 秒重启，十分钟内超过 3 次即停止。

### 3.3 权限目录

| 权限 | 风险 | 方法与事件 |
|---|---|---|
| `combat.read` | 普通 | `combat.snapshot`；事件 `combat.summary.changed`、`combat.effects.changed`、`combat.encounter.closed`、`combat.observation.degraded`、`combat.measurement.changed` |
| `inventory.read` | 普通 | `inventory.accounts`、`inventory.items`（参数 `query` 与本机 API 的库存查询相同）；事件 `inventory.snapshot.changed`、`inventory.observation.degraded` |
| `game.read` | 普通 | `game.binding`、`game.observation`；事件 `game.observation.changed`、`game.environment.changed` |
| `rooms.read` | 普通 | `rooms.state` |
| `music.read` | 普通 | `music.query`（参数 `query` 与曲库查询相同） |
| `ui.page`、`ui.overlay` | 普通 | `ui.page.set`／`remove`、`ui.overlay.set`／`remove`、`ui.state.set`（第 3.4 节） |
| `ui.settings` | 普通 | `settings.get`；清单 `settings` 声明的设置项出现在设置页 |
| `ui.activity` | 普通 | `ui.activity.set`（`text` 最多 16 字、`tone`；空文字清除）：没有模块持有游戏输入时，活动胶囊显示“扩展 名称：状态” |
| `ui.shortcut` | 普通 | 清单 `commands`（`id`、`labels`）声明的命令列在设置页快捷键区，由用户分配组合键（F7–F10 与 Pause 保留给内建动作）；扩展运行且已授权时按下收到 `shortcut/triggered`（`command`、`actionId`） |
| `storage.local` | 普通 | `storage.get`／`set`／`delete`／`list`，`storage.file.read`／`write`／`delete`（Base64）；合计 16 MiB |
| `input.selected-window` | 高风险 | `input.acquire`／`send`／`release`：操作游戏（第 3.5 节） |
| `capture.selected-window` | 高风险 | `capture.game`：截取绑定的游戏窗口 |
| `network.fetch` | 高风险 | `network.fetch`：只访问清单 `network.hosts` 列出的主机（HTTPS） |
| `files.read-selected` | 高风险 | `files.pick`：读取用户在文件选择框里选定的文件 |

读取游戏进程内存不向扩展开放。确认安装即授予普通权限；升级时新增的权限在预览里标出，确认升级即授予新增的普通权限。用户可以随时撤销任何一项，撤销立即生效，扩展收到 `grants/changed`。

### 3.4 界面描述

扩展没有自己的界面程序，页面与浮窗元件用声明式描述交给 Moongate，由工具用自己的控件呈现。`ui.page.set`／`ui.overlay.set` 的参数：`id`（小写字母开头，最多 32 字）、`titles`（`zh-Hans`／`zh-Hant`）、`schemaVersion`（1）、`root`（节点树），浮窗元件另有 `size`（`small`、`medium`、`large`）。每个节点是带 `type` 的对象，可带 `id`（需要交互时必填）与 `children`：

| 类型 | 属性 |
|---|---|
| `column`、`row` | `children`，`gap`（0–48） |
| `card` | `title`，`children` |
| `text` | `text` 或 `bind`，`style`（`title`、`body`、`muted`、`caption`） |
| `value` | `label`，`text` 或 `bind`，`unit` |
| `table` | `columns`（`key`、`title`），`bind` 指向对象数组 |
| `list` | `bind` 指向数组 |
| `button` | `id`，`text`；按下时扩展收到 `ui/event`（`kind` 为 `click`） |
| `toggle`、`select`、`input` | `id`，`text`／`options`（`value`、`text`）／`placeholder`，`bind`；修改时收到 `kind` 为 `change` 的 `ui/event` |
| `progress` | `bind`（0–1），`label` |
| `icon`、`spacer`、`divider` | 简单装饰 |

文字可以写字符串，或写 `{"zh-Hans": "…", "zh-Hant": "…"}`。`bind` 读取 `ui.state.set` 推送的值（`{"values": {"current": "1:23"}}`，值为 null 时删除）；缺失时显示“未读取”。不认识的节点显示为一行“不支持的界面元素”。上限：描述 128 KiB、24 层、400 个节点；每个扩展 8 个页面、8 个浮窗元件；状态 128 个键、64 KiB。扩展停止后它的页面与元件撤下，浮窗版面里已摆放的位置会保留。

清单的 `settings` 声明设置项（`key`、`type`：`toggle`／`number`／`text`／`choice`、`labels`、`default`，数字可带 `minimum`、`maximum`，选择须带 `options`），它们出现在设置页“扩展”卡片里；修改后扩展收到 `settings/changed`。

### 3.5 操作游戏（高风险）

授予“向游戏发送输入”后：

1. `input.acquire`：`{"mode": "background", "seconds": 60}`。`background` 为后台方式：不把游戏窗口带到前台，也不移动你的鼠标；`foreground` 为前台键鼠，要求游戏窗口在前台。取得租约期间，染色、合奏等其他活动不能同时操作同一个游戏。窗口最小化或系统不支持时返回原因码（如 `input.windowMinimized`、`input.backgroundUnsupported`），不会改用前台方式。
2. `input.send`：`{"actions": [...], "intervalMs": 120, "jitterMs": 30, "jitterPx": 2}`。动作：`{"kind":"tap","at":{"x":100,"y":200}}`、`longPress`（`at`、`holdMs` 100–5000）、`drag`（`from`、`to`、`steps` 1–12）、`swipe`（`from`、`to`）、`key`（`vk` 虚拟键码：字母、数字、F1–F12、回车、Esc、空格、退格、Tab、方向键；`holdMs`）。坐标是游戏窗口客户区内的物理像素。每次最多 32 个动作、计划总时长 15 秒；间隔抖动最多 500 毫秒、坐标抖动最多 8 像素且不越出客户区。
3. `input.release` 交还租约。紧急停止（默认 F9）、撤销权限、停用或扩展退出都会立即收回租约并松开按下的按键与触点。

后台输入同样是自动操作游戏，可能不符合游戏服务条款，账号风险由使用者自行承担。

### 3.6 截图、网络与文件（高风险）

- `capture.game`：`{"maxWidth": 640}`，返回 `width`、`height`、`format`（`rgb24`）与 Base64 的 `data`；每秒最多两次，窗口最小化时返回 `input.windowMinimized`。
- `network.fetch`：`{"url": "https://api.example.com/x", "method": "GET"}`，只接受清单 `network.hosts` 列出的主机（HTTPS、默认端口），不跟随重定向、不带 Cookie；请求体 64 KiB、响应 512 KiB（超出为 `extension.resultTooLarge`）、超时 15 秒（`extension.networkTimeout`），连接失败为 `extension.networkFailed`。返回 `status`、`contentType` 与 `text` 或 `base64`。
- `files.pick`：`{"title": "选择存档"}`。工具弹出文件选择框，标题写明是哪个扩展在请求；用户选定后返回 `name`、`size`、`sha256` 与 Base64 的 `content`（最多 512 KiB，只交一次，扩展拿不到本机路径），取消时返回 `{"cancelled": true}`。没有打开的桌面窗口为 `extension.noDesktop`，同时只能有一个请求、两次至少间隔 5 秒（否则 `resource.capacityExceeded`），两分钟未选为 `extension.fileRequestExpired`。

### 3.7 打包与本地调试

1. 打包：把清单 `extension.json`、程序与它需要的文件放进一个目录，压缩成 ZIP（清单在根目录）。.NET 扩展用 `dotnet publish -c Release -o <目录>` 输出后把 `extension.json` 放进去，不需要带运行时。
2. 本地调试：在“模块与扩展”页按“开启开发模式”，再按“加载开发目录”选择含 `extension.json` 的目录（例如示例扩展的构建输出目录）。工具直接从该目录运行，不复制文件；改完代码重新构建后在扩展卡片上按“重启”。日志在卡片的“日志”里查看，标准错误的每一行也会进入日志。开发模式关闭时开发中的扩展随之停止；卸载开发扩展不会删除你的目录。
3. 示例：仓库的 `frontends/examples/extensions/kill-timer` 是一个击杀计时器：订阅战斗事件，提供一个页面、一个浮窗元件和两个设置项，只用普通权限。构建 `dotnet build frontends/examples/extensions/kill-timer -c Release` 后，以开发模式加载输出目录（`artifacts/build/bin/Moongate.Examples.KillTimer/release`）即可。
