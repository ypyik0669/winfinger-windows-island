# WinFinger

一个 Windows 灵动岛效率工具 —— macOS 刘海工具 [MacFinger] 的 Windows 移植 + 灵动岛增强版。

屏幕顶部常驻一个 iOS 灵动岛风格的 Liquid Glass 玻璃胶囊，实时显示网速与内存；点击弹性展开为五页面板：**剪贴板历史 / 媒体控制 / 便利贴 / 快捷键 / 番茄钟**。

## 下载

前往 [Releases](../../releases) 下载最新的 `WinFinger.exe`（单文件，内置运行时，无需安装 .NET），双击即用。

## 功能

| 模块 | 说明 |
|---|---|
| 紧凑岛 | 与 mac 版一致：左侧正在播放（封面 + 频谱）或番茄倒计时，中间两行 `↑ 上传 / ↓ 下载`（每秒刷新，`12.3 KB/s` 格式），右侧内存环（不含可回收缓存，≥65% 橙 / ≥85% 红） |
| 剪贴板历史 | 事件驱动监听（WM_CLIPBOARDUPDATE），采集顺序 排除格式 → 文件 → 文本 → 图片（**文本优先于图片**，Excel 复制的单元格记成文本而不是图片），SHA256 去重命中即置顶，上限 100 条（收藏条目不占配额），超长文本自动截断（默认 512K 字符）并标注"已截断"，图片落盘 PNG；密码管理器等标了 `ExcludeClipboardContentFromMonitorProcessing` / `CanIncludeInClipboardHistory=0` 的内容不会被记录；搜索框支持关键词 + `type:xxx` 语法（如 `type:url`、`type:ocr`），`全部 / 文本 / 图像 / 文件 / 收藏` 筛选、条数统计；单击直接粘贴（可在功能设置关闭"选中即粘贴"改为仅复制）、Shift+单击仅复制、Ctrl+单击多选、方向键导航、右键菜单、编辑文本、星标收藏、删除；鼠标在图片缩略图停留 1 秒弹出大图预览，点击打开灯箱 |
| 智能识别与动作 | 复制文本时自动识别类型（链接 / 邮箱 / 电话 / 颜色 / JSON / 时间戳 / 路径 / Markdown / 代码），卡片上显示类型徽标；悬停内联按钮与右键菜单挂上对应动作（打开链接、发邮件、复制号码、颜色预览、格式化 / 压缩 JSON、时间戳转换、在资源管理器中显示等）；动作目录可通过 `%APPDATA%\WinFinger\actions.json` 自定义，详见 [docs/actions.md](docs/actions.md) |
| OCR 识字 | Windows 内置离线 OCR（`Windows.Media.Ocr`），图片条目右键"识别文字"或用截图识字快捷键；结果写回条目并可被搜索（`type:ocr`）；功能设置里可开启"新图自动识别" |
| 区域截图 | 快捷键冻结虚拟屏后逐显示器遮罩拖选（多屏 / 不同 DPI 下像素级 1:1），Enter 确认 / Esc 取消，截图自动进历史并写入剪贴板；截图识字变体截完立即 OCR 并把文字也复制走；因 `WDA_EXCLUDEFROMCAPTURE`，岛本身不会入镜 |
| 二维码 | 文本条目"生成二维码"得到可复制 / 保存的 PNG；图片条目"识别二维码"，识别出的链接可一键打开（ZXing.Net） |
| 悬浮钉图 | 图片条目右键「悬浮显示」把图片钉在桌面最前层：拖动移动、滚轮缩放、双击或 Esc 关闭；钉图时面板自动收起，方便对照着抄 |
| 结果抽屉 | OCR / 二维码 / JSON / 颜色 / 时间戳 / AI 的结果统一落在剪贴板页底部的抽屉里：复制、粘贴到前台窗口、替换原条目、追加为新条目、保存文件、打开链接，AI 结果还能接着翻译或换一个动作；Esc 关闭 |
| AI（翻译 · 总结 · 润色 · 解释 · 要点） | 右键菜单 AI 子菜单，流式输出到结果抽屉、可随时停止；内置翻译（自动判断中 / 英）、总结、润色、解释、提取要点；兼容任意 OpenAI 格式的 `/chat/completions` 接口（OpenAI / DeepSeek / 通义 / Ollama / OneAPI 类网关等），详见 [docs/ai-providers.md](docs/ai-providers.md) |
| 功能设置 | 托盘 → **功能设置…**：三个全局快捷键的捕获与冲突检测（先试注册成功才落盘）、OCR 语言选择与语言包安装入口、AI 的 BaseUrl / 模型 / 目标语言 / 超时 / API Key（DPAPI 加密）与连接测试、动作目录状态与"打开 / 重新加载 / 恢复默认" |
| 音乐 | 系统全局媒体会话（GSMTC）：封面 / 标题 / 艺术家 · 专辑 · 来源、进度条与时间、上一首 / 播放暂停（封面主色圆钮）/ 下一首，封面后有随播放呼吸的主色辉光；**歌词**：通过 lrclib.net 匹配（同步 LRC 逐行高亮、自动居中滚动），无歌词时回到大封面布局 |
| 便利贴 | 左侧「我的便签」列表（标题 + 正文预览，右键 置顶 / 删除），右侧编辑器（标题、正文、自动保存 300ms 去抖），Ctrl+N 新建 |
| 快捷键 | 跟着前台应用走：先尝试实时读取该应用的菜单栏快捷键（Win32 菜单 / UI Automation，菜单项翻译成中文），读不到时回退到内置词典（资源管理器/Chrome/Edge/VS Code/Word/Excel/微信/Terminal），无匹配显示 Windows 通用快捷键；页首显示应用图标、名称、读取状态与「实时 / 内置」徽标，可手动刷新 |
| 番茄钟 | 230px 进度环 + 倒计时 + 状态语，专注 5–90 分钟 / 休息 1–30 分钟步进器（到边界自动禁用），完成次数持久化累计；紧凑岛显示倒计时，到点岛内弹通知+提示音 |
| 岛内通知 | 复制捕获、番茄到点等事件触发胶囊“鼓起”通知条 3 秒 |
| 音频可视化 | 播放音乐时胶囊内 8 根频谱条实时跳动（WASAPI loopback + FFT） |
| 封面取色辉光 | 从专辑封面提取主色，岛体外圈随播放呼吸脉动的彩色辉光 |
| 悬停预展开 | 鼠标悬停胶囊轻微放大并露出歌名，点击才全展开 |
| Liquid Glass | 自制实时磨砂玻璃：抓取岛后方屏幕 → 降采样模糊 → 饱和度 ×1.6 增强 → 岛底层渲染，背景颜色鲜艳透过玻璃（Windows acrylic 只会出灰，故弃用）；玻璃 rim 折射描边、呼吸游走边缘光、色散棱边、顶部弧面反光、展开流光扫过、播放音乐时封面主色渗入 |
| 幽灵模式 | 鼠标远离时岛体淡化至 40% 且点击穿透（不挡浏览器标签页），靠近自动实体化 |
| 收起位置 | **顶部**：贴屏幕上沿居中，上方两角衔接屏幕边缘；**悬浮**：按住胶囊或展开面板顶部空白拖到任意位置（可跨屏），拖到任一屏幕上边框 16px 内自动吸附回顶部；位置重启后保留 |
| 面板锁定 / 缩放 | 展开面板右上角小锁：锁上后点外面不收起；按住面板左下 / 右下角等比缩放（560 ~ 920），记住上次大小 |
| 外观 | 托盘「外观」：**纯黑**（始终深色）/ **Liquid Glass**（跟随系统浅色 / 深色，整块面板、侧栏、文字、按钮一起切换） |

