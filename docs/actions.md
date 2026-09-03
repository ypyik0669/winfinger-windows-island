# 动作目录（actions.json）

剪贴板每条记录右键菜单里的扩展动作、卡片上的内联图标按钮，都来自同一份"动作目录"。内置在
`src/WinFinger/Resources/actions.json`（编译进程序集），首次启动时会把这份内置内容复制一份到
`%APPDATA%\WinFinger\actions.json`，之后你改的是这个用户文件，改动 500ms 内热重载生效，不用重启。

托盘 → **功能设置…** → 动作区域可以：打开这个文件、手动重新加载、一键恢复默认（会覆盖你的自定义内容）。
右键菜单最下面的"自定义动作…"也能直接定位到这个文件。

## 一条动作长什么样

```json
{
  "id": "open-url",
  "title": "打开链接",
  "icon": "E71B",
  "match": { "types": ["url"] },
  "run": "open:{text}",
  "inline": true,
  "order": 10
}
```

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | string，必填 | 唯一标识；用户文件里写同 `id` 会覆盖内置动作，新 `id` 则追加一条 |
| `title` | string | 菜单/按钮上显示的文字 |
| `icon` | string，可选 | 4 位十六进制 Segoe Fluent 图标码（如 `"E71B"`），或直接一个 emoji / 文字 |
| `match` | object，可选 | 匹配条件，见下；不写等于"任何条目都显示" |
| `run` | string，必填 | 执行方式，`前缀:载荷` 形式，见下 |
| `inline` | bool | 是否在卡片上直接露出图标按钮（一条记录最多显示 3 个内联按钮） |
| `order` | int，默认 100 | 排序用；内联动作总是排在非内联动作前面，组内按 `order` 升序 |
| `hidden` | bool | 只在**用户文件**里有意义：对某个内置 `id` 写 `hidden: true` 即可把它移除，不需要复制整条定义 |

`id` / `run` 为空的条目会被直接丢弃（不加载、不报错）。

## 匹配规则（match）

```json
"match": {
  "types": ["url", "email"],
  "kinds": ["text"],
  "regex": "^https://github\\.com/",
  "apps": ["chrome", "微信"]
}
```

- `types`：内容类型（`ContentDetector` 识别出的：`url` `email` `phone` `color` `json` `timestamp`
  `date` `path` `markdown` `code`），针对**文本**条目。
- `kinds`：条目类别 `text` / `image` / `file` / `ocr`（`ocr` 特指"已经识别出文字的图片"）。
- `types` 与 `kinds` 同时出现时是"或"的关系——条目命中任意一边就算匹配这部分。都不写则不限制类别。
- `regex`：对正文（`Text` 或没有 `Text` 时的 `OcrText`）做正则匹配，忽略大小写，100ms 超时保护；正则写错（无法编译）时这条动作永远不会显示，不会报错炸掉整个文件。
- `apps`：来源应用，跟条目的来源进程名 / 应用显示名做不区分大小写比较（`.exe` 后缀会被自动尝试去掉/加上）。
- 以上四项之间是"与"的关系：都得满足。全部省略 = 对所有条目都显示。

## run：四种执行方式

`run` 的格式固定是 `前缀:载荷`，只认下面四个前缀，其他前缀或缺冒号会被当成配置错误（不加载）：

| 前缀 | 含义 | 例子 |
|---|---|---|
| `open:` | 用系统默认程序打开（URL / `mailto:` / 文件路径），走 `ShellExecute` | `open:{text}`、`open:mailto:{text}` |
| `shell:` | 启动一个进程，**不经过 cmd.exe** | `shell:code {text}` |
| `builtin:` | 调用内置能力，见下表 | `builtin:ocr` |
| `prompt:` | 把展开后的文本交给 AI 流式回答，结果进结果抽屉 | `prompt:把下面翻译成日文：\n\n{text}` |

### 占位符

`run` 里可以用这些占位符，执行时按当前条目展开：

| 占位符 | 展开成 |
|---|---|
| `{text}` | `entry.Text`，没有则退回 `entry.OcrText`，都没有是空串 |
| `{path}` | 图片文件路径，没有则退回第一个文件路径 |
| `{paths}` | 所有文件路径，用空格连接、每个都带双引号；只有一个文件（或图片）时等于给 `{path}` 加引号 |
| `{png}` | 与 `{path}` 相同（历史命名，图片场景语义更明确） |
| `{app}` | 来源应用显示名 |

### `open:` 的注意事项

`open:` 直接把展开后的字符串交给 `ShellExecute`，**不会**帮你判断内容是不是真的能打开——务必配合 `match` 把动作限制在合适的内容类型上（比如 `open-url` 只在 `types: ["url"]` 上出现，`send-mail` 只在 `types: ["email"]` 上出现）。`www.` 开头没有协议头的地址会被自动补上 `http://`。

### `shell:` 的安全模型（务必看这段）

