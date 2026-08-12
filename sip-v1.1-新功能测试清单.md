# sip v1.1.0 新功能上手测试清单

> 本文档覆盖 **v1.0 测试报告之后新增的功能**，供手动上手测试。
> 前置：构建最新版（`dotnet build`）或直接运行 `bin/Debug/net10.0/sip.dll`。
> 测试时建议用一个独立/测试用的数据目录，避免污染真实数据（或用临时副本）。

- 已构建程序：`bin/Debug/net10.0/sip.dll`
- 数据目录：`bin/Debug/net10.0/readwithhotsoup/`（rss.db + 各 sidecar JSON）
- 统一追加 `--ignoresafeannouncement` 跳过安全横幅

---

## 0. 构建与确认版本

```bash
dotnet build                 # 应 0 警告 0 错误
dotnet bin/Debug/net10.0/sip.dll --help --ignoresafeannouncement
# 帮助里应出现：--dedup / --policy / --onboarding
```

---

## 1. 跨源去重 `--dedup`

### 1.1 检测「可能同文」
需要两个订阅源里有「正文大部分相同」的文章（如同一篇被两个源转载）。

```bash
sip --dedup scan --ignoresafeannouncement
```
**预期**：输出「发现 N 组可能同文（段落重合度 ≥ 80%）」，每组给出两篇 `[id]` + 重合度 + `sip --diff A B`。
- 完全不同的文章**不应**被判为同文。
- `--today` 顶部也应出现「⚠ 可能同文（跨源重复）」分组。
- `--today --json` 的 `digest.dedups[]` 里应有 `itemA/itemB/overlap/diff`。

### 1.2 隐藏（hide）
```bash
sip --dedup hide <hiddenId> <canonicalId> --ignoresafeannouncement
```
**预期**：`已隐藏 <hiddenId>（保留 <canonicalId>）`；生成 `readwithhotsoup/dedup.json`（键 = `feedId:url`）。

**验证「全渠道隐身」**：隐藏后，该篇文章应**不再出现**在：
- `--grep` 全文搜索
- `--search` 语义搜索（若已索引）
- `--summary-all` / feed 摘要
- `-l N` 文章列表、`--today` 新增计数
- 源计数（`Status` 不再是 active）

可直接用 SQL 确认：`SELECT COUNT(*) FROM Items WHERE Status='dedup';` 应为 1。

### 1.3 查看与撤销
```bash
sip --dedup list --ignoresafeannouncement          # 应列出已隐藏文章 + 撤销 key
sip --dedup undo "<key>" --ignoresafeannouncement  # 撤销 → 恢复 active
```
**预期**：撤销后文章重新出现，`dedup.json` 里该规则被移除。

### 1.4 防卷土重来（关键）
`hide` 后对该源执行一次 `--sync`/`-u` 更新：
**预期**：被隐藏的那篇**不会被重新导入**（因 `dedup.json` 规则在导入时跳过）。
> 若作者把被隐藏那篇改成了完全不同内容，规则应自动失效、文章重新出现（分歧自动失效）。

### 1.5 TUI
进入 TUI manage（`M`）→ 按 `i`：
**预期**：弹出「已隐藏的文章」列表，`r`/`Enter` 可撤销忽略。

---

## 2. Source Policy `--policy`

```bash
sip --policy list --ignoresafeannouncement                       # 空时提示暂无
sip --policy set 1 tag important --ignoresafeannouncement         # 打标签
sip --policy set 1 keep 每周看一次 --ignoresafeannouncement       # 记备注
sip --policy set 1 lower_frequency 7d --ignoresafeannouncement    # 降频（改 Feeds.Schedule）
sip --policy set 1 archive --ignoresafeannouncement               # 归档
sip --policy set 1 unsubscribe --ignoresafeannouncement           # 退订候选（仅标记）
sip --policy remove 1 --ignoresafeannouncement                    # 移除规则
```

**预期**：
- 每个动作输出「已应用规则：xxx」；`source_policy.json` 里 `createdBy` 恒为 `user`。
- `lower_frequency 7d` 会**真正改动该源更新频率**（`--schedule` 效果）。
- `-l` 列表该源末尾显示规则标记，如 `[#important · 保留]`。
- `--policy list --json` 结构化输出。
- `remove` 后 `-l` 标记消失。

