# Moongate 开发文档

[返回首页](../README.md) · [扩展模块开发](extensions.md) · [素材包制作](asset-packs.md)

本文面向想从源码构建、修改或为 Moongate 编写客户端与扩展的开发者。

## 1. 架构一页纸

```text
┌──────────────────────────┐      HTTP / SSE（127.0.0.1）     ┌───────────────────────────────┐
│ 桌面前端 Avalonia 12      │ ───────────────────────────────▶ │ Host（ASP.NET Core 模块化单体）│
│ 主窗口、迷你窗、游戏浮窗   │ ◀── 快照 + 事件流 + Operation ── │ 模块 · 契约 · SQLite · 认证     │
└──────────────────────────┘                                   └──────┬───────────────┬────────┘
           ▲ 命名管道握手（当前 Windows 用户）                          │ 私有 IPC      │ 私有 IPC
           └──────────────────── runtime/instance.json ─────────────────▼               ▼
                                                        ┌───────────────────┐ ┌──────────────────┐
                                                        │ 观察 Worker (.NET) │ │ 染色 Worker      │
                                                        │ Npcap 共享抓包     │ │ Python + OCR     │
                                                        └───────────────────┘ └──────────────────┘
```

- **Host** 是唯一的业务权威：模块状态机、队列、计划、停止条件与持久化都在后端。目录包里前端带 `--owner-process` 启动宿主，前端结束后宿主按正常停止流程退出；单独启动的宿主不随前端退出。只监听 `127.0.0.1`，默认端口 19998；该端口被其他软件占用或被系统保留时改用系统分配的端口，前端从 `runtime/instance.json` 取实际地址。
- **模块**在同一进程内运行，通过公开应用接口协作，不读写其他模块的私有表。内置模块：core、game-access、music-library、performance、rooms、synchronization、color、environment、presentation、combat-observation、inventory。performance（游戏内演奏）与 environment（游戏内时间、天气等）当前返回 `module.notImplemented`。
- **Provider** 把具体数据源（台服抓包、回放、GitHub Releases、隧道）适配为统一的观测、能力与操作接口。
- **Worker** 隔离原生依赖和重计算：观察 Worker 负责 Npcap 共享抓包，绑定游戏窗口并确定网卡后常驻；染色 Worker 按任务启动。
- **Operation** 表示后端受理的异步工作，分别给出受理、执行／输入提交与观测到的效果，支持幂等键、取消与重启后的中断恢复。
- **游戏输入**统一经过活动租约与紧急停止仲裁，输入热路径不等待界面、数据库、网络或重计算。
- **RoomServer** 是可自托管的合奏房间服务，只协调房间与校时，不下发任意键码或坐标。
- **工具根**：运行时的全部文件（`bootstrap`、`data`、`runtime`、`desktop`）都在工具根内。解压版的工具根是包根下的 `userdata\`；从构建输出运行时用 `--portable-root <绝对路径>` 指定，宿主与桌面前端传同一个目录。

## 2. 仓库结构

```text
src/
  Host/                 宿主入口、HTTP 端点、Operation、更新、扩展安装
  Contracts/            公共 DTO 与契约（生成 OpenAPI、JSON Schema、SDK 的唯一来源）
  ModuleSdk/            模块生命周期、工具根、本机发现与握手
  Infrastructure/       存储（SQLite、写队列、outbox）、文件、备份、诊断
  Platform.Windows/     窗口、输入、快捷键等 Windows 平台服务
  Modules/              MusicLibrary、Rooms、Color、CombatObservation、Inventory
  Providers/            TwObservation（台服）、Replay、GitHubReleases、Tunnels
  RoomServer/           合奏房间服务
  PortableLauncher/     解压版包根入口 Moongate.exe，兼作更新替换器
workers/
  game-observation/     .NET 观察 Worker（Npcap）
  color-engine/         Python 染色引擎、Tesseract 锁定清单与测试
frontends/
  desktop/              Avalonia 桌面前端与无界面测试
  sdk/csharp/           生成的 C# 客户端 SDK 项目
  examples/             Node 与 .NET 前端接入示例；extensions/kill-timer 是带程序的扩展示例
contracts/
  fixtures/             脱敏或合成的契约与协议夹具
  worker/               Worker IPC 的 JSON Schema
tests/
  Domain/ Providers/ Contracts/ EndToEnd/ Performance/   按职责分组的测试与探针
scripts/
  build/                构建、契约生成、打包与对照脚本
  maintenance/          来源基线、生成物清理与预算检查、实采工具
  hooks/                提交前钩子
