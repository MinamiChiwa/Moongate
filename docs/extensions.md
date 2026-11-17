# 扩展模块开发

[开发文档](development.md) · [素材包制作](asset-packs.md) · [返回首页](../README.md)

本文面向想为 Moongate 制作扩展模块的开发者：从两类扩展的选择、清单写法、与工具通信的协议，到权限、界面、调试和打包，照着做可以从零做出一个能安装运行的扩展。

## 1. 两类扩展怎么选

| | 纯数据扩展 | 带程序的扩展 |
|---|---|---|
| 能做什么 | 分发素材包（图标、背景、效果图标等）与文字、数据文件 | 读取战斗、背包、游戏绑定、合奏、曲库数据；提供页面、浮窗元件、设置项、快捷键命令与活动栏状态；保存自己的数据；经授权后截图、联网、读取用户选的文件、操作游戏 |
| 清单 | 不写 `runtime`，`permissions` 等声明为空 | 必须写 `runtime`（运行入口） |
| 包内文件 | 只能是 `.json`、`.txt`、`.md`、`.csv`、`.png`、`.jpg`、`.jpeg`、`.webp`、`.bmp` | 不限类型，程序与它需要的运行库都随包提供 |
| 运行方式 | 工具不执行包内任何内容 | 工具启动一个独立进程，经标准输入输出与它通信 |