## 交互

- **点击胶囊** 展开 / **Esc** 或 **点击面板外** 收起（锁上后点外面不收起）
- 展开后左上角 **WinFinger** 可收起；顶部标签和锁之间的空白：按住可以拖出或移动面板，没锁时轻点会收起
- **Ctrl+1..5** 切换页面（剪贴板/音乐/便利贴/快捷键/番茄钟），便利贴页 **Ctrl+N** 新建
- 点内存环可打开任务管理器
- 托盘图标：打开、外观（纯黑 / Liquid Glass / 外观设置…）、**功能设置…**、收起位置（顶部 / 悬浮）、暂停剪贴板、清空历史、岛背景、开机自启、退出
- 托盘 → **外观设置…**：岛背景三模式（动态玻璃 / 任意纯色 / 自定义图片），HSV 取色器 + Hex 输入、图片暗化、玻璃暗度/饱和度、光效开关，全部实时预览并记忆；纯色/图片模式下取景完全停止，零额外开销
- 托盘 → **功能设置…**：三个全局快捷键、OCR 语言、AI 接口与 Key、动作目录，见下方"默认快捷键"与功能表
- 无任务栏图标、不出现在 Alt-Tab

### 剪贴板页交互

- **鼠标**：单击条目直接粘贴到之前的前台窗口（"选中即粘贴"，可在功能设置关闭改为仅复制）；**Shift+单击** 仅复制、不收起面板；**Ctrl+单击** 多选（配合下面的批量粘贴/复制）；右键菜单含粘贴 / 仅复制 / 粘贴为纯文本 / 收藏 / 编辑文本… / 打开所在文件夹 / 复制路径 / 图片另存为… / 扩展动作 / 删除
- **键盘**（焦点在搜索框或列表均可用）：**↑ / ↓** 选择上一 / 下一条、**Enter** 粘贴（多选则按列表顺序批量粘贴，文本用换行拼接）、**Ctrl+Enter** 仅复制不粘贴、**Ctrl+Shift+Enter** 复制当前项为纯文本、**Delete** 删除选中、**Ctrl+F / Ctrl+D** 切换收藏、**Ctrl+Shift+C** 复制当前项为纯文本、**Space** 勾选/取消勾选多选、**Esc** 依次收起编辑框 → 结果抽屉 → 清空搜索 → 取消清空确认
- 图片缩略图悬停 1 秒弹出预览，点击打开应用内灯箱（滚轮缩放 / 拖拽平移 / Esc 或点击关闭）

