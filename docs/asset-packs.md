# 素材包制作

[扩展模块开发](extensions.md) · [开发文档](development.md) · [返回首页](../README.md)

素材包用来替换 Moongate 的模块图标、图标动效、页面背景，以及效果、乐器与乐谱清单的图片。它只包含图片与一个清单文件，不含程序，换图不影响任何数据；移除素材包即回到内建外观。

## 1. 目录结构

素材包是一个文件夹，根目录放清单 `pack.json`，图片按相对路径放在文件夹里：

```text
my-pack/
  pack.json
  icons/combat.png
  icons/music.png
  bg/combat.webp
  effects/1201.png
  instruments/lute.png
  lists/favorites.png
```

## 2. `pack.json` 字段

```json
{
  "name": "我的素材包",
  "icons": {
    "module.combat-observation": { "image": "icons/combat.png", "hover": "spin", "selected": "pulse" },
    "module.music-library": { "image": "icons/music.png" },
    "module.color": { "hover": "shake" }
  },
  "backgrounds": { "combat-observation": { "image": "bg/combat.webp" } },
  "effects": { "1201": { "image": "effects/1201.png" } },
  "instruments": { "lute": { "image": "instruments/lute.png" } },
  "playlists": { "favorites": { "image": "lists/favorites.png" } }
}
```

| 字段 | 键 | 值 | 单张上限 |
|---|---|---|---|
| `name` | — | 素材包名称，显示在“模块与扩展”页；省略时用文件夹名 | — |
| `icons` | 图标槽名（第 3 节） | `image`（可选）、`hover`、`selected` | 2 MB |
| `backgrounds` | 页面的模块标识（第 4.1 节） | `image` | 8 MB |
| `effects` | 效果编号 | `image` | 2 MB |
| `instruments` | 乐器标识 | `image` | 2 MB |
| `playlists` | 乐谱清单标识 | `image` | 2 MB |

- `image` 是相对 `pack.json` 所在文件夹的路径，必须位于素材包文件夹内。
- 图片格式：PNG、JPEG、WebP、BMP（扩展名 `.png`、`.jpg`、`.jpeg`、`.webp`、`.bmp`）。
- 字段名不区分大小写，允许 `//` 注释与末尾逗号。

## 3. 图标

### 3.1 图标槽

| 槽名 | 界面位置 |
|---|---|
| `module.combat-observation` | 侧栏“DPS检测”入口 |
| `module.rooms` | 侧栏“合奏”入口 |
| `module.color` | 侧栏“染色”入口 |
| `module.inventory` | 侧栏“背包”入口 |
| `module.presentation` | 侧栏“浮窗版面”入口 |
| `module.music-library` | 侧栏“乐谱库”入口 |
| `module.shell.modules` | 侧栏“模块与扩展”入口 |
| `module.shell.settings` | 侧栏“设置”入口 |
| `module.shell.diagnostics` | 侧栏“诊断”入口 |
| `module.extension.<扩展标识>` | 侧栏“扩展”分组里该扩展的入口，例如 `module.extension.example.kill-timer`；内建为扩展名首字 |
| `module.<模块标识>` | “模块与扩展”页“已安装模块”列表里各模块的图标，例如 `module.core`、`module.game-access`、`module.synchronization`；没有内建线稿的显示字章 |
| `shell.language` | 主窗口的语言切换按钮 |
| `panel.combat.damage` | DPS检测页伤害卡片标题 |
| `panel.combat.self` | DPS检测页自身效果卡片标题 |
| `panel.combat.target` | DPS检测页怪物效果卡片标题 |
| `panel.combat.history` | DPS检测页战斗记录卡片标题 |

槽名规则：模块入口为 `module.<模块标识>`，面板标题为 `panel.<页面>.<面板>`，外壳入口为 `shell.<名称>`。

### 3.2 图片与动效

- 图标显示在圆角色块里，图片铺满整个色块并按色块裁出圆角。建议用正方形、128×128 像素以上的 PNG 或 WebP。
- 只写 `hover` 或 `selected`、不写 `image` 时，保留内建图标，只换动效。
- `hover` 是鼠标悬停时播放的动效，`selected` 是入口被选中时播放的动效。可选值：`none`（不播放）、`pop`（放大回弹）、`bounce`（弹跳）、`swing`（摆动）、`spin`（旋转一周）、`pulse`（脉动）、`float`（上浮）、`shake`（左右抖动）。不写或写了其他值时：`hover` 用该槽的内建动效，`selected` 用 `pop`。
- 在设置里关闭动效后，所有图标都不播放动效。

