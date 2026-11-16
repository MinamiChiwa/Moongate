# 扩展模块与素材包开发

[开发文档](development.md) · [返回首页](../README.md)

Moongate 0.1.0 支持两种第三方内容：

| 形式 | 用途 | 安装方式 |
|---|---|---|
| 素材包 | 替换模块图标、动效、页面背景、效果／乐器／清单图片 | “模块与扩展”页导入文件夹，或放在工具根的 `desktop\assets` |
| 纯数据扩展模块 | 以 ZIP 分发的图片、文字与数据，可携带一个素材包 | “模块与扩展”页安装 `.zip`，或放进 `data\extensions\inbox` |

带程序代码的扩展（新的后端能力、桌面控件）需要宿主提供能力代理，当前版本不接受安装。

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
| `provides`、`permissions`、`highRiskBehaviors` | 纯数据扩展必须为空数组；非空表示需要代码实现，安装时以 `extension.codeNotSupported` 拒绝 |
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
| `extension.codeNotSupported` | 包含代码或声明了需要代码的字段 |
| `extension.fileTypeRejected` | 文件类型不在允许列表 |
| `extension.versionConflict`、`extension.downgradeRejected` | 同版本内容不同或版本更低 |
| `extension.installDamaged` | 已安装内容缺失或与记录不符 |
| `extension.previewExpired` | 预览已过期，需要重新检查 |
