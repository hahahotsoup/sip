# 🍲 sip

> **English** | [**简体中文**](./README.md)

> ——"Savor it, sip it slow."
>
> **Reading is like a warm broth — don't keep staring at the bowl; close your eyes and enjoy the taste first.**

sip is a wall against information noise: letting you and the AI you care about see only the content you trust.

It is not an algorithm-driven reader, nor a feed to make you "scroll more," but a **local-first, transparent information filter and reading assistant** — you choose your sources, sip guards and helps curate them while improving the reading experience. You and your AI agents get answers from a clean, traceable dataset.

📖 **Full documentation lives in the [sip Wiki](https://sip.wenshenghe2009.workers.dev/)** — install, CLI/TUI, AI commands, privacy, and all other details are there.

Also, shameless plug: [https://blog.hotsouprealm.top/atom.xml](https://blog.hotsouprealm.top/atom.xml)
Follow the hot soup teahouse, follow the hot soup teahouse, thank you 🐾

---

## Quick start

Download the **single-file executable** from [Releases](https://github.com/hahahotsoup/sipintui/releases) and run it directly:

```bash
./sip.exe            # Windows: enter TUI (first launch auto-creates the readwithhotsoup/ data dir)
./sip.exe --help     # or use the CLI directly
```

- **Single file + built-in translations**: language files are embedded in the exe; just copy one exe and it runs
- **Framework dependency**: requires [.NET 10 runtime](https://dotnet.microsoft.com/download)
- **Data directory**: `readwithhotsoup/` (SQLite + file cache, fully local, copy to migrate)

Building from source, `publish.ps1` cross-platform packaging, and more: see [Wiki · Getting started](https://sip.wenshenghe2009.workers.dev/指南/快速开始.html).

---

## Core capabilities

| Module | One-liner |
|--------|-----------|
| 📚 **Smart archiving** | Version tracking, content diff, snapshot archiving, reading progress, source health |
| 📖 **Assisted reading** | TUI folder view, immersive reading, full-text fetch, Markdown rendering, today's hot soup |
| 🤖 **AI friendly** | Semantic search (RAG), LLM summaries, unified JSON, structured exit codes |
| 🕊️ **Privacy** | Local telemetry Sumenia off by default, stored locally only, never auto-uploaded |

> Full feature list and every detail: [Wiki · Features](https://sip.wenshenghe2009.workers.dev/功能/).

---

## Quick examples

```bash
sip -l                  # list sources
sip -d https://xxx/rss  # add an RSS source
sip -u 1                # update source 1
sip --show 12           # full-screen reading
sip --search "RAG"      # semantic search (run sip --init to configure AI first)
sip --today             # today's hot soup
```

Complete CLI flags, TUI shortcuts, AI commands, and error codes: [Wiki · Usage](https://sip.wenshenghe2009.workers.dev/使用说明/命令行.html).

---

## Why sip?

Today's information environment has three harsh truths:

1. **LLMs cite junk sources** — DeepSeek, Doubao, and ChatGPT casually cite Sohu, Baijiahao, and low-quality self-media, and you cannot tell them "don't use these."
2. **Algorithms put you in a bubble** — they keep you scrolling but never tell you "why this was recommended."
3. **The people you love lack the means to filter** — your parents and friends have no tools to protect themselves from clickbait and rumors.

sip does only two things — **deterministic rules** and **local storage of facts**; anything needing judgment, explanation, or dialogue is left to agents and users. It won't make you scroll more, but it will help you read with more peace of mind.

> **When you open sip, you know what you read today is trustworthy; when your AI calls sip, you know the sources it cites are reliable.**

May we meet again, none the worse for wear 🍲

---

## License

Licensed under the GNU General Public License v3.0 (GPL-3.0)