### 默认快捷键

| 快捷键 | 功能 | 可在功能设置修改 |
|---|---|---|
| `Ctrl+Shift+V` | 唤起 / 收起面板 | 是 |
| `Ctrl+Shift+A` | 区域截图 | 是 |
| `Ctrl+Shift+T` | 截图并识字 | 是 |
| `Ctrl+1..5` | 展开状态下切换五个页面 | 否（固定） |
| `Ctrl+N` | 便利贴页新建 | 否（固定） |

> **隐私**：OCR 与二维码识别完全离线，不联网、不上传任何内容；AI 功能只在你主动触发翻译 / 总结等动作时，把选中的文本发送到功能设置里配置的接口地址（默认 OpenAI，可换成本地部署的 Ollama 等），不会主动上传其他数据；API Key 用 Windows DPAPI（CurrentUser）加密后落盘，只有同一台电脑同一个 Windows 账户能解密。
>
> **OCR 语言包**：需要系统安装对应语言包才能识别——设置 → 时间和语言 → 语言 → 添加语言并勾选"光学字符识别"；未安装时功能设置页和识别动作会提示，并提供跳转入口。

> 与 mac 版（MacFinger 1.1.0）功能对齐：五页内容、剪贴板搜索/文件/收藏/悬停预览、歌词、番茄钟环、快捷键实时读取、顶部/悬浮、面板锁定与缩放、纯黑/Liquid Glass 浅深色。Windows 独有的增强（自制实时玻璃、幽灵模式、岛内通知、频谱、悬停预展开、外观设置面板）全部保留。

## 开发

```bash
dotnet build src/WinFinger -c Debug     # 构建（当前 0 警告）
dotnet run   --project src/WinFinger    # 运行（单实例互斥，先关掉已运行的进程）
dotnet test  tests/WinFinger.Tests      # 单元测试（176 个）
Scripts/publish.cmd                     # 单文件发布
```

调试用环境变量（正式使用不需要）：

