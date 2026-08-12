# 🍲 sip

> **English** | [**简体中文**](./README.md)

> ——"Savor it, sip it slow."
>
> **Reading is like a warm broth — don't keep staring at the bowl; close your eyes and enjoy the taste first.**

sip is a wall against information noise: letting you and the AI you care about see only the content you trust.

It is not an algorithm-driven reader, nor a feed to make you "scroll more," but a **local-first, transparent information filter and reading assistant** — you choose your sources, sip guards and helps curate them while improving the reading experience. You and your AI agents get answers from a clean, traceable dataset.

📖 **Full documentation lives in the [sip Wiki](https://sip.hotsouprealm.top/)** — install, CLI/TUI, AI commands, privacy, and all other details are there.

Also, shameless plug: [https://blog.hotsouprealm.top/atom.xml](https://blog.hotsouprealm.top/atom.xml)
Follow the hot soup teahouse, follow the hot soup teahouse, thank you 🐾

---

## Quick start

> ⚠️ **Newly added features are not yet fully tested**: sip's data files are interoperable, open, standard formats (SQLite + plain-text JSON), so you can switch the software core at any time to migrate — you won't be locked in.

Download the **single-file executable** from [Releases](https://github.com/hahahotsoup/sipintui/releases) and run it directly:

```bash
./sip.exe            # Windows: enter TUI (first launch auto-creates the readwithhotsoup/ data dir)
./sip.exe --help     # or use the CLI directly
```

- **Single file + built-in translations**: language files are embedded in the exe; just copy one exe and it runs
- **Framework dependency**: requires [.NET 10 runtime](https://dotnet.microsoft.com/download)
- **Data directory**: `readwithhotsoup/` (SQLite + file cache, fully local, copy to migrate)

Building from source, `publish.ps1` cross-platform packaging, and more: see [Wiki · Getting started](https://sip.hotsouprealm.top/指南/快速开始.html).

---

## Core capabilities

| Module | One-liner |
|--------|-----------|
| 📚 **Smart archiving** | Version tracking, content diff, snapshot archiving, reading progress, source health |
| 📖 **Assisted reading** | TUI folder view, immersive reading, full-text fetch, Markdown rendering, today's hot soup |
| 🤖 **AI friendly** | Semantic search (RAG), LLM summaries, unified JSON, structured exit codes |
| 🕊️ **Privacy** | Local telemetry Sumenia off by default, stored locally only, never auto-uploaded |

> Full feature list and every detail: [Wiki · Features](https://sip.hotsouprealm.top/功能/).

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

Complete CLI flags, TUI shortcuts, AI commands, and error codes: [Wiki · Usage](https://sip.hotsouprealm.top/使用说明/命令行.html).

---

## Connect QQ / WeChat / Discord / Telegram bots

Use a local agent (**OpenClaw** or **Cherry Studio**) to attach sip to messaging channels — mention it in a group, and it uses sip's search/summary abilities to answer only from sources you trust.

Feed the agent three things:

1. **Standalone `sip.exe`** (single-file from [Releases](https://github.com/hahahotsoup/sipintui/releases))
2. **The `sip-rss` skill** (`.opencode/skills/sip-rss/SKILL.md` or `sip-skill.zip`)
3. **A system prompt** (see Wiki, making the agent retrieve only via `sip --search/--grep/--show/--summary`)

> Telegram and Discord are natively supported by OpenClaw; QQ and WeChat need third-party bridges (OneBot/go-cqhttp, Wechaty, etc.). Full steps, prompt, and examples: [Wiki · Bot Integration](https://sip.hotsouprealm.top/en/usage/bot-integration.html).

---

## Concrete scenarios

### Scenario 1: A "guardrail" for your AI

Tired of your AI citing Sohu or Baijiahao? Make it search only your subscribed sources:

```bash
sip --search "LLM Agent survey" --json   # semantic search, returns trusted hits
sip --search "RAG" --feed 1 --json        # limit to a single source
```

The AI only sees content from your sources — no more junk citations.

### Scenario 2: Verify an article that was "edited"

Did the author quietly change their stance? See exactly how:

```bash
sip --versions 12        # list all historical versions of article 12
sip --diff 12 v1 v3      # diff the bodies of v1 vs v3
```

### Scenario 3: Read something worth reading every day

Don't want to hunt in a haystack? Let sip pick 5 articles first:

```bash
sip --today              # rule-based picks: new in 48h/edited/full-text/♥🤖 weighted
sip --today --json       # structured output for scripts or AI
```

### Scenario 4: Set up "safe reading" sources for your parents

After whitelisting (CCTV news, your local weather bureau, medical accounts you trust), all they need:

```bash
./sip.exe                # open and read; only see the sources you curated
```

Junk sources are filtered out — no need to judge true vs. false themselves.

### Scenario 5: Read long articles slowly

RSS summary too short? Fetch the full text:

```bash
sip --fulltext 12        # fetch the original of article 12 to local cache
sip --show 12            # full-screen reading
```

### Scenario 6: Find an article you read long ago

Can't recall the title, only a keyword? Full-text search without AI:

```bash
sip --grep "quantum entanglement"   # exact SQL LIKE match on title/body/summary
```

### Scenario 7: Ask a bot in your group

After wiring up OpenClaw / Cherry Studio, mention it in a group:

```
@bot Which updates are worth reading from the last two days?
@bot Find me articles about "LLM Agent"
```

The agent automatically runs `sip --today` / `sip --search`, answering only from sources you trust. See [Wiki · Bot Integration](https://sip.hotsouprealm.top/en/usage/bot-integration.html).

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

## Roadmap: from "information warehouse" toward "information ingestion management"

sip's core principle is **facts first** — it only does deterministic rules and local storage of facts; anything that needs judgment, explanation, or dialogue is left to the user and agents. The following plan follows this principle, preferring to go **slow rather than dress up judgment as fact**.

### Shipped: Reading Insights

- `sip --insights` shows **reading facts** per feed (opened / completed / completion rate / ♥🤖 likes / AI-call counts / subscribed & backlog) with **explainable reasons** (e.g. "opened 0 articles in 30 days", "finish rate 3%") — **the decision is yours**.
- Telemetry now attributes AI calls (summary / search / embedding) to **article and feed**, so the report can be aggregated per feed.
- Entry: CLI `--insights` / TUI report page (`P` key / `report` command); optional scheduled reminders via `--insights-interval`.

### Shipped: terminology/fact refactor + Source Policy closed loop

1. **Separate terminology from facts** — the report now separates "status" (technical failure only: fetch failed / long-untouched) from "activity" (behavior about *you*); **low reading ≠ low value**; removed value-judgment phrases like "consider unsubscribing", replaced with a `reasons` factual list.
2. **No black-box scoring** — no opaque `source_score = 0.72`; every `reasons` item is explainable (metric + value), leaving judgment to you.
3. **Source Policy closed loop** — `sip --policy` turns "your decision" into a persistent rule that is applied (`lower_frequency` changes the update schedule / `archive` / `tag` / `keep` / `unsubscribe` records a note); `-l` shows the rule marker. **`createdBy` is always `user`, never auto-written by AI.**

   ```
   read → analyze(Insights) → you confirm → source rule → adjust input
   ```

### Shipped: recommended source templates (Onboarding)

- `sip --onboarding` lists recommended feeds by domain (AI / Dev / Tech), `add <category> <index|all>` adds them in one click; `templates.json` is editable.

> These steps turn sip from an "information warehouse" into an "information ingestion management system" — Insights is the analysis layer, Source Policy makes "your decisions" persist, and onboarding lowers the first-use barrier.

### Engineering: project decomposition (single file → modules)

`sip` started as a single file `RssReader.cs`. `Sumenia.cs` (telemetry) and `Tui.cs` (view classes) were split out. **Decomposition stops here** — the rest stays single-file to keep maintenance simple.

---

## License

Licensed under the GNU General Public License v3.0 (GPL-3.0)