`shell:` 模板会先按空白切分成"程序 + 参数列表"（双引号内的空白不切），**占位符在切分之后才展开**，然后直接
`CreateProcess`（`ProcessStartInfo.ArgumentList`），全程不经过 `cmd.exe` / `%ComSpec%`：

- 剪贴板内容永远只会落在**一个参数**里，不会被里面的 `&` `|` `;` `%VAR%` 断开或展开，也不需要你做任何转义。
- 单独一个 `{paths}` token 会展开成多个独立参数（多文件场景）；出现在其他 token 里的 `{paths}` 按整段字符串展开。
- 传进 shell 动作的文本会先截断到 8000 字符（`ActionExecutor.ShellTextLimit`），避免命令行过长。
- **千万不要把 `{text}` 当作"程序名"（模板的第一个 token）**，例如写 `shell:{text}`——剪贴板里恰好是一段文本时，会尝试把这段文本当可执行文件启动，几乎必然失败，但也没有任何理由这样配置。占位符应该只出现在参数位置。
- 真的需要 `cmd` 的批处理特性（管道、重定向、环境变量展开）可以自己显式写 `shell:cmd /c ...`，风险自负——这等于主动放弃了上面的隔离。

### `builtin:` 内置能力

| 名字 | 作用 |
|---|---|
| `ocr` | 对图片做 OCR，结果进结果抽屉 |
| `qr-decode` | 识别图片里的二维码/条码，链接类内容额外提供"打开"按钮 |
| `qr-encode` | 把文本生成二维码 PNG |
| `json-format` / `json-minify` | 格式化 / 压缩 JSON，结果可"替换回条目" |
| `timestamp` | 把 10/13 位时间戳转换成可读时间 |
| `word-count` | 字数统计 |
| `copy-digits` | 只保留数字后复制（配合电话号码） |
| `color` | 解析颜色文本，展示 Hex / RGB / HSL 三种格式 |
| `open-path` | 用默认程序打开路径类文本/文件条目 |
| `pin` | 把图片悬浮钉在桌面上（`PinnedImageWindow`） |
| `ai-translate` | 按功能设置里的目标语言调用 AI 翻译（含 CJK 检测的"自动"档位） |

`builtin:` 之外的名字会在执行时提示"未知的内置动作"，但不影响其他动作加载。

## 合并 / hidden / 热重载

- 加载顺序：先读嵌入的内置 `actions.json`，再读用户文件，按 `id` 覆盖（用户版本整条替换内置版本，不是字段合并），用户文件里的新 `id` 追加进列表。
- 用户文件里某条写 `"hidden": true`，最终目录里就没有这条（不管它原本是内置的还是用户自己加的）。
- 合并结果按"内联优先，同组内 `order` 升序"排序。
- `%APPDATA%\WinFinger\actions.json` 有文件系统监听，改动后 500ms 去抖自动重新加载；文件被其他进程占用时会重试（最多 5 次）。
- 用户文件解析失败（JSON 语法错误）时**保留上一份可用目录**，并在功能设置页 / 通知里提示大致出错的行号，不会因为一次手滑打字错误导致所有动作消失。
- 支持 JSON 注释（`//`、`/* */`）和结尾多余逗号（宽松解析），手改更方便。

## 五个可以直接抄的例子

追加到你的 `%APPDATA%\WinFinger\actions.json`（它是一个 JSON 数组，把下面对象加进去即可）：

**1. 用 VS Code 打开剪贴板里的路径**

```json
{
  "id": "open-in-vscode",
  "title": "用 VS Code 打开",
  "icon": "E943",
  "match": { "types": ["path"] },
  "run": "shell:code {text}",
  "inline": true,
  "order": 15
}
```

**2. 查询 IP 归属地（正则限定，只在纯 IP 上出现）**

```json
{
  "id": "ip-lookup",
  "title": "查询 IP 归属地",
  "icon": "E774",
  "match": {
    "kinds": ["text"],
    "regex": "^(\\d{1,3}\\.){3}\\d{1,3}$"
  },
  "run": "open:https://ipinfo.io/{text}",
  "inline": true,
  "order": 45
}
```

**3. 一键翻译成日文（AI prompt，不依赖功能设置里的目标语言）**

```json
{
  "id": "ai-to-japanese",
  "title": "翻译成日文",
  "icon": "✨",
  "match": { "kinds": ["text", "ocr"] },
  "run": "prompt:把下面的内容翻译成日文，只输出译文：\n\n{text}",
  "inline": false,
  "order": 115
}
```

**4. 只在微信来源的条目上显示的动作**

```json
{
  "id": "wechat-reply-tip",
  "title": "标记为待回复",
  "icon": "E734",
  "match": {
    "kinds": ["text"],
    "apps": ["微信", "wechat"]
  },
  "run": "builtin:word-count",
  "inline": false,
  "order": 200
}
```

**5. 隐藏内置的"统计字数"动作**

```json
{ "id": "word-count", "hidden": true }
```

只需要这一条最小定义，不用把内置的完整字段抄一遍——`id` 匹配上、`hidden: true`，这条内置动作就不会再出现。
