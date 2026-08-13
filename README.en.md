# 🍲 sip

> ——"Savor it, sip it slow."

Welcome~

No matter how you found this — an AI recommendation, some forum thread, or a friend casually dropping the link — thank you for clicking in, and I hope you'll stay on this page for five minutes.

---

## Does any of this sound familiar?

The blogs you follow update, but you can't be bothered to open each one.

An author quietly flips a conclusion, and you never know — out in the wild, that's how facts get twisted.

You subscribe to a dozen feeds, get a hundred posts a day, can't read them, so you just "mark all read."

You ask an AI to check something, and it cites a pile of sources you don't trust at all.

The sources look impressive, but you just can't get into them.

You want to cross-check from many angles, but you're drowning in sheer volume.

Your RSS reader just feels a bit clunky to you.

If any of these ring a bell, sip can probably help.

## Give it a try?

sip is a **local-first personal information hub**: download one exe, run it, add a few sources, and come back tomorrow.

Single-file builds for Windows / macOS / Linux. No sign-up, no cloud — everything lives on your machine in a folder called `readwithhotsoup`. Copy it and you've migrated.

Once it's open, just feel your way around.

Take it slow, no rush. It's a cure for information overload.

## What if I really can't figure it out?

Honestly, I designed for that from the start: sip isn't just for humans — it's for agents too.

We built **agent-invocation capability** — any agent can call sip through the CLI (with a skill), with capability nearly identical to the TUI (except `init`, which touches your API key). People and AI are first-class citizens.

## What's different from other RSS readers

FreshRSS, Feedly, Inoreader are all fine, but sip has a different temperament.

Most readers live in the cloud and hand you folders and tags.

sip is local SQLite; when an author edits a post, it quietly keeps a version and you can see the diff.

Five or six sources repost the same piece? It clusters them so you don't re-read.

It can also run a reading report, lay the facts out, and let you make the call.

What sip does, plainly, is **collect, preserve, track, filter, and use** the information you care about.

Under the hood, just two things — **deterministic rules**, and **local storage of facts**.

The judgment is yours; AI is just a quiet assistant watching your back.

AI can help you understand information, but it never decides its value for you — it's all up to you.

## A little product philosophy

Honestly, the world isn't short of "smarter" readers. sip isn't trying to be smarter than them — it's trying to hand the judgment back to you.

Like the line from *Let the Bullets Fly* — "I want to stand tall AND make the money." sip is a bit the same: **stand tall, and still read what you care about.** No kowtowing to algorithms, no going with the herd — and you still get to read the things that matter to you.

Information itself is neither good nor bad; whether something is "worth reading" is for you to say. AI can dig up the facts and point out the changes, but "does this source still matter to me" — that's your call.

That's why sip sticks to two things: **deterministic rules**, and **local storage of facts**. You set the rules, the facts stay clean, and it doesn't step past that line.

Which is also why telemetry is off by default and AI just quietly watches — it's not here to live your life for you.

## A few little things inside

🍵 **Today's Hot Soup**: a small bowl of worthwhile reads each day — five, or however many you set. Not greedy, no rush.

💧 **Sumenia**: a cute girl who's absent by default; only after you invite her does she quietly note your reading habits, locally. Don't invite her and she doesn't exist.

📜 **Version tracking**: author changes a claim or a number, you see the diff — almost like git.

## As for how to use it

Besides clicking in the UI, the CLI works too:

    sip --today              # what to read today
    sip --search "RAG"       # semantic search (run sip --init to configure AI first)
    sip --grep "quantum"     # full-text search, no AI needed
    sip --diff 12 v1 v3      # see what changed from v1 to v3
    sip --insights           # a reading report; the facts are yours, the call is yours

Oh, and `sip --init` asks for your API key, so run it yourself in a real terminal — scripts can't do it.

## Want AI to answer only from sources you trust?

Wire sip into a group or bot with OpenClaw or Cherry Studio — it answers only from your subscribed sources. Junk citations, goodbye. Setup steps: [Wiki · Bot Integration](https://sip.hotsouprealm.top/使用/Bot.html).

## More

Full docs at [sip.hotsouprealm.top](https://sip.hotsouprealm.top/); the test report is in the repo (v1.1.4, overall 8.4/10, [link](./sip-完整测试报告-2026-08-12-最终版.md)); to build from source, see the [Wiki](https://sip.hotsouprealm.top/上手/快速开始.html).

Also, shameless plug.

Follow the hot soup teahouse, follow the hot soup teahouse, thank you: [https://blog.hotsouprealm.top/](https://blog.hotsouprealm.top/atom.xml)

Check out our self-test report: [https://sip.hotsouprealm.top/测试报告.html](https://sip.hotsouprealm.top/%E6%B5%8B%E8%AF%95%E6%8A%A5%E5%91%8A.html)

---

When you open sip, you know what you read today is trustworthy; when your AI calls sip, you know the sources it cites are reliable.

May we meet again, none the worse for wear 🍲

Licensed under the GNU General Public License v3.0 (GPL-3.0)