| 变量 | 作用 |
|---|---|
| `WINFINGER_CAPTURABLE=1` | 关掉 `WDA_EXCLUDEFROMCAPTURE`，让截图 / 录屏能拍到岛（自动化测试必开） |
| `WINFINGER_AUTOEXPAND=1..5` | 启动即展开到第 N 页 |
| `WINFINGER_OPENSETTINGS=1` | 启动 1 秒后打开功能设置窗口 |
| `WINFINGER_TRAYMENU=1` | 启动 1.5 秒后在鼠标处弹出托盘菜单（托盘图标常在溢出区，点不到） |
| `WINFINGER_FORCE_LIGHT=1` | 强制浅色主题 |
| `WINFINGER_GLASS_DUMP=1` | 导出一帧玻璃取景位图，排查模糊管线 |

## 技术栈

WPF / .NET 8（`net8.0-windows10.0.19041.0`，内置 CsWinRT 投影调用 WinRT 媒体 API 与 `Windows.Media.Ocr`），MVVM（CommunityToolkit.Mvvm），托盘用 Hardcodet.NotifyIcon.Wpf，二维码用 ZXing.Net。Per-Monitor V2 DPI。

窗口方案：透明无边框置顶窗口固定为最大尺寸，仅对内部 Border 的宽/高/圆角做 Storyboard 动画（展开 `BackEase` 弹性 280ms，收起 `CubicEase` 180ms），透明像素天然点击穿透。

磨砂模糊方案：Windows 自带 acrylic（DWM SystemBackdrop）会把背景色几乎全部去饱和成灰，达不到 liquid glass「背景颜色鲜艳透过玻璃」的效果，因此玻璃是自制的——12.5fps 用 GDI `StretchBlt` 把岛后方屏幕区域直接降采样进 128×84 DIB（降采样即预模糊），两趟盒式模糊 + 饱和度 ×1.6 + 提亮后写入 `WriteableBitmap`，作为岛底层 `ImageBrush` 渲染。岛窗口设 `WDA_EXCLUDEFROMCAPTURE` 防止抓到自己形成反馈循环。

> 注意：因 `WDA_EXCLUDEFROMCAPTURE`，**灵动岛不会出现在截图/录屏/屏幕共享里**（与 DRM 保护窗口同机制）。想截含岛的图请用手机拍摄。

> 已知裁剪：不接管系统 Toast 通知——WinRT `UserNotificationListener` 需要 MSIX package identity，unpackaged exe 不可用；岛内通知仅承载应用自有事件。

## 构建

需要 .NET 8 SDK：

```bash
dotnet build                # 调试
dotnet run --project src/WinFinger
```

发布单文件（约 75MB，含运行时，无需安装 .NET）：

```bash
dotnet publish src/WinFinger/WinFinger.csproj -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true
```

产物：`src/WinFinger/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/WinFinger.exe`

## 本地数据

```
%APPDATA%\WinFinger\
├── clipboard.json      # 剪贴板元数据（字段与 mac 版兼容，含 filePaths / isFavorite）
├── notes.json          # 便利贴
├── settings.json       # 设置（AI Key 经 DPAPI 加密后存储，其余字段明文）
├── actions.json        # 用户自定义 / 覆盖的动作目录（首次启动写入内置副本，见 docs/actions.md）
└── ClipboardMedia\     # 剪贴板图片 PNG
```

## 环境要求

- Windows 10 1809+（Windows 11 最佳）
- 剪贴板可能包含敏感内容（密码等），历史以明文存本地磁盘（`clipboard.json`），请知悉；可随时暂停记录或清空。`settings.json` 中仅 AI API Key 做了 DPAPI 加密，其余设置项仍是明文
- OCR 需要系统安装对应语言包，二维码识别 / 生成完全离线；AI 功能需要自行配置一个可用的接口地址与 Key（见 [docs/ai-providers.md](docs/ai-providers.md)）

## 第三方声明

- **ZXing.Net** — 二维码生成与识别，[Apache License 2.0](https://github.com/micjahn/ZXing.Net/blob/master/LICENSE)
- **lrclib.net** — 歌词数据来源，公开 API，非代码依赖

## 许可证

仅供学习和个人使用。
