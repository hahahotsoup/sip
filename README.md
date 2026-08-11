# 🍲 sip

> **简体中文** | [**English**](./README.en.md)

> ——「品，你细品。」
>
> **读文如喝汤，眼睛别总是往碗里瞟，闭上眼睛享受为先。**

sip 是一堵信息防火墙：让你和你关心的 AI，只看到你信任的内容。

它不是算法推荐阅读器，也不是让你"刷更多"的资讯流，而是一个**本地优先的透明信息过滤器和阅读辅助器**——你指定信源，sip 守护并辅助筛选信源，同时提升阅读体验。你和你的 AI 代理从一份干净、可追溯的数据中获取答案。

📖 **完整文档见 [sip Wiki](https://sip.wenshenghe2009.workers.dev/)** —— 安装、CLI/TUI、AI 命令、隐私等全部细节都在这里。

同时厚脸皮一下：[https://blog.hotsouprealm.top/atom.xml](https://blog.hotsouprealm.top/atom.xml)
关注热汤茶馆喵 关注热汤茶馆谢谢喵 🐾

---

## 快速开始

去 [Releases](https://github.com/hahahotsoup/sipintui/releases) 下载**单文件可执行程序**，直接运行：

```bash
./sip.exe            # Windows：进入 TUI（首次启动自动创建 readwithhotsoup/ 数据目录）
./sip.exe --help     # 或直接用 CLI
```

- **单文件 + 自带官方翻译**：语言文件内嵌 exe，只拷一个 exe 也能跑
- **框架依赖**：需预装 [.NET 10 运行时](https://dotnet.microsoft.com/download)
- **数据目录**：`readwithhotsoup/`（SQLite + 文件缓存，全本地，拷走即迁移）

从源码构建、`publish.ps1` 全平台打包等方法，见 [Wiki · 快速开始](https://sip.wenshenghe2009.workers.dev/指南/快速开始.html)。

---

## 核心能力

| 模块 | 一句话 |
|------|--------|
| 📚 **智能归档** | 版本追踪、内容 Diff、快照归档、阅读进度记忆、来源健康 |
| 📖 **辅助阅读** | TUI 文件夹视图、沉浸阅读、全文抓取、Markdown 渲染、今日哈汤 |
| 🤖 **AI 友好** | 语义搜索（RAG）、LLM 摘要、统一 JSON、结构化退出码 |
| 🕊️ **隐私** | 本地遥测苏暖泉（Sumenia）默认关闭、仅本地保存、绝不自动上传 |

> 完整功能清单与每项细节，见 [Wiki · 功能](https://sip.wenshenghe2009.workers.dev/功能/)。

---

## 快速上手示例

```bash
sip -l                  # 列出订阅源
sip -d https://xxx/rss  # 添加 RSS 源
sip -u 1                # 更新 1 号源
sip --show 12           # 全屏阅读
sip --search "RAG"      # 语义搜索（先 sip --init 配置 AI）
sip --today             # 今日哈汤
```

完整 CLI 参数、TUI 快捷键、AI 命令与错误码，见 [Wiki · 使用说明](https://sip.wenshenghe2009.workers.dev/使用说明/命令行.html)。

---

## 接入 QQ / 微信 / Discord / Telegram 机器人

用本地 Agent（**OpenClaw** 或 **Cherry Studio**）把 sip 挂到消息渠道，你在群里 @ 它，它用 sip 的检索/摘要能力，只从你信任的源回答。

把三样东西喂给 Agent 即可：

1. **独立 `sip.exe`**（[Releases](https://github.com/hahahotsoup/sipintui/releases) 下载单文件）
2. **`sip-rss` skill**（`.opencode/skills/sip-rss/SKILL.md` 或 `sip-skill.zip`）
3. **系统提示词**（见 Wiki，让 Agent 只用 `sip --search/--grep/--show/--summary` 检索）

> Telegram、Discord 由 OpenClaw 原生支持；QQ、微信需第三方桥接（OneBot/go-cqhttp、Wechaty 等）。完整步骤、提示词与示例见 [Wiki · Bot 接入](https://sip.wenshenghe2009.workers.dev/使用说明/Bot接入.html)。

---

## 具体场景

### 🛡️ 场景一：给 AI 装「护栏」，查资料只信白名单

AI 写东西时总引用搜狐、百家号？让它只查你订阅的源：

```bash
sip --search "LLM Agent 综述" --json     # 语义检索，返回可信命中
sip --search "RAG" --feed 1 --json        # 限定单一订阅源
```

AI 只会拿到你订阅源里的内容，从此告别垃圾引用。

### 🔍 场景二：验证一篇「被改过」的文章

作者偷偷改观点？看它怎么变的：

```bash
sip --versions 12        # 看 12 号文章的全部历史版本
sip --diff 12 v1 v3      # 对比 v1 与 v3 的正文差异
```

### 📰 场景三：每天读点值得读的（今日哈汤）

不想大海捞针，让 sip 先选 5 篇：

```bash
sip --today              # 规则式选文：近48h新增/被改过/全文质量/♥🤖 加权
sip --today --json       # 结构化输出给脚本或 AI
```

### 👵 场景四：给父母配置好「看得安心」的源

配好白名单（央视新闻、本地气象局、你信任的医学号）后，他们只需：

```bash
./sip.exe                # 打开即用，只见你筛过的源
```

垃圾源已被挡在外面，不用自己分辨真假。

### 📖 场景五：慢读长文

RSS 摘要太短，抓全文慢慢读：

```bash
sip --fulltext 12        # 抓取 12 号文章原文到本地缓存
sip --show 12            # 全屏阅读
```

### 🔎 场景六：找回很久以前看过的一篇

记不得标题，只记得关键词？不依赖 AI 的全文搜索：

```bash
sip --grep "量子纠缠"     # SQL LIKE 精确匹配标题/正文/摘要
```

### 🤖 场景七：让 bot 在群里帮你查

接 OpenClaw / Cherry Studio 后，群里 @ 它：

```
@bot 最近两天有什么值得读的更新？
@bot 帮我查一下"LLM Agent"相关文章
```

Agent 自动调 `sip --today` / `sip --search`，只从你信任的源回答。见 [Wiki · Bot 接入](https://sip.wenshenghe2009.workers.dev/使用说明/Bot接入.html)。

---

## 为什么有 sip？

现在的信息环境有三个残酷事实：

1. **大模型引用垃圾信源**——DeepSeek、豆包、ChatGPT 随手引用搜狐、百家号、低质自媒体，而你没法告诉它"不要用这些"。
2. **算法把你关进茧房**——让你不停刷，却从不告诉你"为什么推这个"。
3. **你最在乎的人缺乏分辨能力**——父母、朋友面对标题党和谣言，没有技术手段保护自己。

sip 只做两件事——**确定性的规则** 与 **本地事实的存储**；凡需要判断、解释、对话的，都交给 agent 与用户。它不会让你刷更多，但会让你读得更安心。

> **当你打开 sip 时，你知道你今天读到的东西是可信的；当你的 AI 调用 sip 时，你知道它引用的来源是可靠的。**

愿下次相见，别来无恙 🍲

---

## 许可证

遵循 GNU General Public License v3.0 (GPL-3.0)