packaging/              目录包的启动与安装脚本、许可文本
docs/                   用户与开发文档
```

产品名为 Moongate；程序集、命名空间、`MABITOOL_*` 环境变量、数据路径与发行身份 `mabitool` 使用技术标识 `MabiTool`，保持兼容。

## 3. 环境要求

| 工具 | 版本 | 用途 |
|---|---|---|
| .NET SDK | `global.json` 固定的 10.0.402 | 构建全部 .NET 项目 |
| PowerShell | 7 | 构建与打包脚本 |
| Python | 3.12 | 染色引擎、仓库脚本与 Python 测试 |
| Node.js | 24 | TypeScript 契约测试与前端示例 |
| Npcap | 官方最新版 | 真实台服抓包（构建不需要） |

依赖版本集中在 `Directory.Packages.props`，所有项目使用锁文件（`packages.lock.json`），还原以 `--locked-mode` 执行。警告视为错误。

## 4. 构建与运行

### 4.1 构建

```powershell
pwsh -NoProfile -File .\scripts\build\backend.ps1 -Action Build -Configuration Debug
```

构建 `MabiTool.slnx` 中的后端、Worker、桌面前端与测试项目，输出在 `artifacts/build/`；同时生成 OpenAPI、JSON Schema 与 C#／TypeScript SDK 到 `artifacts/generated/`。省略 `-Configuration` 时为 Release。

### 4.2 运行后端与桌面

构建输出会被重新构建清理，从源码运行时用 `--portable-root` 把工具根指到仓库内的 `data\` 子目录（已在 `.gitignore` 中）。首次启动加 `--initialize-data`，之后省略：

```powershell
dotnet .\artifacts\build\bin\MabiTool.Host\debug\MabiTool.Host.dll --initialize-data --portable-root "$PWD\data\dev"
```

在另一个终端启动桌面前端，传入同一个工具根：

```powershell
dotnet run --project .\frontends\desktop\src\MabiTool.Desktop -- --portable-root "$PWD\data\dev"
```

`frontends\desktop\start.cmd` 会优先运行已构建的桌面程序，工具根固定为仓库内的 `data\review`；需要后端时同样以 `--portable-root "$PWD\data\review"` 启动 Host。

### 4.3 不连游戏的开发方式

- **样本数据**：`frontends\desktop\启动 Moongate（样本数据）.cmd`（等同 `start.cmd --sample`）使用前端内置样本，不需要后端，适合调界面。
- **模拟后端**：Host 带 `--mock` 启动时使用合成回放 Provider，走完整的认证、Operation、SQLite、快照与 SSE：

```powershell
$env:MABITOOL_API_PORT = '20098'
dotnet .\artifacts\build\bin\MabiTool.Host\debug\MabiTool.Host.dll --mock --initialize-data --portable-root "$PWD\artifacts\tmp\mock-dev"
node .\scripts\build\replay-demo.mjs artifacts\tmp\mock-dev\runtime\instance.json normal
```

### 4.4 常用环境变量

| 变量 | 作用 |
|---|---|
| `MABITOOL_API_PORT` | Host 端口，默认 19998（被其他软件占用或被系统保留时自动改用空闲端口）；`0` 由系统分配（测试使用）；显式指定的端口不可用时不自动更换，以退出码 6 结束 |
| `MABITOOL_ALLOWED_ORIGINS` | 浏览器前端的精确 Origin 白名单，默认空 |
| `MABITOOL_COLOR_PYTHON` | 染色引擎使用的 Python 3.12 解释器（已安装 `workers/color-engine/requirements.txt`） |
| `MABITOOL_COLOR_WORKER_DIRECTORY` | 染色引擎源码目录，开发时指向 `workers\color-engine` |
| `MABITOOL_COLOR_TESSERACT_DIRECTORY` | Tesseract 安装目录 |
| `MABITOOL_CLOUDFLARED_PATH`、`_SHA256`、`_VERSION` | 合奏 Cloudflare 公网方式使用的 cloudflared 路径、哈希与版本 |

## 5. 测试约定

- **按改动选择最小用例**：不跑全量测试。直接指定测试项目与用例：

  ```powershell
  dotnet test frontends/desktop/tests/MabiTool.Desktop.Tests/MabiTool.Desktop.Tests.csproj --filter 'FullyQualifiedName~CombatControlRequestTests'
  ```

- **测试标记**：xUnit 的 `Slow` 标记真实进程、安装／更新、发行包或大文件等慢用例；`Desktop` 标记依赖真实窗口或前台的用例；`Flaky` 标记已知的间歇失败。
- **需要宿主的测试**设 `MABITOOL_API_PORT=0`，避免端口冲突。同一时间只运行一个桌面前端的 `dotnet test`。
- **测试内容**：验证用户行为、故障恢复、兼容与契约；外观与布局微调不加测试，不放宽已有断言。
- **TypeScript 契约测试**：

  ```powershell
  npm ci --prefix tests/Contracts/TypeScript
  npm test --prefix tests/Contracts/TypeScript
  ```

- **Python 测试**：染色引擎 `python -m unittest discover -s workers/color-engine/tests`；仓库脚本 `python -m unittest tests.EndToEnd.Repository.test_source_baselines`。
- **夹具**：`contracts/fixtures` 与各测试目录下的协议夹具来自合成数据或脱敏后的台服实采，不含真实账号、角色名或公会信息。新增实采夹具同样先脱敏。

## 6. 接口契约与客户端

- 公共契约定义在 `src/Contracts`，构建时单向生成 OpenAPI、JSON Schema 与 C#／TypeScript SDK；前端只依赖生成的契约，不引用模块私有实现。
- 所有端点在 `/api/v1` 下，需要认证；`GET /api/v1/openapi.json` 给出完整接口。集合返回 `items/nextCursor/snapshotRevision`，错误返回统一的 `ApiProblem`（稳定的 `code`、`messageKey`、`traceId`、`details`、`retryable`）。
- **原生客户端认证**：读取工具根下的 `runtime/instance.json`，经当前用户专用的命名管道握手取得令牌（`ModuleSdk` 的 `LocalBootstrapClient.ConnectAsync`），之后以 `Authorization: Bearer <token>` 调用。
- **浏览器客户端**：由桌面端发起配对，浏览器用一次性配对码兑换只绑定该 Origin 的令牌。
- **状态恢复**：客户端按 epoch、快照与 SSE 游标、能力、来源／质量和可用动作恢复，不在前端另建业务状态机。异步写操作带幂等键，需要修订的请求带 `If-Match`。
- 接入示例见 `frontends/examples`（Node 与 .NET）。

## 7. 新增模块与 Provider

1. 先确定所有权：用户能力、实体与表、公开服务、输入或原生观察需求、查询／命令／事件、启停与不可用条件。
2. 模块通过 ModuleSdk 声明版本、依赖与工厂；数据库迁移以 `DatabaseMigration(moduleId, version, sql)` 登记，版本从 1 连续递增，表名使用模块前缀。
3. 公共窗口、输入、调度、快捷键、存储、更新与诊断已有所有者，模块通过接口协作。游戏写操作遵守统一活动租约，紧急停止优先。
4. 能力缺失时返回明确的不可用原因，前端据此停用入口并写明原因，不用假数据。
5. 公开行为变化同步更新 `src/Contracts`，契约测试覆盖新字段。

第三方扩展（纯数据或带程序）的制作见[扩展模块开发](extensions.md)，素材包见[素材包制作](asset-packs.md)。

## 8. 打包与发布

发行物是解压即用的 ZIP，发布到 GitHub Releases，不做安装程序与代码签名。正式发行包含染色组件：

```powershell
# 正式渠道（发行用）
pwsh -NoProfile -File .\scripts\build\publish-backend.ps1 -Version 0.1.0 -Channel stable -IncludeObservation -IncludeColor `
  -PythonExecutable <Windows x64 Python 3.12> -TesseractDirectory <Tesseract 5.4.0 安装目录> `
  -TesseractLicenseDirectory workers/color-engine/tesseract-licenses