**边界**：未知动作 / 不存在源编号应报错且退出码非 0。

---

## 3. 报告事实重构 `--insights`

需先开启遥测（在临时目录验证）：
```bash
sip telemetry enable --yes --ignoresafeannouncement
sip --insights --json --ignoresafeannouncement
```
**预期**：`feeds[]` 里每项含 **`status`**（正常 / ⚠ 长期未更新 / ✗ 失败 N 次）与 **`reasons`（数组）**。
- **不应再出现** `health` / `action` / `basis` 字段（破坏性变更）。
- 文本输出：`[id] 标题 [状态]`，下面列 `· 事实原因`，**不再有「建议退订 / 可考虑精简」这类价值判断**。
- 一个「你常读」的源，其 `status` 应保持「正常」，不会因读得多/少而变红。

---

## 4. TUI 新交互（需真实终端操作）

进入 TUI：`dotnet bin/Debug/net10.0/sip.dll`

### 4.1 manage（`M`）
- 按 `Enter` 在某个订阅源上 → 打开**编辑面板**（更新计划/归档/去归档/删除）。
- 按 `s` → **方向键选更新计划**（预设列表，`↑/↓` 选，`←/→` 调 daily/weekly 的小时，`Enter` 应用，`Esc` 取消；`自定义…` 走文本输入）。
- 按 `i` → 查看/撤销已隐藏（dedup'd）文章。

### 4.2 TUI 命令行（Esc 唤出）
试试：`diff 1`、`feed-info 1`、`likes`、`dedup list`、`policy list`、`telemetry status`、`export 1 out.md`、`export-opml`、`import-opml <file>`、`onboarding list`。
**预期**：这些命令在 TUI 里可用，输出显示在对话框（而非污染界面）。

### 4.3 平铺
manage/报告页底部的提示文本应为**左对齐**（不再居中）。

---

## 5. Onboarding `--onboarding`

```bash
sip --onboarding --ignoresafeannouncement          # 列出分类与推荐源
sip --onboarding 开发 --ignoresafeannouncement      # 只看某分类
sip --onboarding add 开发 1 --ignoresafeannouncement    # 添加第 1 个
sip --onboarding add 开发 all --ignoresafeannouncement  # 添加全部
```
**预期**：`add` 成功/失败计数输出；已存在源会跳过/报错；网络失败的单条会提示「添加失败」但不中断。
- `templates.json` 可编辑：改完后 `--onboarding` 反映新清单。

> ⚠️ 预置的 URL（OpenAI / Hugging Face 等）**可能不准**，测试时若添加失败属正常，可按需编辑 `templates.json` 换成真实源。

---

## 6. 遥测增强

```bash
sip telemetry enable --ignoresafeannouncement      # 无 --yes 时：应弹安全提示 + y/n 确认
sip telemetry status --ignoresafeannouncement
sip telemetry show --limit 20 --ignoresafeannouncement
```
**预期**：
- `enable` 无 `--yes` 时提示「苏暖泉将开始记录…」并询问 `y/n`。
- `disable` → 「苏暖泉已离开」；`clear` → 「苏暖泉已清空工作记录」；`status` 状态值「开启 / 未开启（…）」。
- `show` 里应能看到新事件：
  - `consent_change`（enable/disable 时产生，即使后来关闭也保留）
  - `feed_change`（增删/归档/改频率时产生，带 `action`）
  - `search`（跑 `--grep`/`--search` 时产生，含**完整查询词**）

---

## 快速回归（确认没改坏旧功能）

```bash
sip -l --ignoresafeannouncement                 # 列表正常，含 policy 标记
sip --today --ignoresafeannouncement            # 今日推荐 + 变化摘要正常
sip --grep 测试 --ignoresafeannouncement        # 全文搜索正常
sip --diff 1 --ignoresafeannouncement           # diff 正常
```

---

## 反馈清单

测试时记录：
- 每个功能：✅ 通过 / ❌ 失败 / ⚠️ 异常
- 失败时的：操作、预期、实际输出、退出码
- 顺带记录：任何崩溃、卡死、乱码、数据异常

完成后可把结果贴回来，我会据此修复或补测试。
