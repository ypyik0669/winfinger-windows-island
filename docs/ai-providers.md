# AI 接口配置

WinFinger 的 AI 功能（翻译 / 总结 / 润色 / 解释 / 提取要点，以及 `actions.json` 里 `prompt:` 类动作）
只认一种协议：OpenAI 兼容的 `POST {BaseUrl}/chat/completions`，用 `Authorization: Bearer {Key}`
鉴权，请求体里 `stream: true`（结果流式）。托盘 → **功能设置…** 里填三样：**BaseUrl**、**模型**、**API Key**，
再加一个超时秒数（5–300s，默认 60s）和翻译目标语言（默认"自动"：内容含中日韩字符就翻成英文，否则翻成中文）。

API Key 用 Windows DPAPI（`CurrentUser` scope）加密后存进 `settings.json`，界面上永远不回显明文，
只显示"已保存 ••••，留空不修改"；换电脑或换 Windows 账户后旧 Key 解不出来，需要重新填。

功能设置页有个"测试连接"按钮：发一次 `max_tokens: 1` 的极小请求，成功会显示返回的模型名和一小段回复，
失败会显示下面这套错误文案——排查时直接对照看。

## 错误文案对照

| 情况 | 界面文案 |
|---|---|
| 没填 Key 就触发动作 | "未配置 AI，请在托盘 → 功能设置 中填写 API Key"，带一个"打开功能设置"按钮 |
| HTTP 401 / 403 | "API Key 无效或无权限" |
| HTTP 404 | "模型或地址不存在（检查 BaseUrl/Model）" |
| HTTP 429 | "请求过于频繁或额度用尽" |
| 其他非 2xx | "请求失败 (HTTP {code}): {服务端返回的 error.message，截断到 200 字}" |
| 连不上 / DNS 失败 / TLS 错误 | "网络错误：{异常信息}" |
| 超过设置的超时秒数 | "请求超时（{N} s）" |
| 流式响应正常结束但没有任何内容 | "AI 没有返回内容" |

## 多轮对话的超时口径不一样

面板第六页「AI 对话」用同一套 BaseUrl / Key / 模型，但超时含义不同：单轮动作（翻译 / 总结…）是**整段墙钟超时**，
超过 `超时秒数` 就断；多轮对话是**空闲超时**——只要还在往回吐数据（包括 SSE 心跳、思维链增量）就一直等，
连续 `超时秒数` 没有任何新数据才判断连接中断，另有 900 秒的绝对上限兜底。所以一个要写三分钟的长回答不会被腰斩。

对话相关的三个设置在功能设置的「AI 对话」分区：对话模型（留空跟随上面的模型）、系统提示词（留空用内置的，
建会话时快照进该会话，之后改设置不影响旧对话）、每次带上的历史字符数（默认 6000，从最新往回收，超预算的旧消息整条丢弃）。

| 情况 | 界面文案 |
|---|---|
| 对话流中断（空闲超时） | "连接中断：{N} s 没有收到新数据" |
| 对话超过绝对上限 | "生成时间超过上限（900 s），已停止" |

BaseUrl 末尾的 `/` 会被自动去掉再拼 `/chat/completions`，两种写法都可以：`https://api.openai.com/v1`
或 `https://api.openai.com/v1/`。

---

## OpenAI

- **BaseUrl**：`https://api.openai.com/v1`（默认值，留空即等于这个）
- **模型**：`gpt-4o-mini`（默认，性价比高）、`gpt-4o`、或其他你账号能用的模型
- **Key**：`sk-...`，[platform.openai.com](https://platform.openai.com/api-keys) 创建
- 国内网络直连大概率会遇到"网络错误"，需要自备可用的网络环境

## DeepSeek

- **BaseUrl**：`https://api.deepseek.com/v1`（也接受不带 `/v1` 的 `https://api.deepseek.com`）
- **模型**：`deepseek-chat`（对应 DeepSeek-V3）、`deepseek-reasoner`（对应 R1，会返回推理过程，回复速度更慢）
- **Key**：[platform.deepseek.com](https://platform.deepseek.com/api_keys) 创建，格式同样是 `sk-...`
- 国内可直连，价格便宜，是最省心的默认选项之一

## 通义千问（DashScope 兼容模式）

阿里云百炼 / DashScope 提供了 OpenAI 兼容端点，不要用它原生的 DashScope SDK 端点：

- **BaseUrl**：`https://dashscope.aliyuncs.com/compatible-mode/v1`
- **模型**：`qwen-plus`（功能设置的模型下拉框里已经预置了这个）、`qwen-turbo`（更快更便宜）、`qwen-max`（更强）
- **Key**：[bailian.console.aliyun.com](https://bailian.console.aliyun.com/) 里创建的 API-KEY
- 国内直连，注意用的是"百炼"控制台发的 Key，不是旧版通义千问 App 的任何 Key

## Ollama（本地模型，完全离线）

Ollama 本身自带一个 OpenAI 兼容端点，不需要额外配置：

- **BaseUrl**：`http://localhost:11434/v1`（默认端口 11434；如果 Ollama 跑在局域网另一台机器上换成对应 IP）
- **模型**：本地已 `ollama pull` 过的模型名，如 `llama3`、`qwen2.5:7b`、`deepseek-r1:7b`（跟 `ollama list` 里的名字一致）
- **Key**：Ollama 不校验 Key，**必须填一个任意非空字符串**（如 `ollama` 或随便几个字符）——WinFinger 没有 Key
  就不会发起请求，留空会直接停在"未配置 AI"提示上
- 先确认 `ollama serve` 在跑（默认随 Ollama 应用启动），且模型已经拉取到本地；没拉过的模型第一次请求会很慢甚至超时
- 完全离线，是"AI 隐私"顾虑最低的选项——文本不出这台机器

## 其他 OneAPI / New API 类聚合网关

One-API、New-API、以及各类云厂商的"模型广场"网关，只要暴露的是标准 `/v1/chat/completions`：

- **BaseUrl**：网关文档给的地址，通常形如 `https://your-gateway.example.com/v1`
- **模型**：网关里配置的渠道名 / 模型别名，照抄网关后台给的名字
- **Key**：网关自己签发的令牌，不是上游厂商（OpenAI/DeepSeek 等）的原始 Key
- 网关如果只支持非流式响应，WinFinger 请求里带的 `stream: true` 得不到 SSE 增量时会表现为长时间无输出，
  最终要么超时（"请求超时"）要么一次性收到内容——如果发现回复"卡住不动"，先确认网关是否支持流式转发

---

## 排查思路

1. 先点"测试连接"，把上面的错误文案对照一遍，缩小是 Key / BaseUrl / 模型哪一环出的问题。
2. HTTP 404 几乎总是 BaseUrl 或模型名拼错（少了 `/v1`、多了斜杠、模型名跟服务商后台不一致）。
3. HTTP 401/403 是 Key 本身的问题（过期、被撤销、复制时带了多余空格）。
4. "网络错误" 且用的是国外服务商：先确认本机网络能不能直连该域名。
5. Ollama 场景下"未配置 AI"提示：说明 Key 字段是空的——填任意字符即可，Ollama 不会校验它。