# 不含染色组件（本机试用）
pwsh -NoProfile -File .\scripts\build\publish-backend.ps1 -Version 0.1.0 -Channel stable -IncludeObservation
```

`-Channel` 省略时为 `preview`（预览渠道）：包名、`Moongate.exe` 的产品版本与前端版本都带 `-preview`，设置页版本后标注“预览版”；预览渠道的应用内更新会同时收到预发布与正式发行，正式渠道只收到正式发行。

- 需要与 `-RuntimeVersion`（默认 10.0.13）一致的本机 .NET 与 ASP.NET Core 运行时，打包时复制进包内。
- 先在 `artifacts/packages/<ReleaseId>/` 生成目录包，再由 `scripts/build/package-archive.ps1` 打成 `artifacts/packages/Moongate-v<版本>-Windows-x64.zip` 与同名 `.sha256`（preview 渠道为 `Moongate-v<版本>-preview-Windows-x64.zip`）。
- ZIP 解压后是单个 `Moongate` 文件夹：`Moongate.exe`（入口，源码在 `src/PortableLauncher`）、`components\`（各组件与随包运行时）、`licenses\`（第三方声明与 SPDX 清单）；首次运行生成 `userdata\`，保存全部数据、设置与版面。`components\core\launch-backend.cmd -Wait` 只启动后端并显示输出，用于排查启动失败。
- `components\release.json` 是发行身份：版本、渠道、检查更新的仓库、入口与各组件的树哈希；宿主据此得知自己的版本，应用内更新用它核对下载包。
- 每个版本在 GitHub Releases 发布 ZIP 与同名 `.sha256`，标签为 `v<版本>`，标题为 `Moongate <版本>`（预览版带 `-preview` 并标为预发布）；应用内更新先直连 github.com 取最新版本（不通时改用 GitHub API），有对应本机版本的增量包时只下载变化的文件，否则下载整包；核对 SHA-256、文件清单、压缩包结构与发行身份后由 `Moongate.exe` 替换程序文件，失败时回退并恢复数据库。
- **增量资产（0.1.2 起）。** 打包时加 `-UpdateBase <上一版完整包 zip 或其清单>`（可给多个），在完整包旁生成文件清单 `<完整包名>.manifest.json` 与增量包 `<完整包名>.update-from-<旧版本>.zip`；增量包只含新版里内容变化或新增的文件，打成单个文件。已有完整包时也可单独运行 `python scripts/build/package-update-assets.py --archive <新完整包> --base <旧完整包或清单>`。清单与增量包都作为附加资产上传。
- **资产命名与排序。** GitHub Release 按文件名排序列出资产，完整包必须排在第一个，用户不滚动就能看到该下载的文件：

  | 资产 | 文件名 | 示例 |
  |---|---|---|
  | 完整包 | `Moongate-v<版本>-Windows-x64.zip` | `Moongate-v0.1.1-Windows-x64.zip` |
  | 校验值 | `Moongate-v<版本>-Windows-x64.zip.sha256` | `Moongate-v0.1.1-Windows-x64.zip.sha256` |
  | 其他资产（增量包、可选组件、调试符号等） | `Moongate-v<版本>-Windows-x64.zip.<小写字母开头的用途>…` | `Moongate-v0.1.1-Windows-x64.zip.update-from-0.1.0.zip` |

  校验值与其他资产都以完整包的文件名为前缀，前缀在任何字符串比较（区分或不区分大小写、按区域规则）里都排在更长的名字前面，所以完整包总在第一个。发布脚本 `-AdditionalAssets` 接受其他资产，按这条规则核对文件名，并按序数、不区分大小写与区域规则各排序一次确认完整包在第一个，否则拒绝发布。应用内更新按这套命名拼出完整包、校验值与清单的下载地址，增量包的文件名写在清单里；0.1.0、0.1.1 只按完整包与校验值的确切文件名取用资产，附加资产不影响它们更新。
- **发布说明。** 每个版本在 `docs/` 下有 `release-notes-<版本>.md`（简体）与 `release-notes-<版本>.zh-TW.md`（繁体），两份开头各有一段版本提示与下载指引。Release 正文依次放简体与繁体的这段开头，再放简体全文与繁体全文。
- Tesseract 的版本、来源与文件哈希按 `workers/color-engine/tesseract-lock.json` 核对，各组件的许可文字在 `workers/color-engine/tesseract-licenses/`；不符或缺失时打包在发布 .NET 组件之前停止。
- 染色组件按引擎实际用到的模块瘦身：`scripts/build/package-color-runtime.py` 的 `SLIM_RULES` 逐条写明删除的文件与理由，删除后启动随包 Python 导入引擎全部模块并执行延迟导入，失败即停止打包；`tesseract.exe` 与 `libtesseract-5.dll` 移除调试信息，处理后哈希固定在锁定清单，识别输出须与原程序一致。
- 发行物不带 `.pdb` 调试符号。

## 9. 目录与生成物约定

- 源码、测试、资源和脚本放进对应职责目录；新的顶层目录需要明确用途。
- 构建输出、生成契约、测试结果、临时文件与安装包都在 `artifacts/`（已忽略），`data/` 用于本机运行数据。
- 启用提交前钩子后，每次提交会预览并回收可再生的生成物，并检查 `artifacts/` 的数量预算：

  ```powershell
  git config core.hooksPath scripts/hooks
  python scripts/maintenance/clean-artifacts.py          # 预览
  python scripts/maintenance/clean-artifacts.py --apply  # 回收，跳过 2 小时内的写入
  ```

- `scripts/maintenance/source-baselines.json` 登记移植来源的固定提交、文件哈希与功能映射，`python scripts/maintenance/check-source-baselines.py` 检查其结构；带 `--source <ID>=<检出目录>` 时核对对应上游仓库的固定 Git 对象。

## 10. 来源与许可

本项目以 MIT 发布。MobiFolio（MIT，란님）的乐谱规则与演奏流程，以及作者本人 MabiSync、SyncCue、Color Studio、MabiAgent 的代码以 MIT 并入；来源仓库、固定提交、源文件哈希与功能映射登记在 `scripts/maintenance/source-baselines.json`。依赖与随包组件见[第三方声明](../THIRD_PARTY_NOTICES.md)。