只想换外观时做纯数据扩展：先按[素材包制作](asset-packs.md)做好素材包，再按[素材包制作第 5 节](asset-packs.md#5-随扩展分发)打成扩展 ZIP。需要读数据、显示自己的内容或做操作时做带程序的扩展，下面各节都针对它。

## 2. 五分钟跑起示例

仓库的 `frontends/examples/extensions/kill-timer` 是一个击杀计时器：订阅战斗事件，显示当前战斗用时与最近几次击杀用时，提供一个页面、一个浮窗元件和两个设置项，只用普通权限。

1. 构建示例（需要 .NET SDK，见[开发文档](development.md#3-环境要求)）：

   ```powershell
   dotnet build frontends/examples/extensions/kill-timer -c Release
   ```

   输出目录 `artifacts/build/bin/Moongate.Examples.KillTimer/release` 里有 `extension.json` 与 `Moongate.Examples.KillTimer.dll`。

2. 打开 Moongate 的“模块与扩展”页，在“扩展”卡片上按“开启开发模式”，再按“加载开发目录”，选择上面的输出目录。
3. 扩展卡片显示“运行中（进程号）”后，侧栏“扩展”分组出现“击杀计时器”，浮窗版面页的元件库里出现同名元件，设置页“扩展”卡片里出现两个设置项。
4. 打几只怪，页面上的用时与击杀记录随之更新。改代码后重新构建，在扩展卡片上按“重启”即生效；按“日志”查看扩展输出。

第 4.3 节逐段讲解这个示例。

## 3. 扩展包结构与清单

### 3.1 目录结构

```text
my-extension/
  extension.json        清单，必须在根目录
  MyExtension.dll       运行入口（按 runtime.kind 可以是 .exe、.dll 或脚本）
  MyExtension.runtimeconfig.json
  assets/pack.json      可选：随扩展分发的素材包
  assets/icons/...
```

安装后，扩展的程序在工具数据目录的 `extensions/<标识>/<版本>/`（解压版为 `userdata\data\extensions\`），自己的数据在 `extensions/<标识>/data/`，日志在 `extensions/<标识>/logs/`，临时文件在 `extensions/<标识>/temp/`。

### 3.2 清单字段

清单 `extension.json` 按严格 JSON 解析：重复字段、未知字段、类型错误都会被拒绝（`extension.manifestInvalid`），清单不超过 64 KiB。

| 字段 | 必填 | 规则 |
|---|---|---|
| `schemaVersion` | 是 | 固定为 `1` |
| `id` | 是 | `<发布者>.<名称>`，两段都以小写字母开头，各最多 32 个小写字母、数字或连字符，例如 `example.kill-timer` |
| `version` | 是 | 三段数字，如 `1.2.0`；升级时必须提高 |
| `hostApi` | 是 | `{ "minimum": "1.0", "maximumExclusive": "2.0" }`，两段数字；必须包含当前宿主 API 1.0，否则 `extension.hostApiIncompatible` |
| `displayNames` | 是 | 至少一种语言，键为 `zh-Hans`、`zh-Hant`，每项最多 64 字 |
| `descriptions` | 否 | 同上，每项最多 200 字，显示在安装预览里 |
| `runtime` | 带程序时必填 | 运行入口，见第 3.3 节 |
| `permissions` | 否 | 最多 16 项，只能是第 5 节权限目录里的名称；未登记的名称为 `extension.permissionUnknown` |
| `network` | 用 `network.fetch` 时必填 | `{ "hosts": ["api.example.com"] }`，最多 8 个小写主机名；与 `network.fetch` 权限必须同时声明 |
| `settings` | 否 | 最多 32 个设置项，见第 7.3 节；声明时必须同时声明 `ui.settings` 权限 |
| `commands` | 否 | 最多 16 个快捷键命令，见第 7.4 节；声明时必须同时声明 `ui.shortcut` 权限 |
| `assetPack` | 否 | 包内一个素材包 `pack.json` 的相对路径，见[素材包制作](asset-packs.md#5-随扩展分发) |
| `provides` | 否 | 预留：本扩展提供的能力编号，每项以 `<id>.` 开头，最多 32 项；当前版本只校验格式 |
| `dependencies` | 否 | 预留：`[{ "id": "...", "minimumVersion": "1.0.0" }]`，最多 16 项；当前版本只校验格式 |
| `highRiskBehaviors` | 否 | 预留：`memoryRead`、`memoryInjection`、`gameFileModification`、`gamePacketSend`；当前版本只校验格式 |
| `signature` | 否 | `{ "algorithm", "keyId", "value" }`；当前版本只记录，安装预览显示“已附签名，未验证” |

声明了 `permissions`、`network`、`settings`、`commands`、`provides` 或 `highRiskBehaviors` 中任一项而没有 `runtime` 的包，以 `extension.codeNotSupported` 拒绝。

### 3.3 运行入口 `runtime`

```json
"runtime": { "protocol": 1, "kind": "dotnet", "entry": "MyExtension.dll", "arguments": [] }
```

| 字段 | 规则 |
|---|---|
| `protocol` | 固定为 `1` |
| `kind` | `executable`：运行包内的 `entry`（`.exe`）。`dotnet`：用工具自带的 .NET 10 运行时执行包内的 `entry`（`.dll`，须随包带 `.runtimeconfig.json`）。`script`：用包内的解释器 `interpreter`（`.exe`）运行包内脚本 `entry` |
| `entry` | 包内相对路径，用 `/` 分隔 |
| `interpreter` | 只用于 `script`，包内 `.exe` 的相对路径，例如随包的嵌入式 Python `python/python.exe` |
| `arguments` | 可选，最多 16 个参数，每个最多 256 字，不含控制字符；追加在入口之后 |

工具不在 PATH 或其他系统位置查找程序，入口与解释器必须是安装目录里的普通文件（不是链接）。进程的运行环境：

- 工作目录是扩展的安装目录。
- 环境变量只有：`SystemRoot`、`windir`、`SystemDrive`、`PATH`（只含 Windows 系统目录）、`TEMP` 与 `TMP`（扩展自己的临时目录）、`NUMBER_OF_PROCESSORS`、`PROCESSOR_ARCHITECTURE`，以及 `MOONGATE_EXTENSION_ID`、`MOONGATE_EXTENSION_VERSION`、`MOONGATE_EXTENSION_DATA`（数据目录）、`MOONGATE_EXTENSION_PROTOCOL`（协议版本）。
- 资源上限：内存 256 MiB、CPU 25%、最多 4 个进程；不能使用其他程序的窗口句柄与剪贴板。同时最多运行 8 个扩展。
- .NET 扩展以 `net10.0` 为目标框架，用 `dotnet publish -c Release -o <目录>`（或 `dotnet build`）输出即可，不需要带运行时；工具以 `dotnet exec` 方式运行，项目可设 `UseAppHost=false` 省去 exe。

### 3.4 完整清单示例

```json
{
  "schemaVersion": 1,
  "id": "example.kill-timer",
  "version": "1.0.0",
  "hostApi": { "minimum": "1.0", "maximumExclusive": "2.0" },
  "displayNames": { "zh-Hans": "击杀计时器", "zh-Hant": "擊殺計時器" },
  "descriptions": { "zh-Hans": "记录每次战斗从开始到击杀的用时", "zh-Hant": "記錄每次戰鬥從開始到擊殺的用時" },
  "runtime": { "protocol": 1, "kind": "dotnet", "entry": "Moongate.Examples.KillTimer.dll" },
  "permissions": ["combat.read", "ui.page", "ui.overlay", "ui.settings", "ui.shortcut", "storage.local", "network.fetch"],
  "network": { "hosts": ["api.example.com"] },
  "settings": [
    { "key": "keep", "type": "number", "default": 10, "minimum": 5, "maximum": 50,
      "labels": { "zh-Hans": "保留的击杀条数", "zh-Hant": "保留的擊殺筆數" } }
  ],
  "commands": [
    { "id": "reset", "labels": { "zh-Hans": "清空击杀记录", "zh-Hant": "清空擊殺紀錄" } }
  ]
}
```

## 4. 与工具通信

### 4.1 消息格式

扩展进程的标准输入与标准输出承载 UTF-8 的 JSON-RPC 2.0 消息，**每行一条**，以换行结束，单行不超过 1 MiB。

- 请求：`{"jsonrpc":"2.0","id":1,"method":"combat.snapshot","params":{}}`。`id` 为非负整数或最多 64 字的文字，同一时间不能重复；`params` 必须是对象，可以省略。
- 响应：`{"jsonrpc":"2.0","id":1,"result":{...}}`，或 `{"jsonrpc":"2.0","id":1,"error":{"code":-32000,"message":"说明","data":{"reason":"原因码"}}}`。`error.data.reason` 是稳定的原因码，`message` 给人看。
- 通知：没有 `id` 的请求，不需要回应。
- 消息里出现 `jsonrpc`、`id`、`method`、`params`、`result`、`error` 以外的字段，或同一对象里有重复字段，都视为违反协议，扩展会被结束。
- 标准错误的每一行都写进扩展日志，适合输出调试信息。

### 4.2 生命周期

1. **启动**：用户启用扩展（或宿主启动时扩展已启用）后，工具启动进程。
2. **握手**：扩展必须在 10 秒内发出 `hello` 请求，握手前不能发别的消息：

   ```json
   {"jsonrpc":"2.0","id":1,"method":"hello","params":{"protocolVersion":1,"extensionId":"example.kill-timer","version":"1.0.0"}}
   ```

   `extensionId` 与 `version` 必须与清单一致（可直接取环境变量 `MOONGATE_EXTENSION_ID`、`MOONGATE_EXTENSION_VERSION`）。结果：

   ```json
   {"protocolVersion":1,"host":{"product":"Moongate","version":"0.1.0"},"grants":["combat.read","ui.page"],
    "settings":{"keep":10},"limits":{"maximumMessageBytes":1048576,"maximumInFlight":16,"messagesPerSecond":200,"memoryBytes":268435456,"cpuPercent":25}}
   ```

   `grants` 是当前已授予的权限，`settings` 是设置项的当前值（没保存过时为清单默认值）。
3. **运行**：调用宿主方法（第 6、7、8 节），用 `events.subscribe` 订阅事件，处理宿主发来的通知。
4. **心跳**：宿主每 10 秒发一次 `ping` 请求，扩展须在 5 秒内回应（`result` 为空对象即可）；连续两次没有回应视为挂死。
5. **退出**：宿主发 `shutdown` 请求（参数 `reason`），扩展回应后自行退出；3 秒后仍在运行则被强制结束。停用、卸载、升级、撤销开发模式与工具退出都会走这一步。

**一直读取标准输入。** 宿主向扩展写入时，扩展超过 5 秒不读取标准输入即被判定无响应并结束。处理耗时操作时请另开线程，读取循环保持运转。

### 4.3 调用、取消与错误

- 同时进行中的调用最多 16 个，超出时返回 `extension.tooManyCalls`。
- 取消自己发出的调用：发通知 `{"jsonrpc":"2.0","method":"$/cancel","params":{"id":5}}`，该调用以 `extension.cancelled` 结束。
- 调用的权限被用户撤销时，进行中的调用以 `extension.permissionDenied` 结束。
- 结果超过约 1 MiB 时返回 `extension.resultTooLarge`，请缩小查询范围（分页）。

不需要权限的方法：`hello`、`log`、`events.subscribe`、`events.unsubscribe`、`$/cancel`。

### 4.4 宿主发来的消息

| 消息 | 类型 | 参数 |
|---|---|---|
| `ping` | 请求 | 无；5 秒内回应 |
| `shutdown` | 请求 | `reason`；回应后退出 |
| `event` | 通知 | `type`（事件主题）、`resourceId`、`data`（事件内容） |
| `events/resync` | 通知 | `reason`。扩展读取事件过慢、队列已满，订阅已停止：重新读取快照后再调用 `events.subscribe` |
| `ui/event` | 通知 | `surface`（页面或元件的 `id`）、`node`（节点的 `id`）、`kind`（`click` 或 `change`）、`value`（修改后的值） |
| `settings/changed` | 通知 | `values`：全部设置项的当前值 |
| `grants/changed` | 通知 | `grants`：当前已授予的权限 |
| `shortcut/triggered` | 通知 | `command`（清单里的命令 `id`）、`actionId` |

### 4.5 日志

发通知 `{"jsonrpc":"2.0","method":"log","params":{"level":"info","message":"…"}}`，`level` 为 `debug`、`info`、`warning` 或 `error`。日志写入 `extensions/<标识>/logs/extension.log`（超过 1 MiB 轮换一份），在扩展卡片的“日志”里看最后 200 行；单行最多 2,000 字，每秒超过 100 行的部分丢弃。

### 4.6 结束与自动重启

| 情形 | 原因码 | 是否自动重启 |
|---|---|---|
| 没有在 10 秒内握手 | `extension.handshakeTimeout` | 是 |
| 连续两次不回应心跳，或超过 5 秒不读标准输入 | `extension.unresponsive` | 是 |
| 进程以非零退出码退出或关闭了标准输出 | `extension.exited` | 是 |
| 内存达到 256 MiB 后退出 | `extension.memoryLimit` | 是 |
| 进程以退出码 0 自行退出 | `extension.exited` | 否 |
| 消息不合协议 | `extension.protocolViolation` | 否 |
| 单条消息超过 1 MiB | `extension.messageTooLarge` | 否 |
| 每秒超过 200 条消息（可短暂突发到 400） | `extension.rateLimited` | 否 |
| 握手的协议版本或身份不符 | `extension.protocolIncompatible`、`extension.identityMismatch` | 否 |

自动重启按 1、5、15 秒退避；十分钟内异常超过 3 次即停止重启（`extension.crashLoop`），用户在扩展卡片上按“重启”后重新计数。扩展结束时，它的页面、浮窗元件、活动栏状态与输入租约都会撤下或交还。

## 5. 权限

扩展只能调用清单声明且已被授予的权限所对应的方法，订阅已授予权限对应的事件。

| 权限 | 风险 | 能做什么 | 方法 | 事件主题 |
|---|---|---|---|---|
| `combat.read` | 普通 | 读取 DPS、效果与分段 | `combat.snapshot` | `combat.summary.changed`、`combat.effects.changed`、`combat.encounter.closed`、`combat.observation.degraded`、`combat.measurement.changed` |
| `inventory.read` | 普通 | 读取背包与保管箱 | `inventory.accounts`、`inventory.items` | `inventory.snapshot.changed`、`inventory.observation.degraded` |
| `game.read` | 普通 | 读取游戏绑定、抓包状态、地图与频道 | `game.binding`、`game.observation` | `game.observation.changed`、`game.environment.changed` |
| `rooms.read` | 普通 | 读取合奏房间的成员与轮次 | `rooms.state` | — |
| `music.read` | 普通 | 查询本机曲库 | `music.query` | — |
| `ui.page` | 普通 | 在侧栏“扩展”分组显示页面 | `ui.page.set`、`ui.page.remove`、`ui.state.set` | — |
| `ui.overlay` | 普通 | 在浮窗元件库提供元件 | `ui.overlay.set`、`ui.overlay.remove`、`ui.state.set` | — |
| `ui.settings` | 普通 | 在设置页显示清单声明的设置项 | `settings.get` | — |
| `ui.activity` | 普通 | 在活动栏显示一句状态 | `ui.activity.set` | — |
| `ui.shortcut` | 普通 | 把清单声明的命令登记到快捷键设置 | —（按下时收到 `shortcut/triggered`） | — |
| `storage.local` | 普通 | 在自己的数据目录读写键值与文件，合计 16 MiB | `storage.get`、`storage.set`、`storage.delete`、`storage.list`、`storage.file.read`、`storage.file.write`、`storage.file.delete` | — |
| `input.selected-window` | 高风险 | 以前台或后台方式操作游戏 | `input.acquire`、`input.send`、`input.release` | — |
| `capture.selected-window` | 高风险 | 截取绑定的游戏窗口画面 | `capture.game` | — |
| `network.fetch` | 高风险 | 经工具访问清单声明的主机（HTTPS） | `network.fetch` | — |
| `files.read-selected` | 高风险 | 读取用户每次在文件选择框里选定的文件 | `files.pick` | — |

**授予规则。**

- 首次安装时，确认安装即授予全部普通权限；高风险权限默认关闭。
- 用户在“模块与扩展”页的扩展卡片上看到每项权限的名称、风险与说明，按“授予”或“撤销”逐项开关。撤销立即生效：进行中的调用结束、事件订阅退订、输入租约交还、界面撤下，扩展收到 `grants/changed`。
- 升级时新增的权限在安装预览里标“（新增）”：新增的普通权限随确认授予；高风险权限只保留旧版本已授予且仍声明的。
- 读取游戏进程内存不向扩展开放，清单声明 `process.read.selected-game` 的包以 `extension.permissionUnavailable` 拒绝安装。

## 6. 数据与事件

### 6.1 读取方法

| 方法 | 参数 | 结果 |
|---|---|---|
| `combat.snapshot` | 无 | `revision` 与 `workspace`：DPS检测页使用的同一份战斗工作区（整场、分段、测量、效果、技能与目标明细，名称已换成游戏内文字）；没有数据时为 `null` |
| `inventory.accounts` | `query`：`{ "pageSize": 25, "cursor": null }` | 账号分页：每个账号的角色、资源与道具汇总 |
| `inventory.items` | `query`：`accountId`、`characterId`、`container`（`backpack`、`character-storage`、`account-storage`）、`category`、`search`、`pageSize`（默认 100）、`cursor`、`currentOnly`，都可省略 | 道具分页 |
| `game.binding` | 无 | 当前绑定的游戏窗口与会话 |
| `game.observation` | 无 | 抓包状态、识别到的角色、地图与频道 |
| `rooms.state` | 无 | 当前合奏房间的成员、分组与轮次状态 |
| `music.query` | `query`：`collection`（`folders`、`playlists`、`playlist-items`、`scores`）、`parentId`、`playlistId`、`scopeKey`、`search`、`pageSize`、`cursor` | 曲库分页 |
| `settings.get` | 无 | 设置项的当前值 |

查询参数与本机 API 的对应查询相同，字段名为小驼峰、枚举值为小写连字符，未知字段会被拒绝（`request.invalid`）。各结果的完整结构见工具运行时的 `GET /api/v1/openapi.json`（[开发文档第 6 节](development.md#6-接口契约与客户端)）。宿主缺少某项能力时返回 `capability.unavailable`。

### 6.2 事件订阅

```json
{"jsonrpc":"2.0","id":7,"method":"events.subscribe","params":{"topics":["combat.summary.changed","combat.encounter.closed"]}}
```

`topics` 为 1–16 项；结果 `topics` 是订阅后的全部主题。`events.unsubscribe` 用同样的参数退订。只能订阅已授予权限对应的主题（见第 5 节的表）。事件以 `event` 通知送达，`data` 与本机 API 事件流中同一主题的内容相同；例如 `combat.summary.changed` 的 `data.durationMs` 是当前这场的时长，`combat.encounter.closed` 的 `data.summary` 是结束的这一场的汇总。

## 7. 界面

扩展没有自己的界面程序：页面与浮窗元件用声明式描述交给 Moongate，由工具用自己的控件呈现，跟随工具的主题与语言。

### 7.1 页面与浮窗元件

- `ui.page.set`：参数 `id`、`titles`、`schemaVersion`、`root`。页面出现在侧栏“扩展”分组里扩展的入口下，一个扩展的多个页面依次排列。
- `ui.overlay.set`：参数同上，另有 `size`（`small`、`medium`、`large`，默认尺寸 220×96、300×160、380×240）。元件出现在浮窗版面页的元件库，用户可以像内建元件一样摆放、上锁和设置外观。
- `ui.page.remove`、`ui.overlay.remove`：参数 `id`。
- `id`：小写字母开头，1–32 个小写字母、数字或连字符。`titles`：以 `zh-Hans`、`zh-Hant` 为键，各 1–32 字。`schemaVersion`：`1`。
- 同一 `id` 再次调用即替换描述。扩展停止后页面与元件撤下，用户摆好的元件位置保留，下次运行时回到原处。

### 7.2 界面描述

`root` 是节点树。每个节点是带 `type` 的对象，可以带 `id`（需要交互时必填）与 `children`。

| 类型 | 属性 | 呈现 |
|---|---|---|
| `column` | `children`，`gap`（0–48，默认 8） | 纵向排列 |
| `row` | `children`，`gap` | 横向排列，放不下时换行 |
| `card` | `title`，`children` | 带标题的内嵌块 |
| `text` | `text` 或 `bind`，`style`（`title`、`body`、`muted`、`caption`） | 一段文字 |
| `value` | `label`，`text` 或 `bind`，`unit` | 小标签加大号数值 |
| `table` | `columns`（`[{ "key", "title" }]`，最多 12 列），`bind` 指向对象数组 | 表格，最多 200 行 |
| `list` | `bind` 指向数组 | 每项一行，最多 200 行 |
| `progress` | `bind`（0–1 的数字），`label` | 进度条；值不是数字时显示为进行中 |
| `button` | `id`，`text` | 按钮；按下时扩展收到 `kind` 为 `click` 的 `ui/event` |
| `toggle` | `id`，`text`，`bind`（布尔） | 开关；修改时收到 `kind` 为 `change`、`value` 为布尔的 `ui/event` |
| `select` | `id`，`options`（`[{ "value", "text" }]`，最多 64 项），`bind` | 下拉选择；修改时 `value` 为所选的 `value` |
| `input` | `id`，`placeholder`，`bind` | 文字输入（最多 256 字）；按回车或离开输入框时 `value` 为输入的文字 |
| `icon` | `text` | 一个字符或符号 |
| `spacer` | `size`（0–64，默认 8） | 空白 |
| `divider` | — | 分隔线 |

- **文字**：`text`、`title`、`label`、`unit`、`placeholder` 可以写字符串，也可以写 `{"zh-Hans": "…", "zh-Hant": "…"}`，按界面语言选用。
- **绑定**：`bind` 是状态键名，值由 `ui.state.set` 推送：`{"values": {"current": "1:23", "kills": [...]}}`，值为 `null` 时删除该键。同一扩展的页面与元件共用一份状态。绑定的键缺失时显示“未读取”；数字按本地格式显示，布尔显示“是／否”。没有 `bind` 的节点可以用 `value` 写固定值。
- **交互**：只有带 `id` 的 `button`、`toggle`、`select`、`input` 能交互，没有 `id` 时显示为停用。浮窗元件上锁时鼠标穿透，元件内的控件不接收输入。
- **容错**：不认识的节点显示一行“不支持的界面元素：类型”，其余照常显示。
- **上限**：描述 128 KiB、24 层、400 个节点；每个扩展 8 个页面、8 个浮窗元件；状态 128 个键、合计 64 KiB，键名 1–64 字。超出返回 `extension.uiLimit`。

示例：

```json
{"jsonrpc":"2.0","id":3,"method":"ui.page.set","params":{
  "id":"main","schemaVersion":1,"titles":{"zh-Hans":"击杀计时","zh-Hant":"擊殺計時"},
  "root":{"type":"column","children":[
    {"type":"card","title":{"zh-Hans":"当前战斗","zh-Hant":"目前戰鬥"},"children":[
      {"type":"value","label":{"zh-Hans":"用时","zh-Hant":"用時"},"bind":"current"}]},
    {"type":"table","bind":"kills","columns":[{"key":"time","title":"时间"},{"key":"duration","title":"用时"}]},
    {"type":"button","id":"reset","text":{"zh-Hans":"清空记录","zh-Hant":"清空紀錄"}}]}}}
{"jsonrpc":"2.0","id":4,"method":"ui.state.set","params":{"values":{"current":"0:42","kills":[{"time":"20:15:03","duration":"1:07"}]}}}
```

### 7.3 设置项

清单的 `settings` 声明设置项，出现在设置页的“扩展”卡片里，修改即保存，扩展收到 `settings/changed`。

| 字段 | 规则 |
|---|---|
| `key` | 小写字母开头，最多 32 个字母、数字、`_` 或 `-`，不重复 |
| `type` | `toggle`（开关，默认值为布尔）、`number`（数字，可带 `minimum`、`maximum`）、`text`（文字，最多 256 字）、`choice`（选择，须带 `options`） |
| `labels` | 以语言为键，各最多 32 字 |
| `default` | 必填，类型与 `type` 一致，须在范围内 |
| `options` | 只用于 `choice`：1–32 项 `{ "value", "labels" }`，`value` 最多 64 字 |

### 7.4 快捷键命令

清单的 `commands` 声明命令（`id` 小写字母开头、最多 32 字；`labels` 各最多 32 字）。命令以“扩展名：命令名”列在设置页快捷键区，由用户分配组合键；F7–F10 与 Pause 保留给内建动作，不能分配给扩展命令。扩展运行且已授予 `ui.shortcut` 时，按下组合键扩展收到 `shortcut/triggered`；扩展停用、未运行或未授权时，快捷键区写明不可用原因，绑定保留。

### 7.5 活动栏状态

`ui.activity.set`：`{"text": "计时中 1:23", "tone": "active"}`。`text` 最多 16 字，超出返回 `request.invalid`；`tone` 为 `neutral`、`active`、`warning` 或 `error`；`text` 为空时清除。没有模块持有游戏输入时，活动栏显示“扩展 名称：状态”。

## 8. 高风险能力

以下权限默认关闭，由用户在扩展卡片上逐项授予。

### 8.1 操作游戏（`input.selected-window`）

**取得输入租约** `input.acquire`：`{"mode": "background", "seconds": 60}`。

- `mode`：`background`（后台，默认）不把游戏窗口带到前台，也不移动用户的鼠标；`foreground`（前台）用系统键鼠，要求游戏窗口能接收输入。
- `seconds`：1–300，默认 60，到期后租约失效，需要重新取得。
- 结果：`leaseId`、`mode`、`expiresAt`。
- 持有租约期间，染色、合奏等其他活动不能同时操作同一个游戏；被占用时返回 `resource.busy`，说明里写出占用者。没有绑定游戏窗口为 `input.bindingUnavailable`，后台方式的前提不满足时返回下表的原因码。

**发送动作** `input.send`：`{"actions": [...], "intervalMs": 120, "jitterMs": 30, "jitterPx": 2}`。

| 动作 | 写法 |
|---|---|
| 点按 | `{"kind":"tap","at":{"x":100,"y":200}}` |
| 长按 | `{"kind":"longPress","at":{"x":100,"y":200},"holdMs":800}`，`holdMs` 100–5000 |
| 拖动 | `{"kind":"drag","from":{"x":100,"y":200},"to":{"x":300,"y":200},"steps":6}`，`steps` 1–12，末段减速后松开 |
| 滑动 | `{"kind":"swipe","from":{...},"to":{...}}` |
| 按键 | `{"kind":"key","vk":65,"holdMs":40}`，`vk` 为虚拟键码，只接受字母、数字、F1–F12、回车、Esc、空格、退格、Tab 与方向键；`holdMs` 0–2000 |

- 坐标是游戏窗口客户区内的物理像素（左上角为 0,0），越出客户区为 `input.pointOutsideWindow`。
- 每次最多 32 个动作，`intervalMs` 0–2000（默认 120），`jitterMs` 0–500、`jitterPx` 0–8（默认 0），抖动后的坐标仍在客户区内；按计划估算的总时长不超过 15 秒（`input.planTooLong`）。不合规则为 `input.actionInvalid`。
- 结果 `delivered` 是实际送出的动作数。中途失效时调用以原因码结束，已送出的动作不重放。

**交还** `input.release`。紧急停止（默认 F9）、撤销权限、停用或扩展退出都会立即收回租约，并松开已按下的按键与触点；之后的 `input.send` 返回 `input.leaseInvalid`。

**后台方式的原理与前提。** 后台方式用合成触控生成手势，再把触控消息投递给游戏窗口，按键以窗口消息投递，游戏窗口不被前置，鼠标位置不变。前提：Windows 10 1809 或更新版本、已绑定游戏窗口且窗口没有最小化、手势在同一块显示器内。条件不满足时返回原因码，不会改用前台方式：

| 原因码 | 含义 |
|---|---|
| `input.bindingUnavailable` | 没有绑定游戏窗口，或窗口已不存在 |
| `input.windowMinimized` | 游戏窗口最小化 |
| `input.backgroundUnsupported` | 系统版本不支持合成触控 |
| `input.bindingChanged` | 绑定的窗口或游戏进程已变化 |
| `input.backgroundFramesUnavailable` | 系统没有生成触控帧（重试三次后仍失败） |
| `input.keyUnsupported` | 当前键盘布局无法发送该键 |
| `input.leaseInvalid` | 没有租约，或租约已到期、被紧急停止或撤销 |
| `input.deviceFailure` | 输入设备出错，已松开按下的输入 |

游戏以管理员身份运行时，Moongate 也需要以管理员身份运行。后台输入同样是自动操作游戏，可能不符合游戏服务条款，账号风险由使用者自行承担；请在扩展说明里如实告知用户扩展会做哪些操作。

### 8.2 截图（`capture.selected-window`）

`capture.game`：`{"maxWidth": 640}`。截取绑定的游戏窗口，按 `maxWidth`（16–640）等比缩小，返回 `width`、`height`、`format`（`rgb24`）与 Base64 的 `data`（每像素 3 字节，逐行排列）。每秒最多两次（否则 `resource.capacityExceeded`），窗口最小化时为 `input.windowMinimized`。

### 8.3 联网（`network.fetch`）

`network.fetch`：`{"url": "https://api.example.com/x", "method": "GET"}`，`method` 为 `GET` 或 `POST`，POST 可带 `body`（文字，最多 64 KiB）与 `contentType`（默认 `application/json`）。

- 只接受清单 `network.hosts` 列出的主机、HTTPS、默认端口，否则 `extension.hostNotDeclared`。
- 不跟随重定向、不带 Cookie，请求头 `User-Agent: Moongate-Extension/1`。
- 响应最多 512 KiB（超出为 `extension.resultTooLarge`），超时 15 秒（`extension.networkTimeout`），连接失败为 `extension.networkFailed`。
- 结果：`status`、`contentType`，文本与 JSON 类型的响应在 `text` 里，其他在 `base64` 里。

### 8.4 读取用户选的文件（`files.read-selected`）

`files.pick`：`{"title": "选择存档"}`（`title` 最多 64 字）。工具弹出文件选择框，标题写明是哪个扩展在请求；用户选定后返回 `cancelled: false`、`name`、`size`、`sha256` 与 Base64 的 `content`（最多 512 KiB），扩展拿不到本机路径。用户取消时返回 `{"cancelled": true}`。没有打开的 Moongate 窗口为 `extension.noDesktop`；同一扩展同时只能有一个请求、两次至少间隔 5 秒（`resource.capacityExceeded`）；两分钟内没有选择为 `extension.fileRequestExpired`。

## 9. 存储（`storage.local`）

扩展自己的数据在 `MOONGATE_EXTENSION_DATA` 指向的目录里，卸载扩展时一起删除。也可以直接读写这个目录；经下面的方法读写时，写入以临时文件替换，不会读到写了一半的内容。

| 方法 | 参数 | 结果 |
|---|---|---|
| `storage.get` | `key` | `value`（任意 JSON，没有时为 `null`） |
| `storage.set` | `key`、`value` | 空对象 |
| `storage.delete` | `key` | `deleted` |
| `storage.list` | 无 | `keys` |
| `storage.file.read` | `name` | `content`（Base64，没有时为 `null`） |
| `storage.file.write` | `name`、`content`（Base64） | 空对象 |
| `storage.file.delete` | `name` | `deleted` |

键为 1–128 个字母、数字或 `. _ : -`，最多 1,024 个键、每个值不超过 256 KiB；文件名为不含路径的 1–64 个字母、数字、点、下划线或连字符，单个文件不超过 4 MiB；键值与文件合计不超过 16 MiB。超出为 `extension.storageQuota`。

## 10. 最小示例

### 10.1 消息往来

不依赖任何语言，一个只显示计数按钮的扩展与工具之间的完整往来如下（`→` 为扩展写到标准输出，`←` 为从标准输入读到）：

```text
→ {"jsonrpc":"2.0","id":1,"method":"hello","params":{"protocolVersion":1,"extensionId":"example.counter","version":"1.0.0"}}
← {"jsonrpc":"2.0","id":1,"result":{"protocolVersion":1,"host":{"product":"Moongate","version":"0.1.0"},"grants":["ui.page"],"settings":{},"limits":{...}}}
→ {"jsonrpc":"2.0","id":2,"method":"ui.page.set","params":{"id":"main","schemaVersion":1,"titles":{"zh-Hans":"计数器"},"root":{"type":"column","children":[{"type":"value","label":"次数","bind":"count"},{"type":"button","id":"add","text":"加一"}]}}}
← {"jsonrpc":"2.0","id":2,"result":{"revision":"5"}}
→ {"jsonrpc":"2.0","id":3,"method":"ui.state.set","params":{"values":{"count":0}}}
← {"jsonrpc":"2.0","id":3,"result":{}}
← {"jsonrpc":"2.0","method":"ui/event","params":{"surface":"main","node":"add","kind":"click","value":null}}
→ {"jsonrpc":"2.0","id":4,"method":"ui.state.set","params":{"values":{"count":1}}}
← {"jsonrpc":"2.0","id":4,"result":{}}
← {"jsonrpc":"2.0","id":"h1","method":"ping","params":{}}
→ {"jsonrpc":"2.0","id":"h1","result":{}}
← {"jsonrpc":"2.0","id":"h2","method":"shutdown","params":{"reason":"扩展已停用"}}
→ {"jsonrpc":"2.0","id":"h2","result":{}}
```

### 10.2 Python 脚本版

同一个计数器，只用 Python 标准库。Python 不在系统里查找，须把 [Windows 嵌入式 Python](https://www.python.org/downloads/windows/)（“Windows embeddable package (64-bit)”）解压到包内的 `python/` 目录一起打包。

```text
counter/
  extension.json
  main.py
  python/python.exe  （嵌入式 Python 的全部文件）
```

`extension.json`：

```json
{
  "schemaVersion": 1,
  "id": "example.counter",
  "version": "1.0.0",
  "hostApi": { "minimum": "1.0", "maximumExclusive": "2.0" },
  "displayNames": { "zh-Hans": "计数器", "zh-Hant": "計數器" },
  "runtime": { "protocol": 1, "kind": "script", "interpreter": "python/python.exe", "entry": "main.py" },
  "permissions": ["ui.page"]
}
```

`main.py`：

```python
import json, os, sys

pending, next_id, count = {}, 0, 0

def send(message):
    sys.stdout.buffer.write((json.dumps(message, ensure_ascii=False) + "\n").encode("utf-8"))
    sys.stdout.buffer.flush()

def call(method, params, on_result=None):
    global next_id
    next_id += 1
    pending[next_id] = on_result
    send({"jsonrpc": "2.0", "id": next_id, "method": method, "params": params})

def log(level, text):
    send({"jsonrpc": "2.0", "method": "log", "params": {"level": level, "message": text}})

def publish():
    call("ui.state.set", {"values": {"count": count}})

def on_hello(result):
    log("info", "握手完成，已授予：" + "、".join(result["grants"]))
    call("ui.page.set", {
        "id": "main", "schemaVersion": 1, "titles": {"zh-Hans": "计数器", "zh-Hant": "計數器"},
        "root": {"type": "column", "children": [
            {"type": "value", "label": {"zh-Hans": "次数", "zh-Hant": "次數"}, "bind": "count"},
            {"type": "button", "id": "add", "text": {"zh-Hans": "加一", "zh-Hant": "加一"}}]}})
    publish()

call("hello", {"protocolVersion": 1, "extensionId": os.environ["MOONGATE_EXTENSION_ID"],
               "version": os.environ["MOONGATE_EXTENSION_VERSION"]}, on_hello)

while True:
    raw = sys.stdin.buffer.readline()
    if not raw:
        break  # 标准输入关闭：工具已结束本扩展
    line = raw.decode("utf-8").strip()
    if not line:
        continue
    message = json.loads(line)
    method = message.get("method")
    if method is None:  # 对自己请求的响应
        callback = pending.pop(message["id"], None)
        if "error" in message:
            log("warning", "调用失败：" + message["error"]["data"]["reason"])
        elif callback:
            callback(message["result"])
    elif "id" in message:  # 宿主请求：ping 与 shutdown 都回应空结果
        send({"jsonrpc": "2.0", "id": message["id"], "result": {}})
        if method == "shutdown":
            break
    elif method == "ui/event" and message["params"]["node"] == "add":
        count += 1
        publish()
```

这个循环只做很快的处理，读取标准输入不会停顿；要做耗时操作（例如大量计算）时放到线程里，主循环继续读取。

### 10.3 示例 kill-timer 讲解

`frontends/examples/extensions/kill-timer` 用 C# 实现，分两部分：

- **`ExtensionConnection.cs`**：协议的最小客户端。`StartAsync` 启动读取循环并发出 `hello`；`CallAsync` 为每个请求分配递增的 `id`，用一张表等待对应的响应，宿主返回错误时抛出带原因码的 `HostCallException`；读取循环对宿主请求（`ping`、`shutdown`）立即回应空结果，`shutdown` 后结束；通知交给 `Notification` 事件在线程池处理，读取循环不被阻塞。可以直接复制到自己的 .NET 扩展里使用。
- **`Program.cs`**：业务流程。
  1. 握手后从 `hello` 结果的 `settings` 读取“保留的击杀条数”与“用时显示毫秒”。
  2. 用 `storage.get` 读回上次保存的击杀记录。
  3. 用 `ui.page.set` 提供页面（当前用时、最近击杀表格、“清空记录”按钮），用 `ui.overlay.set` 提供 `small` 浮窗元件，两者绑定同一份状态（`current`、`last`、`kills`）。
  4. `events.subscribe` 订阅 `combat.summary.changed`（更新当前用时）与 `combat.encounter.closed`（一场结束时记下用时与 DPS，`storage.set` 保存）。
  5. 收到按钮的 `ui/event` 时清空记录；收到 `settings/changed` 时按新设置重新显示。每次变化都以 `ui.state.set` 推送状态。
- **`Moongate.Examples.KillTimer.csproj`**：普通控制台程序，`UseAppHost=false`（以 `dotnet` 方式运行，不需要 exe），构建时把 `extension.json` 复制到输出目录，输出目录就是开发模式可以直接加载的扩展目录。

## 11. 调试

1. “模块与扩展”页的“扩展”卡片上按“开启开发模式”。
2. 按“加载开发目录”，选择含 `extension.json` 的目录。开发模式只加载声明了 `runtime` 的扩展；工具直接从该目录运行，不复制文件，扩展名后标“· 开发模式”。
3. 改完代码重新构建，在扩展卡片上按“重启”。
4. 按“日志”查看最后 200 行：工具记录的启动、握手、授权变化与结束原因，扩展经 `log` 写的日志，以及标准错误的每一行。
5. 卡片上的运行状态写明“运行中（进程号）”“正在启动”“异常退出，将自动重启（第 N 次）”“已停止，不再自动重启”等，非运行状态附原因码（第 4.6 节）。

关闭开发模式时开发中的扩展随之停止；卸载开发扩展不会删除你的目录。也可以从源码运行工具调试，见[开发文档第 4 节](development.md#4-构建与运行)。

## 12. 打包、安装与升级

### 12.1 打包

把 `extension.json` 和程序需要的全部文件放进一个目录，压缩成 ZIP，`extension.json` 必须在 ZIP 根目录（不要多套一层文件夹）。.NET 扩展用 `dotnet publish -c Release -o <目录>` 输出后把 `extension.json` 放进去。

| 项目 | 上限 |
|---|---|
| ZIP 文件 | 32 MiB |
| 解压后总量 | 64 MiB |
| 条目数 | 1,000 |
| 清单 | 64 KiB |

### 12.2 安装检查会拒绝的情形

| 原因码 | 情形 |
|---|---|
| `extension.limitExceeded` | 超过上表的大小或数量 |
| `extension.packageInvalid` | ZIP 损坏，或解出的内容与检查结果不一致 |
| `extension.pathRejected` | 条目路径越界或不合法（绝对路径、`..` 等） |
| `extension.manifestMissing` | 根目录没有 `extension.json` |
| `extension.manifestInvalid` | 清单不合第 3 节的规则；`assetPack`、`runtime.entry`、`runtime.interpreter` 指向的文件不在包内 |
| `extension.permissionUnknown` | 声明了未登记的权限 |
| `extension.permissionUnavailable` | 申请了不开放的权限（读取游戏进程内存） |
| `extension.hostApiIncompatible` | 当前宿主 API 不在 `hostApi` 范围内 |
| `extension.codeNotSupported` | 没有 `runtime`，却含可执行内容（PE 文件，或 `.exe`、`.dll`、`.sys`、`.com`、`.scr`、`.msi`、`.ps1`、`.psm1`、`.bat`、`.cmd`、`.vbs`、`.js`、`.py`）或声明了需要程序的字段 |
| `extension.fileTypeRejected` | 纯数据扩展含允许列表以外的文件类型 |
| `extension.versionConflict` | 已安装同一版本但内容不同；请提高版本号 |
| `extension.downgradeRejected` | 版本低于已安装的版本；需先卸载 |
| `extension.previewExpired` | 安装预览超过 15 分钟，需要重新检查 |

### 12.3 安装与升级

1. 用户在“模块与扩展”页按“安装扩展模块”选择 ZIP，或把 ZIP 放进扩展文件夹的 `inbox`（卡片底部“打开扩展文件夹”可以找到），在“待安装”里逐个“检查”。
2. 工具先在暂存区检查，显示预览：标识、版本、发布者、文件数、大小、签名状态、运行方式、普通权限、高风险权限、访问的主机与动作（安装／升级／重新安装）。
3. 用户确认后安装，新装的扩展默认停用，用户按“启用”后启动。
4. 版本更高为升级，沿用原来的启用状态；同版本同内容为重新安装。

## 13. 安全边界

扩展进程在独立进程里以当前 Windows 用户身份运行，**不是沙箱**：

- 工具约束的是扩展**经工具**所做的事：只能调用已授予权限的方法，操作游戏要经过输入租约与紧急停止，联网只能访问声明的主机，读取文件只能拿到用户选的那一个。
- 工具同时限制它的资源与系统操作：内存、CPU、进程数，不能使用其他程序的窗口句柄、剪贴板，不能改系统参数与显示设置或注销关机，环境变量只有白名单；工具退出时扩展进程随之结束。
- 扩展程序本身仍可以做当前用户能做的事（例如直接读写用户有权限的文件、自行发起网络连接）。

请只安装信任来源的扩展；作为作者，请在说明里如实写明扩展会做什么、为什么需要每一项权限。

## 14. 本机管理接口

前端与脚本可以通过本机 API 管理扩展（修改需要 `modules.configure`；认证见[开发文档第 6 节](development.md#6-接口契约与客户端)）：

| 接口 | 行为 |
|---|---|
| `GET /api/v1/extensions` | 已安装扩展、运行状态、权限与待安装包 |
| `POST /api/v1/extensions/previews` | 正文 `uploadId` 或 `inboxName`，返回安装预览 |
| `POST /api/v1/extensions/previews/{id}/install` | 按预览安装 |
| `POST /api/v1/extensions/{id}/enabled` | 正文 `enabled`，启用或停用 |
| `POST /api/v1/extensions/{id}/permissions` | 正文 `grants`（权限 → 是否授予），授予或撤销权限 |
| `POST /api/v1/extensions/{id}/restart` | 重启 |
| `GET /api/v1/extensions/{id}/logs` | 最近的日志 |
| `POST /api/v1/extensions/{id}/uninstall` | 卸载 |
| `POST /api/v1/extensions/development` | 开启或关闭开发模式 |
| `POST /api/v1/extensions/development/load` | 加载开发目录 |
| `GET /api/v1/extensions/surfaces` | 运行中扩展的页面、浮窗元件、状态、设置项与命令 |