## 4. 其他素材

### 4.1 页面背景 `backgrounds`

键是页面所属的模块标识：`combat-observation`、`rooms`、`color`、`inventory`、`presentation`、`music-library`（同时用于迷你窗）、`shell.modules`、`shell.settings`、`shell.diagnostics`，扩展的页面为 `extension.<扩展标识>`。背景铺满窗口，工具按图片最暗与最亮的颜色和当前主题自动加一层遮罩，保证文字清楚。建议用与窗口比例相近、宽 1920 像素左右的 WebP 或 JPEG。

### 4.2 效果、乐器与清单图片

- `effects`：键为效果编号（游戏内效果的数字编号，可从扩展的 `combat.snapshot` 或本机 API 的战斗数据里读到），显示在 DPS检测页与浮窗的效果列表里。
- `instruments`：键为乐器标识（曲库里乐器的标识），显示在乐谱库与迷你窗的乐谱行上。
- `playlists`：键为乐谱清单标识，显示为乐谱库与迷你窗的清单封面。

没有对应图片的条目保持内建外观。浮窗元件的外框图片不在素材包里，在浮窗版面页逐个元件设置。

## 5. 随扩展分发

素材包可以打进纯数据扩展模块，方便分享与升级：

```text
fishing-skin.zip
  extension.json
  assets/pack.json
  assets/icons/combat.png
```

```json
{
  "schemaVersion": 1,
  "id": "example.fishing-skin",
  "version": "1.0.0",
  "hostApi": { "minimum": "1.0", "maximumExclusive": "2.0" },
  "displayNames": { "zh-Hans": "钓鱼主题", "zh-Hant": "釣魚主題" },
  "descriptions": { "zh-Hans": "钓鱼主题的图标与背景", "zh-Hant": "釣魚主題的圖示與背景" },
  "assetPack": "assets/pack.json"
}
```

- `assetPack` 指向包内 `pack.json` 的相对路径。`extension.json` 必须在 ZIP 根目录。
- 纯数据扩展只能包含 `.json`、`.txt`、`.md`、`.csv`、`.png`、`.jpg`、`.jpeg`、`.webp`、`.bmp`；ZIP 不超过 32 MiB、解压后不超过 64 MiB、最多 1,000 个文件。清单的其余规则与安装检查见[扩展模块开发](extensions.md#3-扩展包结构与清单)。
- 用户安装并启用扩展后，扩展卡片上出现“用作素材包”，按下即载入；扩展被停用或卸载时回到内建外观。

## 6. 导入、生效与撤销

| 方式 | 做法 |
|---|---|
| 导入文件夹 | “模块与扩展”页按“导入素材包”，选择含 `pack.json` 的文件夹。工具记住这个选择，下次启动自动载入 |
| 随扩展 | 安装并启用带素材包的扩展，按扩展卡片上的“用作素材包” |
| 默认位置 | 把素材包放在工具根的 `desktop\assets`（解压版为 `userdata\desktop\assets`），没有选择其他素材包时自动载入 |
| 调试 | 从源码运行桌面前端时加 `--assets <文件夹>`，只对本次运行有效 |

启动时依次尝试 `--assets`、界面里保存的选择、默认位置，载入第一个找到的素材包。同一时间只有一个素材包生效。按“移除素材包”回到内建外观。修改了素材包里的图片后，再按一次“导入素材包”选择同一文件夹即可重新载入。

## 7. 常见问题

**图片没有显示。** 导入后“模块与扩展”页会列出被跳过的条目，格式为“字段.键：原因”，例如 `icons.module.color：图片超过 2 MB`、`backgrounds.combat-observation：找不到图片 bg/combat.webp`。常见原因：

- 图片不在素材包文件夹内（路径里用了 `..` 或绝对路径）。
- 扩展名不是 PNG、JPEG、WebP、BMP 之一。
- 文件不存在，或路径大小写、斜杠写错。
- 超过大小上限（图标与其他图片 2 MB，背景 8 MB）。
- 图片已损坏，无法读取。

被跳过的条目不影响其他条目生效。

**提示“找不到 pack.json”或“pack.json 无法解析”。** 选择的文件夹根目录要有 `pack.json`，且是有效 JSON；整个素材包不会载入。

**槽名写错了。** 不在第 3.1 节表里的槽名不会报错，也不会显示在任何位置；请对照表格核对。

**扩展卡片上没有“用作素材包”。** 扩展需要先启用；清单里要有 `assetPack`。
