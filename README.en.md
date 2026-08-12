# 🍲 sip

> **English** | [**简体中文**](./README.md)

> ——"Savor it, sip it slow."
>
> **sip: your information, your history, your judgment.**

sip is a **local-first personal information hub**: it collects content from RSS and other sources, preserves it locally and tracks changes over time, and helps you take control of your information input through search, filtering, and agents.

It helps you do five things:

```
collect → preserve → track → filter → use
```

- **Collect**: RSS / RSSHub sources
- **Preserve**: full-text fetching, version snapshots
- **Track**: what the author changed (Version / Diff)
- **Filter**: Insights reports, Source Policy rules, cross-source dedup, high-frequency collapsing
- **Use**: full-text / semantic search, agents / bots, Markdown export

**AI helps you understand information, but never decides its value for you** — the judgment is always yours.

📖 **Full documentation lives in the [sip Wiki](https://sip.hotsouprealm.top/)** — install, CLI/TUI, AI commands, privacy, and all other details are there.

Also, shameless plug: [https://blog.hotsouprealm.top/atom.xml](https://blog.hotsouprealm.top/atom.xml)
Follow the hot soup teahouse, follow the hot soup teahouse, thank you 🐾

---

## Quick start

> ✅ **Thoroughly tested (v1.1.4, overall 8.4/10)**: functionality, security, performance, stability, concurrency, and fault injection all verified — see the [full test report](./sip-完整测试报告-2026-08-12-最终版.md). sip's data files are interoperable, open, standard formats (SQLite + plain-text JSON), so you can switch the software core at any time to migrate — you won't be locked in.

Download the **single-file executable** from [Releases](https://github.com/hahahotsoup/sipintui/releases) and run it directly:

```bash
./sip.exe            # Windows: enter TUI (first launch auto-creates the readwithhotsoup/ data dir)
./sip.exe --help     # or use the CLI directly
./sip.exe --version  # show the version
```

- **Single file + built-in translations**: language files are embedded in the exe; just copy one exe and it runs
- **Framework dependency**: requires [.NET 10 runtime](https://dotnet.microsoft.com/download)
- **Data directory**: `readwithhotsoup/` (SQLite + file cache, fully local, copy to migrate)

Building from source, `publish.ps1` cross-platform packaging, and more: see [Wiki · Getting started](https://sip.hotsouprealm.top/en/guide/quick-start.html).

---

## Core capabilities

Grouped by the five verbs, so you can see what each part of the loop does:

| Verb | What it carries |
|------|----------------|
| 📥 **Collect** | RSS / RSSHub sources, OPML import |
| 💾 **Preserve** | full-text fetch, version snapshots, reading progress |
| 🔎 **Track** | version history, content diff, "modified by author" |
| 🧹 **Filter** | Insights reports, Source Policy rules, cross-source dedup, high-frequency collapsing, today's change digest |
| 🚀 **Use** | full-text / semantic search, agents / bots, Markdown export |

> Before adding a feature, ask: which part of the loop does it belong to? — it defines the product boundary and keeps the concept from over-expanding. Every detail: [Wiki · Features](https://sip.hotsouprealm.top/en/features/).

> 🕊️ **Privacy**: local telemetry Sumenia is **off by default**, stored locally only, never auto-uploaded; when enabled, the `search` event records the **full query** (local only, removable via `telemetry export/clear`).

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

> 🔒 `sip --init` involves entering an API key — **run it manually in a real interactive terminal only** (security: pipe/script input is not accepted); AI cannot run it on your behalf, you must configure it yourself.

Complete CLI flags, TUI shortcuts, AI commands, and error codes: [Wiki · Usage](https://sip.hotsouprealm.top/en/usage/cli.html).

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

sip does only two things — **deterministic rules** and **local storage of facts**. Everything you care about (collect, preserve, track, filter, use) is built on top of these two; anything needing judgment, explanation, or dialogue is left to agents and users. It won't make you scroll more, but it will help you read with more peace of mind.

> **When you open sip, you know what you read today is trustworthy; when your AI calls sip, you know the sources it cites are reliable.**

May we meet again, none the worse for wear 🍲

---

## Roadmap: from an "RSS reader" toward a "personal information hub"

sip's core principle is **facts first** — it only does deterministic rules and local storage of facts; anything that needs judgment, explanation, or dialogue is left to the user and agents. The following plan follows this principle, preferring to go **slow rather than dress up judgment as fact**.

### Shipped: Reading Insights

- `sip --insights` shows **reading facts** per feed (opened / completed / completion rate / ♥🤖 likes / AI-call counts / subscribed & backlog) with **explainable reasons** (e.g. "opened 0 articles in 30 days", "finish rate 3%") — **the decision is yours**.
- Telemetry now attributes AI calls (summary / search / embedding) to **article and feed**, so the report can be aggregated per feed.
- Entry: CLI `--insights` / TUI report page (`P` key / `report` command); optional scheduled reminders via `--insights-interval`.

### Shipped: today's change digest (the diff showcase)

- The top of `sip --today` is a **change digest**: how many articles each feed added, ⚠ high-frequency feeds (fire-hose updates) folded separately, **articles edited by the author** with a change overview (title changed or not / lines added-removed / approx ±chars — pure diff counting, zero LLM), and ⚠ possible duplicates (cross-source repeats).
- Each "edited by author" item links to `sip --diff <id>` — making version-tracking's **diff capability the showcase** so "help me see what changed" actually lands.

### Shipped: cross-source dedup (cluster detection, user-confirmed)

- `sip --dedup scan` detects **possible duplicates** (paragraph overlap, cross-source, zero LLM) and outputs **duplicate clusters** (a group of same-article rows: a representative + members); `hide-cluster <representativeId>` hides the whole cluster in one shot (keeps the representative, hides the rest), marking `Status='dedup'` and recording the rule — after which they are **automatically excluded from search / fulltext / summary / counts**, and `--sync` skips re-importing them (no resurrection). Since v1.1.4 detection is cluster-based: however many duplicates, you only get a handful of clusters — **no pair explosion, no truncation**.
- **Hide ≠ delete**: data is preserved and recoverable (`--dedup undo`); press `i` in the manage view to review/undo hidden items.
- Detection is a fact, the decision is yours, the rule persists — the seed of Source Policy "remembering your decisions".

### Shipped: terminology/fact refactor + Source Policy closed loop

1. **Separate terminology from facts** — the report now separates "status" (technical failure only: fetch failed / long-untouched) from "activity" (behavior about *you*); **low reading ≠ low value**; removed value-judgment phrases like "consider unsubscribing", replaced with a `reasons` factual list.
2. **No black-box scoring** — no opaque `source_score = 0.72`; every `reasons` item is explainable (metric + value), leaving judgment to you.
3. **Source Policy closed loop** — `sip --policy` turns "your decision" into a persistent rule that is applied (`lower_frequency` changes the update schedule / `archive` / `tag` / `keep` / `unsubscribe` records a note); `-l` shows the rule marker. **`createdBy` is always `user`, never auto-written by AI.**

   ```
   read → analyze(Insights) → you confirm → source rule → adjust input
   ```

### Shipped: recommended source templates (Onboarding)

- `sip --onboarding` lists recommended feeds by domain (AI / Dev / Tech), `add <category> <index|all>` adds them in one click; `templates.json` is editable.

> These steps turn sip from an "RSS reader" into a "personal information hub" — Insights is the analysis layer, Source Policy makes "your decisions" persist, and onboarding lowers the first-use barrier.

### Engineering: project decomposition (single file → modules)

`sip` started as a single file `RssReader.cs`. `Sumenia.cs` (telemetry) and `Tui.cs` (view classes) were split out. **Decomposition stops here** — the rest stays single-file to keep maintenance simple.

---

## License

Licensed under the GNU General Public License v3.0 (GPL-3.0)
