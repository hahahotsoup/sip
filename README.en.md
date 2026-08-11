# 🍲 sip

> **English** | [**简体中文**](./README.md)

> ——"Savor it, sip it slow."
>
> **Reading is like a warm broth — don't keep staring at the bowl; close your eyes and enjoy the taste first.**

sip is a wall against information noise: letting you and the AI you care about see only the content you trust.

It is not an algorithm-driven reader, nor a feed to make you "scroll more." It is a **local-first, transparent information filter and reading assistant** — you choose your sources, sip guards and helps curate them while improving the reading experience. You and your AI agents get answers from a clean, traceable dataset.

Also, shameless plug: [https://blog.hotsouprealm.top/atom.xml](https://blog.hotsouprealm.top/atom.xml)
Follow the hot soup teahouse, follow the hot soup teahouse, thank you 🐾

---

## Why sip?

Today's information environment has three harsh truths:

1. **LLMs cite junk sources** — DeepSeek, Doubao, and ChatGPT will casually cite Sohu, Baijiahao, and low-quality self-media, and you cannot even tell them "don't use these."
2. **Algorithms put you in a bubble** — Douyin/Toutiao keep you scrolling but never tell you "why this was recommended," let alone let you choose "what I don't want to see."
3. **The people you love lack the means to filter** — your parents and friends have no technical tools to protect themselves from clickbait and rumors.

sip's answer to all three is simple:

> **Stand tall, and still get your information read.**

---

## Core design principles

| Principle | Description |
|-----------|-------------|
| **Local-first** | Your data stays in your hands (SQLite + file cache). No account, no reading history uploaded |
| **Transparent decisions** | Only sources you subscribe to. No algorithm black box; your filter rules are the source list you maintain |
| **Version as fact** | What did an author change, and when? sip records everything so no history is ever lost |
| **AI reads a whitelist only** | AI summaries and semantic search are based only on sources you trust, eliminating low-quality citations |
| **Out-of-the-box, ultra-light** | A single-file exe, zero dependencies, ready on launch; AI capabilities called on demand, never pre-run |

---

## What concrete problems does it solve?

### 1. A "guardrail" for your AI

When you or your AI agent needs to research:

- Let the AI call `sip --search "xxx" --json`
- The AI only retrieves from your subscribed sources
- No more "the AI just cites Sohu and Baijiahao"

### 2. Making information changes "visible"

A normal RSS reader only tells you "there's a new article."

sip tells you:

- "This article was modified by the author on Aug 1"
- "Before the change it said this; after the change it says that" (`sip --diff 123 v1 v3`)
- "This blog changed key opinions 12 times over the past year"

**What you see is no longer a static page, but the evolution trajectory of information.**

### 3. Helping the people you care about escape overload

Set up a whitelist for your parents (e.g., CCTV news, your local weather bureau, medical accounts you trust).

After they open sip:

- They only see the sources you curated
- Articles with overly short summaries are automatically prompted for full-text fetch, to read slowly
- No need to judge true vs. false — junk sources are already filtered out

**hahahotsoup's note: I'm well aware the TUI has a high learning curve, so once this program matures, Avalonia is also on the roadmap.**

---

## Features

### 📚 Smart Archiving

- **Version tracking**: automatically detects every modification to an article, saving v1, v2, v3…
- **Content Diff**: `sip --diff 123 v1 v3` clearly shows changes
- **Snapshot archiving**: timestamp snapshots of a whole source, permanently preserving a complete state at a point in time
- **Reading progress memory**: return to where you left off after quitting the TUI
- **Source identity & health**: `sip --feed-info <id>` shows name/type/author/website/last updated/latest articles/status; `-l` auto-marks "⚠ long inactive" and "✗ failed N times"
- **Content quality markers**: `-l <id>` flags summary-only (`[摘要]`) or bodyless (`[无正文]`) articles; JSON output carries a `quality` field (`full` / `short` / `empty`)

### 📖 Assisted Reading

- **TUI folder view**: sources + articles in a tree that expands; keyboard-driven (Vim-style shortcuts)
- **Immersive reading mode**: hide all sidebars with one key and read the body full-screen
- **Full-text fetch**: when an RSS summary is too short, `sip --fulltext <id>` fetches the original text into a local cache (zero table changes)
- **Markdown rendering**: HTML auto-converts to Markdown; code blocks, lists, and links render perfectly
- **Today's hot soup**: `sip --today` gives today's 5 rule-based picks (new in last 48h / recently updated by author / full-text quality / ♥🤖 flag weighting, with estimated reading time and reasons), also shown on the start page — **guiding a daily small-reading habit first**; personalized ranking evolves after Sumenia accumulates enough behavioral data

### 🤖 AI Friendly

- **Full-featured CLI**: every operation is callable from the command line
- **Unified JSON output**: `--json` lets AI parse reliably without writing regex
- **Embedding semantic search**: vector-based retrieval, supporting local Ollama / OpenAI / DeepSeek
- **LLM summaries**: generated on demand, results cached and reused
- **Structured exit codes**: `0` success / `1` general / `2` network error / `3` resource not ready, ideal for scripting

---

## Planned (not yet implemented)

These are designed but **not in the current version** — no false advertising:

- 🔒 **Whitelist / blacklist filtering** (domain-level, keyword-level) + **filter logs**
- 🔒 **Cross-source article deduplication**: automatically detect the same content pushed by multiple sources
- 📖 **System TTS reading** (native Windows/macOS/Linux voices) + **author-audio-first** (detect RSS audio attachments, prioritize playing the original audio)
- 📖 **Sip Today personalization**: v1 is rule-based selection; after Sumenia accumulates enough behavioral data, evolve into personalized ranking with "why this was recommended"

---

## Telemetry & Privacy

sip has a built-in **local reading telemetry** (event layer) — her name is **Sumenia** (苏暖泉), a soft little girl who quietly gets to know how you read. The data is used to improve content filtering and recommendations in the future. Her boundaries are hard:

| Principle | Description |
|-----------|-------------|
| **Off by default** | `unset` (no choice) = nothing recorded; the first TUI launch asks once, defaulting to "I don't need it for now" |
| **Local only** | Stored in `readwithhotsoup/telemetry.db` (a separate DB, fully isolated from `rss.db`) |
| **Never auto-uploaded** | No upload logic at all; it can only be shared if you explicitly `telemetry export` |
| **Viewable** | `sip telemetry show` shows raw events (time/type/article/data) |
| **Disableable** | `sip telemetry disable` (stops recording, keeps history) |
| **Clearable** | `sip telemetry clear` (clears events, does not affect your toggle choice or other data) |
| **Exportable** | `sip telemetry export [file]` generates JSON; you decide whether to hand it to the developer |
| **Records facts, not profiles** | Only facts like "which article was opened/finished/skipped, AI call info"; no user preference inference |

**What is recorded** (low-frequency events; scrolling/keys are never recorded):
- `article_open` / `article_progress` (25/50/75/100% milestones) / `article_complete` / `article_skip` (actively leaving with progress <10%)
- `ai_call` (operation/provider/model/success/duration; **no prompt/response/full text/tokens**)
- `article_like` (`--like` marker, distinguishing `actor: user` / `actor: ai`)

**Safety design**: telemetry.db uses WAL mode and runs an integrity check at startup (with busy_timeout and retries, so concurrent launches aren't misjudged as corrupt); if truly corrupt, it renames the file to preserve the scene (`.corrupt-timestamp`) and auto-rebuilds, **never affecting rss.db or reading**; events are buffered in memory and written in batches (50 events or 5 seconds), and auto-degrades/disabled on consecutive write failures. **Non-interactive/agent scenarios (non-TTY, `--ignoresafeannouncement`) never ask and stay off.**

**Article markers** (`article_signals.json`, separate from telemetry): `sip --like <id>` user like (♥), `sip --like <id> --ai [reason]` AI judgment (🤖), `sip --likes` to view; visible in sidebar / `-l N` / JSON output.

---

## Quick Start

### 🍵 Direct download (recommended)

Download the latest **single-file executable** from [Releases](https://github.com/hahahotsoup/sipintui/releases) (no unzip, no a pile of DLLs):

| Platform | File |
|----------|------|
| Windows x64 | `sip-win-x64.exe` |
| Linux x64 | `sip-linux-x64` |
| macOS Intel / Apple Silicon | `sip-osx-x64` / `sip-osx-arm64` |

Then run it directly:

```bash
./sip.exe            # Windows: enter TUI (first launch auto-creates the readwithhotsoup/ data dir)
./sip.exe --help     # or use the CLI directly
```

- **Single file + built-in official translations**: language files are embedded in the exe and auto-restored when the data dir is missing; just copy one exe and it runs
- **Framework dependency**: requires [.NET 10 runtime](https://dotnet.microsoft.com/download) on the target machine (small footprint); for a no-runtime build, publish self-contained (see below)
- **Data directory**: created next to the exe on first run as `readwithhotsoup/` — SQLite DB `rss.db`, AI config, language files, full-text cache, reading progress, telemetry etc. **everything lives here**; back up/migrate by copying the whole folder

### Building from source (optional)

```bash
git clone https://github.com/hahahotsoup/sipintui.git
cd sipintui
dotnet publish -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugSymbols=false -o publish/win-x64
./publish/win-x64/sip.exe          # enter TUI (Windows)
./publish/win-x64/sip.exe --help   # or use the CLI directly
```

> **Language files are embedded**: official translations like `zh-CN.json` / `en-US.json` are baked into the exe — even if you copy only the single exe from the publish folder, the first launch (or whenever the data dir is missing) **auto-restores** the default language, and the UI is still Chinese. The external `languages/` folder in the publish directory is **for user-customized translations** (changes take effect immediately; the built-in copies never overwrite your edits).

Replace `-r win-x64` with your target platform. Common RIDs:

| Platform | RID |
|----------|-----|
| Windows x64 / ARM64 | `win-x64` / `win-arm64` |
| Linux x64 / ARM64 | `linux-x64` / `linux-arm64` |
| macOS Intel / Apple Silicon | `osx-x64` / `osx-arm64` |

> **No-runtime build (self-contained)**: to distribute to others "copy and run" without requiring the .NET runtime, change `--self-contained false` to `--self-contained true` and republish (roughly tens of MB, more standalone).

> **Publish all platforms at once**: run `powershell -ExecutionPolicy Bypass -File publish.ps1`, which produces one single-file executable for Windows x64 / Linux x64 / macOS Intel / macOS Apple Silicon, output to `publish/<platform>/`.

### ai skill

The repo's [.opencode/skills/sip-rss](https://github.com/hahahotsoup/sipintui/tree/main/.opencode/skills/sip-rss) contains a skill you can feed directly to an AI. You can also download `sip-skill.zip` from [Releases](https://github.com/hahahotsoup/sipintui/releases) (provided alongside each platform's single-file build).

---

## Usage

### TUI mode (launch with no arguments)

Run `sip` (no arguments) to enter the folder-style TUI. It first shows a **start screen** (slogan + Dashboard data panel); press Enter to enter, `Q` to exit.

The left side is a **tree view merging sources + articles**: sources are parent nodes (`▶`/`▼` expand/collapse); expanding shows all articles of that source, like browsing folders. **Sources are collapsed by default** (press `l`/`Enter`/`Space` to expand; sources you expand stay expanded after refresh). **Long article titles wrap automatically** (never truncated) for easy reading. Each article shows only the **latest version**; if an article was modified and has older versions, a **`✎`** marker appears to the right of the title — select it and press **`V`** to view all versions. When an article is selected, the right panel **renders the body in Markdown**.

| Action | Description |
|--------|-------------|
| `j` / `k` (or `↑` / `↓`) | Move up/down in the sidebar (long titles wrap) |
| `l` / `Enter` / `Space` | On a source: collapse/expand; on an article: jump to the body page |
| `←` | Return to the sidebar from the body pane |
| `b` (or `PageUp`) | Page up in the sidebar (`Space` in the sidebar only "opens") |
| `Space` / `PageDown` | Page down in the body pane (with saved progress, Space = jump back to last position) |
| `Ctrl+D` / `Ctrl+U` | Half page down / up in the body pane (vim habit) |
| `i` | Immersive reading: hide sidebar/status bar/status line, body fills the screen (press `i` again to restore) |
| `U` | Update the current source (same as CLI `-u`) |
| `F6` | Update all sources |
| `A` | Archive the current source (timestamped title, same as CLI `-a`) |
| `R` | Unarchive (same as CLI `-una`) |
| `X` | Delete the selected source / article (same as CLI `-r`) |
| `D` | Add a new source (same as CLI `-d`) |
| `S` | Semantic search (same as CLI `--search`) |
| `Y` | Generate a summary for the current article (same as CLI `--summary`) |
| `G` | Toggle "full body / article summary" |
| `V` | View article version/change history (only for articles with the `✎` marker; enter an ID to see an older body) |
| `M` (or command `manage`) | Open the "source management page": list all sources full-screen, `j/k` move, `u` update, `a` archive, `r` unarchive, `x` delete, `s` schedule, `d` add source |
| `C` | Collapse/expand the left sidebar |
| `H` | Shortcut help |
| `F2` | About page |
| `Esc` | Bring up the bottom command line; type a command and press `Enter` to run it, `Esc` again to close |
| `Ctrl+O` | Link navigation mode |
| `Q` | Quit |

> **Reading progress memory**: the scroll position of each article is remembered (stored in `readwithhotsoup/reading_progress.json`, no DB change) — when you reopen an article with saved progress, the bottom status line shows "▷ press Space to jump back"; press **`Space`** to quickly jump to your last position (with boundary checks, never jumping negative or past the end).

**Bottom command line**: press `Esc` to bring it up (hidden normally); you can type the same commands as the CLI, e.g.:

```
u 2             # update source 2
d https://xxx   # download and add a new source
a 2             # archive source 2
r 2             # unarchive source 2
s keyword       # semantic search
g keyword       # full-text search (no AI needed)
fetch           # fetch the current article's full text (needs consent phrase on first use; prompted when summary is too short)
manage          # open source management page (same as M key)
y               # generate summary for the selected article
init            # AI config wizard (dialog version)
index           # vectorize the current source
reindex         # clear all vectors and re-vectorize
q               # quit
```

### CLI mode

```bash
sip -l                  # list all sources
sip -l 1                # list articles of source 1 (id format [list index/real ID])
sip -d https://xxx/rss  # download a new RSS source
sip -u 1                # update source 1
sip -a 1                # archive (adds timestamp)
sip -una 1              # unarchive
sip -r 1                # remove source
sip -h                  # help
sip --lang en-US -l     # switch to English UI
```

**Full-screen reading**: `sip --show <article id>` opens a full-screen, sidebar-free reading view (Markdown-rendered body), with "**press W for the full reader · press Esc to exit**" at the bottom — press `W` to seamlessly switch to the full TUI (and locate the current article), `Esc`/`Q` to return to the command line.

**AI reads the original**: `sip --show <article id> --json` prints the article's title/source/link/published time/author + **raw body** (no rendering) as JSON to stdout for AI or scripts, e.g. `sip --show 42 --json --lang en-US --ignoresafeannouncement`.

| Short | Long | Description |
|-------|------|-------------|
| `-l` | `--list` | List all sources; with an id, list that source's articles (`-l --json` / `-l 1 --json` structured output including health and content quality). Id format `[list index/real ID]`; commands like `--show/--versions/--summary` use the real ID on the right |
| `-d` | `--download` | Download a new RSS source (http/https prefix optional, auto-completed) |
| `-u` | `--update` | Update a specific source (id) |
| `-a` | `--archive` | Archive the current snapshot (adds timestamp) |
| `-una` | `--unarchive` | Unarchive (checks for name conflicts) |
| `-r` | `--remove` | Delete a source and all its articles and vectors (add `--yes`/`-y` to skip confirmation, for non-interactive scripts/AI) |
| `--show <id>` | | Full-screen reading (no sidebar; `W` into full TUI, `Esc` to exit); with `--json`, outputs raw unrendered body JSON for AI/scripts |
| `--versions <id>` | | List all historical versions of an article (with status and time, `--json` structured); to see a version's body use `--show <that version's id>` |
| `--diff <id> [vA vB]` | | Compare two versions of an article's body (defaults to the latest two); `--json` structured output for AI |
| `--export <id \| feed:N \| all> [out.md\|dir]` | | Export an article as Markdown (confirms before `--export-all`; `--yes` skips) |
| `--fulltext <id>` | | Fetch an article's full text to local cache (consent on first use; `--yes` skips consent/confirmation, `--json` structured); `--purge-fulltext [id]` clears cache |
| `--feed-info <id>` | | Source identity & health: name/type/author/website/last updated/latest article/status (`--json` structured) |
| `--export-opml [file]` | | Export all sources as OPML (default `feeds.opml`) |
| `--import-opml <file>` | | Batch-import sources from OPML (skips existing by FeedUrl) |
| `--like <id> [--ai [reason]]` | | Mark an article: user like (♥) or AI judgment (🤖); `--likes` to view |
| `--today [--json]` | | Today's reading list (5 rule-based picks + goal/progress) |
| `--sync` | | Update only "due" sources (optional `--feed id` to limit to one; `--json` structured) |
| `--update-all` | | Force-update all sources (equivalent to TUI's `F6`) |
| `--schedule` | | Set a source's update schedule: `--schedule <id> <expression>` |
| `-h` | `--help` | Show help |

### Update scheduling (per-source auto-update, no wasted resources)

Each source can have its own **update schedule**; the program only fetches when it's "due." Expressions: interval `5m` / `30m` / `1h` / `7d`, fixed time `daily@10:00`, `weekly@Mon 08:00`, manual `manual`.

```bash
sip --schedule 1 30m            # source 1 updates every 30 minutes
sip --schedule 2 daily@10:00    # source 2 updates daily at 10:00
sip --schedule 3 manual         # source 3 switched to manual
sip -l                          # each source shows "frequency · last · next"
```

- **On launch**: silently syncs all due sources
- **While running**: checks every 15 minutes in the background, updating only due sources
- **CLI mode does not auto-sync**, but warns about due sources (`--ignoresafeannouncement` does not suppress this warning; `--json` mode auto-suppresses it to avoid polluting structured output)
- **Due determination**: `now >= last fetch time + schedule due point`; every successful fetch rewrites the "last fetch time"

### Multiple languages (language files)

All user-visible strings are read from `readwithhotsoup/languages/<code>.json`. **The source code is in English**; language files map "English key → translation" and fall back to English when missing. The files support **nested group structure** (`Lang.Init` auto-flattens, compatible with the old flat format).

- Selection: `--lang <code>` argument > `LANG` env var > default `zh-CN`
- Default translations are copied/merged into the data dir on first launch; **just edit the files in the data dir** — changes take effect immediately
- New translation keys auto-merge into existing files, **without overwriting keys you've changed**
- Custom translation: copy `en-US.json` to `your-code.json`, change values, load with `--lang your-code`

### AI commands (semantic search / smart summaries)

Built-in AI capabilities: **Embedding vectorization + semantic search** (RAG) and **LLM article summaries**, usable by AI agents or humans through the same CLI.

> **Initialization note for AI agents/scripts**: by default **no model is configured and nothing is vectorized** — using `--search` directly will report "AI not configured" or "no vector index." An AI should first run `sip --config` to confirm initialization; if config is missing run `sip --init`, if the index is missing run `sip --index`, and after changing models run `sip --reindex`. Output is always **UTF-8**.

```bash
sip --init                          # first-time AI config (model + API key, interactive)
sip --config                        # view/edit AI config
sip --index                         # embed/vectorize articles (interactive source selection)
sip --reindex                       # re-vectorize after changing the embedding model
sip --search "LLM Agent"            # semantic search (returns hits + similarity)
sip --search "RAG" --feed 1 --json  # search limited to a source, JSON output
sip --grep "keyword"                # full-text search (title/body/summary, no AI needed)
sip --summary 12                    # generate a summary for article 12 (saved to DB)
sip --summary feed:3                # generate summaries for all articles in source 3
sip --summary-all                   # generate summaries for all articles without one
```

| Command | Description |
|---------|-------------|
| `--init` | Interactive first-time config: choose Embedding provider, LLM provider, and enter the API key (auto-degrades to plain input when stdin is redirected, without crashing) |
| `--config` | Print current AI config (no secrets) and config file path |
| `--index` | Batch-generate Embedding vectors for articles of a selected source |
| `--reindex` | After changing the Embedding model (dimension change), clear old vectors and rebuild everything |
| `--search <query>` | Semantic search; optional `--feed id`, `--threshold 0.7`, `--json`. ⚠️ Performance note: cross-source search is a full vector scan; prefer `--grep` (exact SQL LIKE match) first; use `--feed id` to limit to one source for semantic expansion, or tune `--threshold` to reduce candidates. ⚠️ Full-text vector hit scores are usually 0.1–0.2 lower than title vectors; when searching "body-only concepts" with few results, consider lowering the threshold |
| `--grep <keyword>` | Full-text search (SQL LIKE, no AI); default output "id+title+occurrence count+±50 char snippet", with limits (`--limit N` / `--max-snippets N` / `--json` / `--full`) |
| `--summary <id>` | Call the LLM to generate a summary for a single article (`--json` structured); `feed:<id>` generates for all articles in that source |
| `--summary-all` | Generate summaries for all articles whose `Summary` is empty |

**API Key** is stored in the OS-native credential store (Windows Credential Manager / macOS Keychain / Linux Secret Service), never written to any file; non-sensitive config is stored in `readwithhotsoup/ai_config.json` (key names are case-insensitive, missing `http(s)://` prefixes on endpoints are auto-completed; `"allowPrivateNet": true` allows intranet full-text fetching).

#### Error codes

AI command failures uniformly report a structured error code; in `--json` mode errors return as `{"error": {"code": "...", ...}}`: `MODEL_UNAVAILABLE` / `INVALID_RESPONSE` / `INVALID_JSON` / `EMPTY_RESPONSE` / `API_KEY_INVALID` / `NETWORK_ERROR` / `NO_INDEX` / `FEED_NOT_FOUND` / `ITEM_NOT_FOUND` / `EMPTY_QUERY`.

#### Exit codes (for scripts/AI to judge success)

A successful CLI command exits `0`; failures return a nonzero code by category:

| Exit code | Meaning |
|-----------|---------|
| `0` | Success (including normal cancellation, e.g. answering n during `-r` confirmation) |
| `1` | General error (argument/usage error, unknown command, DB error, partial update failure) |
| `2` | Network / service unreachable (`NETWORK_ERROR`, `MODEL_UNAVAILABLE`, download timeout) |
| `3` | Resource not ready (AI not configured, API key missing/invalid, `NO_INDEX`, source/article not found, empty query) |

> In `--json` mode, errors still first output structured `{"success": false, "error": {...}}`, then exit with the corresponding nonzero code.

### Article archiving mechanism

The program keeps a status for each article: `active` (currently valid) / `archived` (older version after the author modified it). When updating RSS:

- Compares old vs. new Content; **archiving triggers only on body changes**
- Modified articles: old version → `archived`, new version → `active`; new articles are written directly as `active`
- Deletion is no longer detected (many sites only push the most recent N articles in RSS; an old article going offline doesn't mean it was deleted)

### Full-text fetching

When an RSS summary is too short (<100 chars), fetch the original into a local cache:

```bash
sip --fulltext <id>            # fetch full text (consent phrase on first use; --yes skips consent/confirmation)
sip --fulltext <id> --json     # structured output {itemId, cached, content}
sip --purge-fulltext [id]      # clear cache (no id = clear all)
```

- Full text is stored at `readwithhotsoup/fulltext/<itemId>.md` (file cache, **no DB change**); when the source is indexed, the full-text vector is stored in `vecs.json` and merged into semantic search; `--index`/`--reindex` auto-backfill full-text vectors for articles with an existing cache (so fetching full text first and indexing later doesn't miss them)
- **Content is always the primary content**; full text is supplementary; the original is displayed on top, full text below, separated by a divider
- Fetching creates no new version and doesn't participate in diff/updates
- **Fetching safety boundary (SSRF protection)**: only http/https links; loopback (127.0.0.1/::1) and link-local/cloud metadata addresses (169.254.0.0/16) are always rejected; private ranges (10/8, 172.16/12, 192.168/16, 100.64/10) are rejected by default — to fetch an intranet source, set `"allowPrivateNet": true` in `ai_config.json`
- `sip --show <id> --json` outputs a `fulltext` field when a cache exists (AI/scripts read full text without first running `--fulltext` and reading a separate file)

---

## Tech stack

- C# / .NET 10.0
- [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite)
- [CodeHollow.FeedReader](https://github.com/arminreiter/FeedReader) (RSS/Atom parsing)
- [DiffPlex](https://github.com/mmanela/diffplex) (text diff)
- [Terminal.Gui](https://github.com/gui-cs/Terminal.Gui) (folder-view TUI)
- [HtmlAgilityPack](https://html-agility-pack.net/) (body HTML → plain text / full-text extraction)
- [ktsu.CredentialCache](https://www.nuget.org/packages/ktsu.CredentialCache) (native credential store for API keys)
- Embedding / LLM: OpenAI-compatible APIs (local Ollama, DeepSeek, OpenAI, etc.)

---

## Project structure

```
├── sip.csproj          # project file (program name sip)
├── RssReader.cs        # all code (single file)
├── publish.ps1         # single-file packaging script (win/linux/mac platforms)
├── languages/          # default language files (copied next to the exe at build/publish, also embedded as fallback)
│   ├── zh-CN.json
│   └── en-US.json
├── .opencode/skills/   # skill for AI agents to use the CLI (teaches AI to call sip)
│   └── sip-rss/SKILL.md
├── readwithhotsoup/    # runtime data dir (auto-created next to the exe on first launch)
│   ├── rss.db          # SQLite database
│   ├── ai_config.json  # non-sensitive AI config (generated at runtime)
│   ├── fulltext/       # full-text fetch cache (<itemId>.md + vecs.json)
│   ├── reading_progress.json  # reading progress memory
│   └── languages/      # language files (default translations copied here, editable directly)
└── README.md
```

---

## AI-related

- AI (deepseek / opencode / chatgpt) was used to generate some code and comments
- Built-in Embedding semantic search and LLM summaries (see the "AI commands" section above)

---

## Long-term testing checklist

The following behaviors only show problems over **days/weeks**, so a single test can't catch them; recommend incorporating them into routine checks:

### Data accumulation
| Feature | What to observe |
|---------|-----------------|
| Version tracking/archiving | Author edits → new version; correct Guid grouping, `✎` marker, and `--diff` after long accumulation; list performance as version count grows |
| Update scheduling | Whether `30m` / `daily@10:00` / `weekly@Mon 08:00` expire correctly by clock/interval; `LastCheckedAt` recalculation; update only what's due, no wasted refreshes |
| Source health | Failure counter accumulation → reset on success; "⚠ long inactive" marker (needs 30 days or schedule×3) |
| Reading progress | Correct restoration of scroll positions across days/articles; `reading_progress.json` growth; bad values (negative/out-of-range) rejected |
| Full-text cache | Auto-cleanup triggers at 200 files/200MB; `vecs.json` growth; orphan cleanup after deleting articles/sources |
| Language file merging | New keys keep auto-merging after upgrades; keys the user changed are never overwritten |

### Long-running
| Feature | What to observe |
|---------|-----------------|
| TUI left open for a long time | Whether the 15-minute background sync loop leaks (memory/handles); immersive/collapse/management page/version dialogs don't crash under repeated operation |
| Auto-sync | Due sources update on time with no freezes/duplicates during long idle |
| SQLite performance | Latency of `-l` / `--grep` / `--search` with 10k+ articles; concurrent read/write works under WAL |

### Stability & consistency
| Feature | What to observe |
|---------|-----------------|
| AI summary/vector cache | Summary cache reused without repeat calls; sidecar merge search still correct after model unavailable→recovered |
| OPML round-trip | Export → import → re-export, no duplicate sources (idempotent) |
| Full-text consent | consent takes effect once; `--yes` / interactive paths stay consistent over time |

---

## 📋 Read the test report

[sip comprehensive test report (2026-08-11)](sip-测试报告-2026-08-11.md) — 51 feature tests + 30+ boundary/exception injections + security penetration + data-scale stress tests + concurrency tests. The 11 defects in the report (`-l` list O(n²), SSRF, terminal injection, main-db corruption tolerance, etc.) were all fixed in **v1.0** and each re-tested.

---

## A final word

sip is not a product chasing "daily active users" and "time spent."

What it pursues is:

> **When you open sip, you know what you read today is trustworthy; when your AI calls sip, you know the sources it cites are reliable.**

It won't make you scroll more, but it will help you read with more peace of mind.

**Design boundary**: sip does only two things — **deterministic rules** and **local storage of facts**; anything that needs judgment/explanation/dialogue is left to agents and users. Today's hot soup selection rules stay fixed and explainable; personalization "intelligence" waits until telemetry data is sufficient, then is implemented either as new deterministic rules or placed at the agent layer — never in the program.

May we meet again, none the worse for wear

---

## License

Licensed under the GNU General Public License v3.0 (GPL-3.0)
