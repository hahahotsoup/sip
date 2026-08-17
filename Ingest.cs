// ===== ingest 应用层:Phase1「广开言路」第二扇门(独立文件,partial Program)=====
// 与 RssReader.cs 同属 partial class Program(见 RssReader.cs L95),
// 因此可自由调用 RssReader.cs / simon.cs 中的顶层函数(同类 private static 成员)。
// 与 Tui.cs 同约定:一律通过 dbPath 参数拿数据目录,不碰 Main 局部变量。
// 职责:收集(把非 RSS 的信息变成证据包)→ 版本链/哈希/双时态 → 浏览/核实/遗忘。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

public partial class Program
{
    // ══════════ ingest 命令入口(sip ingest <sub> …)══════════
    // 子命令:
    //   --stdin                    管道输入存为证据(source_type=evidence)
    //   --evidence <file|--stdin>  导入 sip-evidence-v1 证据包(schema 校验)
    //   --url <url>                URL 直存(source_type=watch + watch_targets 首快照,SSRF 防护)
    //   list [--stale] [--group N] 浏览;show <id> 看详情;confirm <id> 核实;rm <id> [--yes] 遗忘
    static void IngestCli(string[] args, string dbPath)
    {
        bool json = args.Any(a => a.Equals("--json", StringComparison.OrdinalIgnoreCase));
        bool yes = args.Any(a => a.Equals("--yes", StringComparison.OrdinalIgnoreCase))
                || args.Any(a => a.Equals("-y", StringComparison.OrdinalIgnoreCase));
        var pos = args.Where(a => !a.StartsWith("--")).ToArray();
        string sub = pos.Length > 0 ? pos[0].ToLowerInvariant() : "";

        if (args.Contains("--stdin", StringComparer.OrdinalIgnoreCase)) { IngestStdin(args, dbPath, json); return; }
        if (args.Contains("--evidence", StringComparer.OrdinalIgnoreCase)) { IngestEvidence(args, dbPath, json); return; }
        int urlIdx = Array.FindIndex(args, a => a.Equals("--url", StringComparison.OrdinalIgnoreCase));
        if (urlIdx >= 0)
        {
            string? url = urlIdx + 1 < args.Length ? args[urlIdx + 1] : null;
            if (string.IsNullOrWhiteSpace(url) || url.StartsWith("--"))
            { IngestUsage(json, Lang.T("Usage: sip ingest --url <url> [--ttl <days>] [--yes] [--json]")); return; }
            IngestUrl(url, args, dbPath, json);
            return;
        }

        switch (sub)
        {
            case "list":
                IngestList(args, dbPath, json);
                return;
            case "show":
                if (pos.Length < 2 || !int.TryParse(pos[1], out int sid)) { IngestUsage(json); return; }
                IngestShow(sid, dbPath, json);
                return;
            case "confirm":
                if (pos.Length < 2 || !int.TryParse(pos[1], out int cid)) { IngestUsage(json); return; }
                IngestConfirm(cid, dbPath, json);
                return;
            case "rm":
                if (pos.Length < 2 || !int.TryParse(pos[1], out int rid)) { IngestUsage(json); return; }
                IngestRm(rid, dbPath, json, yes);
                return;
            case "refresh":
                IngestRefresh(args, dbPath, json);
                return;
            case "group":
                IngestGroup(args, dbPath, json);
                return;
            case "groups":
                IngestGroups(dbPath, json);
                return;
            case "retrieve":
                IngestRetrieve(args, dbPath, json);
                return;
            case "ask":
                IngestAsk(args, dbPath, json);
                return;
            default:
                IngestUsage(json);
                return;
        }
    }

    static void IngestUsage(bool json, string? custom = null)
    {
        string msg = custom ?? Lang.T("Usage: sip ingest <--stdin | --url <url> | --evidence <file|--stdin>> | list [--stale] | show <id> | confirm <id> | rm <id> [--yes] [--json]");
        SetExit();
        if (json) JsonOut(new { success = false, error = new { code = "USAGE", message = msg } });
        else Console.WriteLine(msg);
    }

    // ══════════ 核心存储:版本链 + 无变化不存 + 双时态 ══════════
    // 返回 (Id, Status, Error);Status: created / updated / unchanged / error
    // · 同 SourceKey 最新 active 版哈希相同 → 不建新版,仅刷新 ObservedAt(无变化不存)
    // · 哈希不同 → 旧版标 superseded(不删除),新版本 Version+1 且 PrevId 链接版本链
    // · CapturedAt = valid_at(内容声称时间);ObservedAt = observed_at(本地记录时间)
    static (long Id, string Status, string? Error) IngestStore(
        string dbPath, string sourceType, string sourceKey, string? sourceName, string? sourceUrl,
        string? title, string? excerpt, string content, string? producerMeta, int ttlDays, string? capturedAt,
        bool skipPolishVersion = false)
    {
        try
        {
            string text = (content ?? "").Trim();
            if (text.Length == 0 && string.IsNullOrWhiteSpace(excerpt))
                return (0, "error", Lang.T("Nothing to store: content and excerpt are both empty"));
            string hash = EvidenceHash(text);
            string now = DateTime.Now.ToString("O");
            string cap = string.IsNullOrWhiteSpace(capturedAt) ? now : capturedAt;
            if (ttlDays <= 0) ttlDays = sourceType == "watch" ? 7 : 30;   // 默认保鲜:网页7天 / 证据30天

            using var conn = OpenDb(dbPath);
            conn.Open();

            // 查同 SourceKey 最新 active(事务外:分级需要另开连接调 embedding,避免事务内锁)
            long? existingId = null; int oldVersion = 0; string oldHash = ""; string oldContent = "";
            using (var q = conn.CreateCommand())
            {
                q.CommandText = "SELECT Id, Version, Hash, Content FROM Evidence WHERE SourceKey = @k AND Status = 'active' ORDER BY Id DESC LIMIT 1";
                q.Parameters.AddWithValue("@k", sourceKey);
                using var r = q.ExecuteReader();
                if (r.Read())
                {
                    existingId = r.GetInt64(0);
                    oldVersion = r.GetInt32(1);
                    oldHash = r.IsDBNull(2) ? "" : r.GetString(2);
                    oldContent = r.IsDBNull(3) ? "" : r.GetString(3);
                }
            }

            if (existingId.HasValue && oldHash == hash)
            {
                using var tx = conn.BeginTransaction();
                var u = conn.CreateCommand();
                u.Transaction = tx;
                u.CommandText = "UPDATE Evidence SET ObservedAt = @now WHERE Id = @id";
                u.Parameters.AddWithValue("@now", now);
                u.Parameters.AddWithValue("@id", existingId.Value);
                u.ExecuteNonQuery();
                tx.Commit();
                return (existingId.Value, "unchanged", null);
            }

            // 内容变化 → 改动分级(事务外;有 AI 走语义距离,无 AI 走字符级降级)
            string? grade = null; bool reversed = false;
            if (existingId.HasValue)
                (grade, reversed) = ComputeChangeGrade(dbPath, oldContent, text);

            // 动态页防噪(refresh 场景):润色级变化 → "连续两次"才判动态页不建版。
            // 偶发润色(第一次)→ 仍建版(版本即事实:任何一次真实修改都留痕);
            // 连续两次润色 → 判动态页(时间戳/动态内容反复微变),不建版 + 标记 DynamicPage。
            if (existingId.HasValue && skipPolishVersion && grade == "polish" && PrevGradeIsPolish(dbPath, existingId.Value))
            {
                using var tx = conn.BeginTransaction();
                var u = conn.CreateCommand();
                u.Transaction = tx;
                u.CommandText = "UPDATE Evidence SET ObservedAt = @now, DynamicPage = 1 WHERE Id = @id";
                u.Parameters.AddWithValue("@now", now);
                u.Parameters.AddWithValue("@id", existingId.Value);
                u.ExecuteNonQuery();
                tx.Commit();
                return (existingId.Value, "polish", null);
            }

            using var tx2 = conn.BeginTransaction();
            if (existingId.HasValue)
            {
                var arc = conn.CreateCommand();
                arc.Transaction = tx2;
                arc.CommandText = "UPDATE Evidence SET Status = 'superseded' WHERE Id = @id AND Status = 'active'";
                arc.Parameters.AddWithValue("@id", existingId.Value);
                arc.ExecuteNonQuery();
            }

            var ins = conn.CreateCommand();
            ins.Transaction = tx2;
            ins.CommandText = @"
                INSERT INTO Evidence (Schema, SourceType, SourceKey, SourceName, SourceUrl, Title, Excerpt, Content, Hash,
                                      Version, Status, PrevId, Grade, Reversed, CapturedAt, ObservedAt, Freshness, TtlDays, ProducerMeta)
                VALUES ('sip-evidence-v1', @st, @k, @sn, @su, @t, @e, @c, @h,
                        @v, 'active', @prev, @g, @rev, @cap, @now, 'fresh', @ttl, @pm)";
            ins.Parameters.AddWithValue("@st", sourceType);
            ins.Parameters.AddWithValue("@k", sourceKey);
            ins.Parameters.AddWithValue("@sn", (object?)sourceName ?? DBNull.Value);
            ins.Parameters.AddWithValue("@su", (object?)sourceUrl ?? DBNull.Value);
            ins.Parameters.AddWithValue("@t", (object?)title ?? DBNull.Value);
            ins.Parameters.AddWithValue("@e", (object?)excerpt ?? DBNull.Value);
            ins.Parameters.AddWithValue("@c", text);
            ins.Parameters.AddWithValue("@h", hash);
            ins.Parameters.AddWithValue("@v", oldVersion + 1);
            ins.Parameters.AddWithValue("@prev", (object?)existingId ?? DBNull.Value);
            ins.Parameters.AddWithValue("@g", (object?)grade ?? DBNull.Value);
            ins.Parameters.AddWithValue("@rev", reversed ? 1 : 0);
            ins.Parameters.AddWithValue("@cap", cap);
            ins.Parameters.AddWithValue("@now", now);
            ins.Parameters.AddWithValue("@ttl", ttlDays);
            ins.Parameters.AddWithValue("@pm", (object?)producerMeta ?? DBNull.Value);
            ins.ExecuteNonQuery();
            var lid = conn.CreateCommand();
            lid.Transaction = tx2;
            lid.CommandText = "SELECT last_insert_rowid()";
            long newId = Convert.ToInt64(lid.ExecuteScalar());

            // watch 类型 → watch_targets 首快照(幂等,后续 refresh/watch 接管)
            if (sourceType == "watch" && !string.IsNullOrWhiteSpace(sourceUrl))
            {
                var w = conn.CreateCommand();
                w.Transaction = tx2;
                w.CommandText = @"INSERT INTO WatchTargets (Url, FirstEvidenceId, CreatedAt) VALUES (@u, @eid, @now)
                                  ON CONFLICT(Url) DO NOTHING";
                w.Parameters.AddWithValue("@u", NormalizeWatchUrl(sourceUrl));
                w.Parameters.AddWithValue("@eid", newId);
                w.Parameters.AddWithValue("@now", now);
                w.ExecuteNonQuery();
            }

            tx2.Commit();
            return (newId, existingId.HasValue ? "updated" : "created", null);
        }
        catch (Exception ex)
        {
            return (0, "error", ex.Message);
        }
    }

    // ══════════ 改动分级 + 反转检测(P0;D5-6)══════════
    // 语义距离 = 1 − cos(旧版向量,新版向量);阈值进 sip_settings.json(你定)
    //   < ChangeGradePolish → ⚪润色(重写但意思没变)  < ChangeGradeReverse → 🟡调整  ≥ → 🔴反转
    // 反转检测:距离 ≥ 反转阈值 且 旧/新文本含立场翻转信号词(双验证,只呈现不下结论)
    static (string? Grade, bool Reversed) ComputeChangeGrade(string dbPath, string oldText, string newText)
    {
        double dist = SemanticDistance(dbPath, oldText, newText);
        var s = LoadSettings();
        string? grade = dist < s.ChangeGradePolish ? "polish"
                     : dist < s.ChangeGradeReverse ? "adjust" : "reverse";
        bool reversed = dist >= s.ChangeGradeReverse && HasReversalSignal(oldText, newText);
        return (grade, reversed);
    }

    // 语义距离:有 AI(已注册 embedding 模型)走向量;否则字符级降级(bigram Jaccard × 长度比)
    static double SemanticDistance(string dbPath, string a, string b)
    {
        if (CurrentEmbeddingModelId(dbPath) == 0)
            return 1.0 - CharSimilarity(a, b);   // 从未配置 AI:纯字符,不打扰
        try
        {
            var cfg = LoadConfig(dbPath);
            var va = GetEmbeddingAsync(a, cfg).GetAwaiter().GetResult();
            var vb = GetEmbeddingAsync(b, cfg).GetAwaiter().GetResult();
            if (va != null && vb != null && va.Length > 0 && va.Length == vb.Length)
                return Math.Clamp(1.0 - CosineSimilarity(va, vb), 0.0, 1.0);
        }
        catch { /* 嵌入失败 → 字符降级 */ }
        return 1.0 - CharSimilarity(a, b);
    }

    // 字符级相似度(无 AI 降级;确定性规则)
    static double CharSimilarity(string a, string b)
    {
        if (a == b) return 1.0;
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0.0;
        var sa = new HashSet<string>();
        for (int i = 0; i + 1 < a.Length; i++) sa.Add(a.Substring(i, 2));
        var sb = new HashSet<string>();
        for (int i = 0; i + 1 < b.Length; i++) sb.Add(b.Substring(i, 2));
        if (sa.Count == 0 && sb.Count == 0) return 1.0;
        int inter = 0;
        foreach (var x in sa) if (sb.Contains(x)) inter++;
        int uni = sa.Count + sb.Count - inter;
        double jac = uni == 0 ? 0.0 : (double)inter / uni;
        double lenRatio = (double)Math.Min(a.Length, b.Length) / Math.Max(a.Length, b.Length);
        return jac * lenRatio;
    }

    // 立场翻转信号词(双语;命中=改口信号,与距离双验证)
    static readonly string[] ReversalSignals =
    {
        "不再认为", "推翻", "否认", "更正", "改口", "撤回", "纠正", "反悔", "承认错误", "收回",
        "no longer", "reversed", "retracted", "retract", "denies", "deny", "corrected", "correction", "take back"
    };

    static bool HasReversalSignal(string oldText, string newText)
    {
        string o = (oldText ?? "").ToLowerInvariant();
        string n = (newText ?? "").ToLowerInvariant();
        foreach (var sig in ReversalSignals)
            if (o.Contains(sig) || n.Contains(sig)) return true;
        return false;
    }

    // 上一版是否为润色级(连续两次润色 → 判动态页的判定依据)
    static bool PrevGradeIsPolish(string dbPath, long id)
    {
        using var conn = OpenDb(dbPath);
        conn.Open();
        var q = conn.CreateCommand();
        q.CommandText = "SELECT Grade FROM Evidence WHERE Id = @id";
        q.Parameters.AddWithValue("@id", id);
        var o = q.ExecuteScalar();
        return o != null && o is not DBNull && o.ToString() == "polish";
    }

    // Grade 展示(事实标签,非价值判断)
    static string GradeLabel(string? g) => g switch
    {
        "polish" => "⚪润色",
        "adjust" => "🟡调整",
        "reverse" => "🔴反转",
        _ => "-"
    };

    // 失效标记:标 invalid + StatusNote,旧内容保留可读(双时态:不覆盖)
    static void MarkEvidenceInvalid(string dbPath, long id, string note)
    {
        using var conn = OpenDb(dbPath);
        conn.Open();
        var u = conn.CreateCommand();
        u.CommandText = "UPDATE Evidence SET Status = 'invalid', StatusNote = @n, ObservedAt = @now WHERE Id = @id AND Status = 'active'";
        u.Parameters.AddWithValue("@n", note);
        u.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
        u.Parameters.AddWithValue("@id", id);
        u.ExecuteNonQuery();
    }

    // ══════════ ingest refresh(D5-6:追踪)══════════
    // sip ingest refresh [id | --stale | --all] [--json]
    // 重查原文(SSRF 通道)→ 幂等清洗 → 哈希比对 →
    //   没变(刷新 ObservedAt)/ 分级变化(建新版,旧版 superseded)/ 失效(标 invalid 不覆盖)/ 被拒(URL 策略,不动状态)
    // 默认只刷 stale 的 watch 目标;--all 全刷;id 刷单个
    static void IngestRefresh(string[] args, string dbPath, bool json)
    {
        bool all = args.Contains("--all", StringComparer.OrdinalIgnoreCase);
        var pos = args.Where(a => !a.StartsWith("--")).ToArray();
        long? id = pos.Length > 0 && long.TryParse(pos[0], out long i) ? i : null;

        var targets = new List<(long Id, string Url, string SourceKey, string? Title, string? SourceName, string? ObservedAt, int TtlDays)>();
        using (var conn = OpenDb(dbPath))
        {
            conn.Open();
            var q = conn.CreateCommand();
            // 保鲜看 ObservedAt(本地记录时间),不是 CapturedAt(原文声称时间)——双时态的 observed_at 才是"我上次确认"
            q.CommandText = "SELECT Id, SourceUrl, SourceKey, Title, SourceName, ObservedAt, TtlDays FROM Evidence WHERE Status = 'active' AND SourceType = 'watch' AND SourceUrl IS NOT NULL";
            if (id.HasValue) { q.CommandText += " AND Id = @id"; q.Parameters.AddWithValue("@id", id.Value); }
            q.CommandText += " ORDER BY Id";
            using var r = q.ExecuteReader();
            while (r.Read())
                targets.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                             r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                             r.IsDBNull(6) ? 7 : r.GetInt32(6)));
        }
        if (!all && !id.HasValue)
            targets = targets.Where(t => EvidenceIsStale(t.ObservedAt, t.TtlDays)).ToList();

        var results = new List<(long Id, string Result, string? Grade, bool Reversed, int Version, string? Error)>();
        foreach (var t in targets)
        {
            string? urlErr = ValidateFetchUrl(t.Url, LoadConfig(dbPath).AllowPrivateNet);
            if (urlErr != null) { results.Add((t.Id, "blocked", null, false, 0, urlErr)); continue; }

            string? text = FetchAndExtract(t.Url);
            if (string.IsNullOrWhiteSpace(text))
            {
                MarkEvidenceInvalid(dbPath, t.Id, Lang.T("Fetch failed: {0}", t.Url));
                results.Add((t.Id, "invalid", null, false, 0, Lang.T("Fetch failed: {0}", t.Url)));
                continue;
            }

            var (nid, status, err) = IngestStore(dbPath, "watch", t.SourceKey, t.SourceName, t.Url, t.Title,
                FirstChars(text, 300), text, null, t.TtlDays, null, skipPolishVersion: true);
            if (err != null) { results.Add((t.Id, "error", null, false, 0, err)); continue; }

            string? grade = null; bool reversed = false; int version = 0;
            if (status == "updated")
            {
                EmbedEvidence(dbPath, nid, text);
                AssignGroupFromVector(dbPath, nid);
                using (var conn = OpenDb(dbPath))
                {
                    conn.Open();
                    var q = conn.CreateCommand();
                    q.CommandText = "SELECT Version, Grade, Reversed FROM Evidence WHERE Id = @id";
                    q.Parameters.AddWithValue("@id", nid);
                    using var r = q.ExecuteReader();
                    if (r.Read()) { version = r.GetInt32(0); grade = r.IsDBNull(1) ? null : r.GetString(1); reversed = r.GetInt32(2) == 1; }
                }
            }
            results.Add((t.Id, status, grade, reversed, version, null));
        }

        if (json)
        {
            JsonOut(new
            {
                success = true,
                data = new
                {
                    refreshed = results.Select(x => new { id = x.Id, result = x.Result, grade = x.Grade, reversed = x.Reversed, version = x.Version, error = x.Error })
                }
            });
            return;
        }

        if (results.Count == 0) { Console.WriteLine(Lang.T("Nothing to refresh")); return; }
        foreach (var x in results)
        {
            switch (x.Result)
            {
                case "unchanged":
                    Console.WriteLine(Lang.T("Evidence #{0}: no change", x.Id));
                    break;
                case "polish":
                    Console.WriteLine(Lang.T("Evidence #{0}: polish (no new version)", x.Id));
                    break;
                case "updated":
                    Console.WriteLine(Lang.T("Evidence #{0}: updated to v{1} ({2})", x.Id, x.Version, GradeLabel(x.Grade))
                        + (x.Reversed ? Lang.T(" — reversed") : ""));
                    break;
                case "invalid":
                    Console.WriteLine(Lang.T("Evidence #{0}: invalid — {1}", x.Id, x.Error ?? ""));
                    break;
                case "blocked":
                    Console.WriteLine(Lang.T("Evidence #{0}: blocked — {1}", x.Id, x.Error ?? ""));
                    break;
                default:
                    Console.WriteLine(Lang.T("Evidence #{0}: {1}", x.Id, x.Error ?? ""));
                    break;
            }
        }
    }

    // ══════════ 语义去重 + 主题分组(D7-8;复用 embedding + EvidenceVectors)══════════

    // 存后:把证据向量写入 EvidenceVectors(复用 embedding 服务;失败静默,不阻塞存储)
    static void EmbedEvidence(string dbPath, long evidenceId, string text)
    {
        if (CurrentEmbeddingModelId(dbPath) == 0) return;   // 无 AI 不打扰
        try
        {
            var cfg = LoadConfig(dbPath);
            var vec = GetEmbeddingAsync(text, cfg).GetAwaiter().GetResult();
            if (vec == null || vec.Length == 0) return;
            int modelId = EnsureModel(dbPath, cfg.Embedding);
            using var conn = OpenDb(dbPath);
            conn.Open();
            var u = conn.CreateCommand();
            u.CommandText = "INSERT INTO EvidenceVectors (EvidenceId, ModelId, Vector, CreatedAt) VALUES (@e, @m, @v, @now) ON CONFLICT(EvidenceId, ModelId) DO UPDATE SET Vector = @v, CreatedAt = @now";
            u.Parameters.AddWithValue("@e", evidenceId);
            u.Parameters.AddWithValue("@m", modelId);
            u.Parameters.AddWithValue("@v", VectorToBytes(vec));
            u.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
            u.ExecuteNonQuery();
        }
        catch { }
    }

    // 存前语义去重:新内容 vs 库里 active 证据的向量(cos ≥ DedupSemanticThreshold)
    // 排除同 SourceKey(版本链不算重复);无 AI → 不检测
    static (long? DuplicateId, double Score) FindDuplicateEvidence(string dbPath, string sourceKey, string text)
    {
        if (CurrentEmbeddingModelId(dbPath) == 0) return (null, 0);
        double thr = LoadSettings().DedupSemanticThreshold;
        float[]? q;
        try { var cfg = LoadConfig(dbPath); q = GetEmbeddingAsync(text, cfg).GetAwaiter().GetResult(); }
        catch { return (null, 0); }
        if (q == null || q.Length == 0) return (null, 0);
        using var conn = OpenDb(dbPath);
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT v.EvidenceId, v.Vector FROM EvidenceVectors v JOIN Evidence e ON v.EvidenceId = e.Id WHERE e.Status = 'active' AND e.SourceKey != @k";
        cmd.Parameters.AddWithValue("@k", sourceKey);
        long? bestId = null; double best = 0;
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                float[] vec = BytesToVector(r.GetFieldValue<byte[]>(1));
                if (vec.Length != q.Length) continue;
                double score = CosineSimilarity(q, vec);
                if (score > best) { best = score; bestId = r.GetInt64(0); }
            }
        }
        return best >= thr ? (bestId, best) : (null, 0);
    }

    // 去重门:命中重复 → 脚本/Agent 自动跳过(不替你删);交互终端询问;返回是否跳过
    static (bool Skip, long? DuplicateId, double Score) CheckDuplicateInteractive(string dbPath, string sourceKey, string text, bool json)
    {
        var (dupId, score) = FindDuplicateEvidence(dbPath, sourceKey, text);
        if (!dupId.HasValue) return (false, null, 0);
        if (Console.IsInputRedirected)
        {
            if (json) JsonOut(new { success = true, data = new { skipped = true, duplicateOf = dupId.Value, similarity = score } });
            else Console.WriteLine(Lang.T("Duplicate: evidence #{0} already stores this (score {1:P0}) — skipped", dupId.Value, score));
            return (true, dupId, score);
        }
        Console.Write(Lang.T("Duplicate check: evidence #{0} (similarity {1:P0}). Store anyway? (y/n): ", dupId.Value, score));
        var line = Console.ReadLine();
        if (line == null || !line.Trim().Equals("y", StringComparison.OrdinalIgnoreCase))
        {
            if (json) JsonOut(new { success = true, data = new { skipped = true, duplicateOf = dupId.Value, similarity = score } });
            else Console.WriteLine(Lang.T("Skipped (not stored)"));
            return (true, dupId, score);
        }
        return (false, dupId, score);
    }

    static bool EvidenceIsVerified(string dbPath, long id)
    {
        using var conn = OpenDb(dbPath);
        conn.Open();
        var q = conn.CreateCommand();
        q.CommandText = "SELECT Verified FROM Evidence WHERE Id = @id";
        q.Parameters.AddWithValue("@id", id);
        var o = q.ExecuteScalar();
        return o != null && Convert.ToInt32(o) == 1;
    }

    // 归组:把证据归入最匹配的主题(cos ≥ GroupMatchThreshold;只归组不建组,主题由你定义)
    static void AssignGroupFromVector(string dbPath, long evidenceId)
    {
        try
        {
            double thr = LoadSettings().GroupMatchThreshold;
            using var conn = OpenDb(dbPath);
            conn.Open();
            var qv = conn.CreateCommand();
            qv.CommandText = "SELECT Vector FROM EvidenceVectors WHERE EvidenceId = @e";
            qv.Parameters.AddWithValue("@e", evidenceId);
            float[]? vec = null;
            using (var r = qv.ExecuteReader())
            {
                if (r.Read()) vec = BytesToVector(r.GetFieldValue<byte[]>(0));
            }
            if (vec == null || vec.Length == 0) return;
            var q = conn.CreateCommand();
            q.CommandText = "SELECT Id, Centroid FROM Groups";
            long? bestId = null; double best = 0;
            using (var r = q.ExecuteReader())
            {
                while (r.Read())
                {
                    float[] c = BytesToVector(r.GetFieldValue<byte[]>(1));
                    if (c.Length != vec.Length) continue;
                    double s = CosineSimilarity(vec, c);
                    if (s > best) { best = s; bestId = r.GetInt64(0); }
                }
            }
            if (bestId.HasValue && best >= thr)
            {
                var u = conn.CreateCommand();
                u.CommandText = "UPDATE Evidence SET GroupId = @g, Consensus = @c WHERE Id = @e";
                u.Parameters.AddWithValue("@g", bestId.Value);
                u.Parameters.AddWithValue("@c", EvidenceConsensus(dbPath, (int)bestId.Value, evidenceId, EvidenceIsVerified(dbPath, evidenceId)));
                u.Parameters.AddWithValue("@e", evidenceId);
                u.ExecuteNonQuery();
            }
        }
        catch { }
    }

    // group add 后:把未分组且有向量的证据立即尝试归组
    static void MatchUnassigned(string dbPath)
    {
        var ids = new List<long>();
        using (var conn = OpenDb(dbPath))
        {
            conn.Open();
            var q = conn.CreateCommand();
            q.CommandText = "SELECT Id FROM Evidence WHERE Status = 'active' AND GroupId IS NULL";
            using var r = q.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt64(0));
        }
        foreach (var id in ids) AssignGroupFromVector(dbPath, id);
    }

    // sip ingest group add <label> [--seed <query>] | rename <N> <new> | rm <N>
    static void IngestGroup(string[] args, string dbPath, bool json)
    {
        var pos = args.Where(a => !a.StartsWith("--")).ToArray();
        if (pos.Length < 2)
        { IngestUsage(json, Lang.T("Usage: sip ingest group add <label> [--seed <query>] | rename <N> <new> | rm <N>")); return; }
        string action = pos[1].ToLowerInvariant();

        if (action == "add")
        {
            if (pos.Length < 3)
            { IngestUsage(json, Lang.T("Usage: sip ingest group add <label> [--seed <query>]")); return; }
            string label = pos[2];
            string seedText = ArgValue(args, "--seed") ?? label;
            if (CurrentEmbeddingModelId(dbPath) == 0)
            { ReportError("NO_INDEX", Lang.T("Topic grouping needs AI embedding configured (sip --init)"), json: json); return; }
            float[]? vec;
            try { var cfg = LoadConfig(dbPath); vec = GetEmbeddingAsync(seedText, cfg).GetAwaiter().GetResult(); }
            catch { ReportError("MODEL_UNAVAILABLE", Lang.T("Embedding service unavailable"), json: json); return; }
            if (vec == null || vec.Length == 0)
            { ReportError("EMBED_FAILED", Lang.T("Embedding service returned an empty vector"), json: json); return; }
            int modelId = EnsureModel(dbPath, LoadConfig(dbPath).Embedding);
            string now = DateTime.Now.ToString("O");
            long gid;
            using (var conn = OpenDb(dbPath))
            {
                conn.Open();
                var ins = conn.CreateCommand();
                ins.CommandText = "INSERT INTO Groups (Label, Centroid, ModelId, CreatedAt, UpdatedAt) VALUES (@l, @c, @m, @now, @now)";
                ins.Parameters.AddWithValue("@l", label);
                ins.Parameters.AddWithValue("@c", VectorToBytes(vec));
                ins.Parameters.AddWithValue("@m", modelId);
                ins.Parameters.AddWithValue("@now", now);
                ins.ExecuteNonQuery();
                var lid = conn.CreateCommand();
                lid.CommandText = "SELECT last_insert_rowid()";
                gid = Convert.ToInt64(lid.ExecuteScalar());
            }
            MatchUnassigned(dbPath);   // 已有未分组证据立即尝试匹配
            if (json) JsonOut(new { success = true, data = new { id = gid, label, seeded = seedText } });
            else Console.WriteLine(Lang.T("Topic #{0} \"{1}\" added (centroid seeded from: {2})", gid, label, seedText));
            return;
        }

        if (action == "rename")
        {
            if (pos.Length < 4 || !long.TryParse(pos[2], out long rid))
            { IngestUsage(json, Lang.T("Usage: sip ingest group rename <N> <new name>")); return; }
            string newLabel = string.Join(" ", pos.Skip(3));
            using (var conn = OpenDb(dbPath))
            {
                conn.Open();
                var u = conn.CreateCommand();
                u.CommandText = "UPDATE Groups SET Label = @l, UpdatedAt = @now WHERE Id = @id";
                u.Parameters.AddWithValue("@l", newLabel);
                u.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
                u.Parameters.AddWithValue("@id", rid);
                int n = u.ExecuteNonQuery();
                if (n == 0) { ReportError("GROUP_NOT_FOUND", Lang.T("Topic #{0} not found", rid), json: json); return; }
            }
            if (json) JsonOut(new { success = true, data = new { id = rid, label = newLabel } });
            else Console.WriteLine(Lang.T("Topic #{0} renamed to \"{1}\"", rid, newLabel));
            return;
        }

        if (action == "rm")
        {
            if (pos.Length < 3 || !long.TryParse(pos[2], out long did))
            { IngestUsage(json, Lang.T("Usage: sip ingest group rm <N>")); return; }
            using (var conn = OpenDb(dbPath))
            {
                conn.Open();
                using var tx = conn.BeginTransaction();
                var upd = conn.CreateCommand(); upd.Transaction = tx;
                upd.CommandText = "UPDATE Evidence SET GroupId = NULL WHERE GroupId = @id";
                upd.Parameters.AddWithValue("@id", did);
                int moved = upd.ExecuteNonQuery();
                var del = conn.CreateCommand(); del.Transaction = tx;
                del.CommandText = "DELETE FROM Groups WHERE Id = @id";
                del.Parameters.AddWithValue("@id", did);
                int n = del.ExecuteNonQuery();
                tx.Commit();
                if (n == 0) { ReportError("GROUP_NOT_FOUND", Lang.T("Topic #{0} not found", did), json: json); return; }
                if (json) JsonOut(new { success = true, data = new { id = did, moved } });
                else Console.WriteLine(Lang.T("Topic #{0} removed ({1} evidence moved back to ungrouped)", did, moved));
            }
            return;
        }

        IngestUsage(json, Lang.T("Usage: sip ingest group add <label> [--seed <query>] | rename <N> <new> | rm <N>"));
    }

    // sip ingest groups [--json] —— 主题列表(你定义的主题 + 各主题证据数)
    static void IngestGroups(string dbPath, bool json)
    {
        var rows = new List<(long Id, string Label, int Count)>();
        using (var conn = OpenDb(dbPath))
        {
            conn.Open();
            var q = conn.CreateCommand();
            q.CommandText = "SELECT g.Id, g.Label, (SELECT COUNT(*) FROM Evidence e WHERE e.GroupId = g.Id AND e.Status = 'active') FROM Groups g ORDER BY g.Id";
            using var r = q.ExecuteReader();
            while (r.Read()) rows.Add((r.GetInt64(0), r.GetString(1), r.GetInt32(2)));
        }
        if (json)
        {
            JsonOut(new { success = true, data = new { count = rows.Count, groups = rows.Select(x => new { id = x.Id, label = x.Label, count = x.Count }) } });
            return;
        }
        if (rows.Count == 0) { Console.WriteLine(Lang.T("No topics yet. Define one: sip ingest group add <label>")); return; }
        Console.WriteLine(Lang.T("Topics ({0}):", rows.Count));
        foreach (var x in rows) Console.WriteLine(Lang.T("  [{0}] {1}（{2} 条证据）", x.Id, x.Label, x.Count));
    }

    // ══════════ ingest retrieve + ask(D9-10:使用层收官)══════════

    // 检索命中条目(证据随行:原文片段/来源/版本/新鲜度/核实/共识/主题/分级/反转/被改过)
    class RetrieveHit
    {
        public long Id;
        public string Title = "";
        public string? Source;
        public string? SourceUrl;
        public string? Excerpt;
        public int Version;
        public string? Freshness;
        public bool Verified;
        public double Consensus;
        public int? Group;
        public string? Grade;
        public bool Reversed;
        public string? CapturedAt;
        public bool HasDiff;    // 该 SourceKey 有被改过的历史(RAG 带 diff 状态)
        public double Score;
    }

    // 检索核心:有 AI → 语义近邻;无 AI/失败 → LIKE 全文兜底
    static List<RetrieveHit> RetrieveHits(string dbPath, string query, int top, int? group)
    {
        var scored = new List<(long Id, double Score)>();
        if (CurrentEmbeddingModelId(dbPath) != 0)
        {
            try
            {
                var cfg = LoadConfig(dbPath);
                var qv = GetEmbeddingAsync(query, cfg).GetAwaiter().GetResult();
                if (qv != null && qv.Length > 0)
                {
                    using var conn = OpenDb(dbPath);
                    conn.Open();
                    var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT v.EvidenceId, v.Vector FROM EvidenceVectors v JOIN Evidence e ON v.EvidenceId = e.Id WHERE e.Status = 'active'";
                    if (group.HasValue) { cmd.CommandText += " AND e.GroupId = @g"; cmd.Parameters.AddWithValue("@g", group.Value); }
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        float[] vec = BytesToVector(r.GetFieldValue<byte[]>(1));
                        if (vec.Length != qv.Length) continue;
                        double s = CosineSimilarity(qv, vec);
                        if (s >= LoadConfig(dbPath).Embedding.SearchThreshold)
                            scored.Add((r.GetInt64(0), s));
                    }
                }
            }
            catch { /* 语义失败 → 降级 LIKE */ }
        }
        if (scored.Count == 0)
        {
            using var conn = OpenDb(dbPath);
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id FROM Evidence WHERE Status = 'active' AND (Title LIKE @q OR Excerpt LIKE @q OR Content LIKE @q)";
            if (group.HasValue) { cmd.CommandText += " AND GroupId = @g"; cmd.Parameters.AddWithValue("@g", group.Value); }
            cmd.Parameters.AddWithValue("@q", "%" + query + "%");
            using var r = cmd.ExecuteReader();
            while (r.Read()) scored.Add((r.GetInt64(0), 1.0));
        }
        return scored.OrderByDescending(x => x.Score).Take(top)
                     .Select(x => LoadRetrieveHit(dbPath, x.Id, x.Score))
                     .Where(x => x != null).ToList()!;
    }

    static RetrieveHit? LoadRetrieveHit(string dbPath, long id, double score)
    {
        using var conn = OpenDb(dbPath);
        conn.Open();
        var q = conn.CreateCommand();
        q.CommandText = @"SELECT e.Title, e.SourceName, e.SourceUrl, e.Excerpt, e.Version, e.Freshness, e.Verified, e.Consensus, e.GroupId, e.Grade, e.Reversed, e.CapturedAt,
                          (SELECT COUNT(*) FROM Evidence x WHERE x.SourceKey = e.SourceKey AND x.Status = 'superseded') AS HasDiff
                          FROM Evidence e WHERE e.Id = @id";
        q.Parameters.AddWithValue("@id", id);
        using var r = q.ExecuteReader();
        if (!r.Read()) return null;
        return new RetrieveHit
        {
            Id = id,
            Title = r.IsDBNull(0) ? "" : r.GetString(0),
            Source = r.IsDBNull(1) ? null : r.GetString(1),
            SourceUrl = r.IsDBNull(2) ? null : r.GetString(2),
            Excerpt = r.IsDBNull(3) ? null : r.GetString(3),
            Version = r.GetInt32(4),
            Freshness = r.IsDBNull(5) ? null : r.GetString(5),
            Verified = r.GetInt32(6) == 1,
            Consensus = r.IsDBNull(7) ? 0 : r.GetDouble(7),
            Group = r.IsDBNull(8) ? null : (int?)r.GetInt32(8),
            Grade = r.IsDBNull(9) ? null : r.GetString(9),
            Reversed = r.GetInt32(10) == 1,
            CapturedAt = r.IsDBNull(11) ? null : r.GetString(11),
            HasDiff = r.GetInt64(12) > 0,
            Score = score
        };
    }

    // sip ingest retrieve <query> [--top N] [--group N] [--json]
    static void IngestRetrieve(string[] args, string dbPath, bool json)
    {
        var pos = args.Where(a => !a.StartsWith("--")).ToArray();
        string query = pos.Length > 1 ? string.Join(" ", pos.Skip(1)) : "";
        if (string.IsNullOrWhiteSpace(query))
        { IngestUsage(json, Lang.T("Usage: sip ingest retrieve <query> [--top N] [--group N] [--json]")); return; }
        int top = ArgInt(args, "--top") ?? 5;
        int? group = ArgInt(args, "--group");
        if (top < 1) top = 5;

        var hits = RetrieveHits(dbPath, query, top, group);
        if (json)
        {
            JsonOut(new
            {
                success = true,
                data = new
                {
                    query,
                    count = hits.Count,
                    hits = hits.Select(h => new
                    {
                        id = h.Id,
                        title = h.Title,
                        source = h.Source,
                        sourceUrl = h.SourceUrl,
                        excerpt = h.Excerpt,
                        version = h.Version,
                        freshness = h.Freshness,
                        verified = h.Verified,
                        consensus = h.Consensus,
                        group = h.Group,
                        grade = h.Grade,
                        reversed = h.Reversed,
                        hasDiff = h.HasDiff,
                        capturedAt = h.CapturedAt,
                        score = h.Score
                    })
                }
            });
            return;
        }
        if (hits.Count == 0) { Console.WriteLine(Lang.T("No evidence matches \"{0}\"", query)); return; }
        Console.WriteLine(Lang.T("Evidence for \"{0}\" ({1}):", query, hits.Count));
        foreach (var h in hits)
        {
            string extra = (h.HasDiff ? " ✎被改过" : "") + (h.Reversed ? " 🔴反转" : "") + (h.Grade != null ? " " + GradeLabel(h.Grade) : "");
            Console.WriteLine($"[{h.Id}] {StripControlChars(h.Title)}  ({h.Source ?? "-"})  v{h.Version} · {h.Freshness ?? "fresh"} · {(h.Verified ? Lang.T("你核实过") : Lang.T("未核实"))} · {h.Score:P1}{extra}");
            if (!string.IsNullOrEmpty(h.SourceUrl)) Console.WriteLine($"    {h.SourceUrl}");
            if (!string.IsNullOrEmpty(h.Excerpt)) Console.WriteLine($"    {FirstChars(h.Excerpt, 120)}");
        }
    }

    // sip ingest ask "<问题>" [--json]
    // 使用层:只摘录不转述 + 不知道就说不知道 + 命中证据并现呈现。
    // 答案只能由检索到的 excerpt 逐字摘录(引用带来源+版本+新鲜度);库里没有 → 直接说不知道。
    static void IngestAsk(string[] args, string dbPath, bool json)
    {
        var pos = args.Where(a => !a.StartsWith("--")).ToArray();
        string question = pos.Length > 1 ? string.Join(" ", pos.Skip(1)) : "";
        if (string.IsNullOrWhiteSpace(question))
        { IngestUsage(json, Lang.T("Usage: sip ingest ask <question> [--json]")); return; }

        var hits = RetrieveHits(dbPath, question, 5, null);
        if (hits.Count == 0)
        {
            // 不知道就说不知道(诚实的不确定性;不生成式填空)
            if (json) JsonOut(new { success = true, data = new { answer = Lang.T("I don't know — no evidence in your library covers this"), evidence = Array.Empty<object>() } });
            else Console.WriteLine(Lang.T("I don't know — no evidence in your library covers this"));
            return;
        }

        // prompt(发给 LLM;摘录纪律写死):只摘录不转述,证据不足说不知道
        var sb = new StringBuilder();
        sb.AppendLine("You are a strict local-facts assistant. Answer ONLY from the evidence excerpts below. Rules:");
        sb.AppendLine("1. Quote the original excerpt VERBATIM. Never paraphrase, never fabricate.");
        sb.AppendLine("2. Every citation must carry [evidence#N v<version> <freshness>] plus its source URL.");
        sb.AppendLine("3. If the evidence is insufficient, answer exactly: I don't know.");
        sb.AppendLine();
        for (int i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            sb.AppendLine($"#{i + 1} source: {h.SourceUrl ?? h.Source ?? "-"} · v{h.Version} {h.Freshness ?? "fresh"}" + (h.HasDiff ? " · has-been-edited" : "") + (h.Reversed ? " · reversed-stance" : ""));
            sb.AppendLine($"excerpt: {h.Excerpt ?? ""}");
            sb.AppendLine();
        }
        sb.AppendLine($"Question: {question}");

        try
        {
            EnsureAiPrompted();
            var cfg = LoadConfig(dbPath);
            string? answer = CallLlmAsync(sb.ToString(), cfg).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(answer)) answer = Lang.T("(empty answer — LLM returned nothing)");
            if (json)
                JsonOut(new
                {
                    success = true,
                    data = new
                    {
                        answer = answer.Trim(),
                        evidence = hits.Select(h => new { id = h.Id, title = h.Title, sourceUrl = h.SourceUrl, version = h.Version, freshness = h.Freshness, grade = h.Grade, reversed = h.Reversed, hasDiff = h.HasDiff, excerpt = h.Excerpt })
                    }
                });
            else Console.WriteLine(answer.Trim());
        }
        catch (AiException ex)
        {
            ReportError(ex.Code, ex.Message, ex.Suggestion, ex.Details, json);
        }
        catch (HttpRequestException ex)
        {
            ReportError("NETWORK_ERROR", Lang.T("LLM request failed"), null, ex.Message, json);
        }
    }

    // ══════════ 子命令实现 ══════════

    // sip ingest --stdin [--origin <url>] [--producer <name>] [--title <t>] [--ttl <days>] [--yes] [--json]
    static void IngestStdin(string[] args, string dbPath, bool json)
    {
        string origin = ArgValue(args, "--origin") ?? "";
        string producer = ArgValue(args, "--producer") ?? "stdin";
        string? title = ArgValue(args, "--title");
        int ttl = ParseTtl(args);

        string content = Console.In.ReadToEnd();
        if (string.IsNullOrWhiteSpace(content))
        { ReportError("EMPTY_STDIN", Lang.T("Empty stdin: nothing to store"), json: json); return; }

        string sourceKey = "evidence:" + producer;
        if (!args.Contains("--force", StringComparer.OrdinalIgnoreCase))
        {
            var (skip, _, _) = CheckDuplicateInteractive(dbPath, sourceKey, content, json);
            if (skip) return;
        }
        string? excerpt = string.IsNullOrEmpty(origin) ? null : FirstChars(content, 300);
        var (id, status, err) = IngestStore(dbPath, "evidence", sourceKey, producer,
            string.IsNullOrEmpty(origin) ? null : origin, title, excerpt, content, null, ttl, null);
        if (err != null) { ReportError("STORE_FAILED", err, json: json); return; }
        if (status is "created" or "updated")
        {
            EmbedEvidence(dbPath, id, content);
            AssignGroupFromVector(dbPath, id);
        }

        if (json) JsonOut(new { success = true, data = new { id, status, sourceType = "evidence" } });
        else Console.WriteLine(status switch
        {
            "created" => Lang.T("Stored evidence #{0} from stdin", id),
            "unchanged" => Lang.T("No change: evidence #{0} already stored", id),
            _ => Lang.T("Evidence #{0} updated to a new version", id)
        });
    }

    // sip ingest --evidence <file|--stdin> [--yes] [--json]
    static void IngestEvidence(string[] args, string dbPath, bool json)
    {
        string? file = null;
        int idx = Array.FindIndex(args, a => a.Equals("--evidence", StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--")) file = args[idx + 1];

        string raw;
        if (string.IsNullOrEmpty(file) || file.Equals("--stdin", StringComparison.OrdinalIgnoreCase))
            raw = Console.In.ReadToEnd();
        else
        {
            try { raw = File.ReadAllText(file); }
            catch (Exception ex) { ReportError("READ_FAILED", Lang.T("Cannot read {0}: {1}", file, ex.Message), json: json); return; }
        }
        if (string.IsNullOrWhiteSpace(raw)) { ReportError("EMPTY_EVIDENCE", Lang.T("Empty evidence package"), json: json); return; }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException) { ReportError("INVALID_JSON", Lang.T("Invalid JSON in evidence package"), json: json); return; }
        using (doc)
        {
            var root = doc.RootElement;
            string? schema = root.TryGetProperty("schema", out var s) ? s.GetString() : null;
            if (schema != "sip-evidence-v1")
            { ReportError("SCHEMA_MISMATCH", Lang.T("Invalid evidence package: schema must be sip-evidence-v1"), json: json); return; }

            string sourceType = root.TryGetProperty("source_type", out var st) ? (st.GetString() ?? "evidence") : "evidence";
            if (sourceType != "evidence" && sourceType != "watch")
            { ReportError("SCHEMA_MISMATCH", Lang.T("Invalid evidence package: source_type must be 'evidence' or 'watch'"), json: json); return; }

            string? content = StrProp(root, "content");
            string? excerpt = StrProp(root, "excerpt");
            if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(excerpt))
            { ReportError("SCHEMA_MISMATCH", Lang.T("Invalid evidence package: content or excerpt required"), json: json); return; }

            string sourceKey = StrProp(root, "source_key")
                ?? (sourceType == "watch" ? "watch:" + NormalizeWatchUrl(StrProp(root, "source_url") ?? "?") : "evidence:import");
            int ttl = root.TryGetProperty("ttl_days", out var t) && t.TryGetInt32(out int tn) && tn > 0 ? tn : 0;
            string? capturedAt = StrProp(root, "captured_at");
            string? pm = root.TryGetProperty("producer_meta", out var p) ? p.GetRawText() : null;

            string textForDedup = content ?? excerpt ?? "";
            if (!args.Contains("--force", StringComparer.OrdinalIgnoreCase))
            {
                var (skip, _, _) = CheckDuplicateInteractive(dbPath, sourceKey, textForDedup, json);
                if (skip) return;
            }
            var (id, status, err) = IngestStore(dbPath, sourceType, sourceKey,
                StrProp(root, "source_name"), StrProp(root, "source_url"), StrProp(root, "title"),
                excerpt, content ?? "", pm, ttl, capturedAt);
            if (err != null) { ReportError("STORE_FAILED", err, json: json); return; }
            if (status is "created" or "updated")
            {
                EmbedEvidence(dbPath, id, textForDedup);
                AssignGroupFromVector(dbPath, id);
            }

            bool verified = root.TryGetProperty("verified", out var vv) && vv.ValueKind == JsonValueKind.True;
            if (verified) IngestConfirm(id, dbPath, json);

            if (json) JsonOut(new { success = true, data = new { id, status, sourceType } });
            else Console.WriteLine(status switch
            {
                "created" => Lang.T("Imported evidence #{0}", id),
                "unchanged" => Lang.T("No change: evidence #{0} already imported", id),
                _ => Lang.T("Evidence #{0} updated to a new version", id)
            });
        }
    }

    // sip ingest --url <url> [--ttl <days>] [--yes] [--json]
    static void IngestUrl(string url, string[] args, string dbPath, bool json)
    {
        int ttl = ParseTtl(args);
        string? urlErr = ValidateFetchUrl(url, LoadConfig(dbPath).AllowPrivateNet);
        if (urlErr != null) { ReportError("NETWORK_ERROR", urlErr, json: json); return; }

        string? text = FetchAndExtract(url);
        if (string.IsNullOrWhiteSpace(text)) { ReportError("NETWORK_ERROR", Lang.T("Fetch failed: {0}", url), json: json); return; }

        string sourceKey = "watch:" + NormalizeWatchUrl(url);
        if (!args.Contains("--force", StringComparer.OrdinalIgnoreCase))
        {
            var (skip, _, _) = CheckDuplicateInteractive(dbPath, sourceKey, text, json);
            if (skip) return;
        }
        var (id, status, err) = IngestStore(dbPath, "watch", sourceKey, null, url, null, FirstChars(text, 300), text, null, ttl, null);
        if (err != null) { ReportError("STORE_FAILED", err, json: json); return; }
        if (status is "created" or "updated")
        {
            EmbedEvidence(dbPath, id, text);
            AssignGroupFromVector(dbPath, id);
        }

        if (json) JsonOut(new { success = true, data = new { id, status, sourceType = "watch", url } });
        else Console.WriteLine(status switch
        {
            "created" => Lang.T("Stored web page as evidence #{0}", id),
            "unchanged" => Lang.T("No change: web page evidence #{0} already stored", id),
            _ => Lang.T("Web page evidence #{0} updated to a new version", id)
        });
    }

    // sip ingest list [--stale] [--group N] [--json]
    static void IngestList(string[] args, string dbPath, bool json)
    {
        bool staleOnly = args.Contains("--stale", StringComparer.OrdinalIgnoreCase);
        int? group = ArgInt(args, "--group");

        var rows = new List<(long Id, string Title, string? Source, string SourceType, string? SourceUrl,
                              int Version, string? Grade, int Reversed, int Verified, double Consensus, int? GroupId,
                              string? Freshness, bool Stale, string? CapturedAt, string? Excerpt)>();
        using (var conn = OpenDb(dbPath))
        {
            conn.Open();
            var q = conn.CreateCommand();
            // 保鲜看 ObservedAt(本地记录时间);CapturedAt 仅展示
            q.CommandText = "SELECT Id, Title, SourceName, SourceType, SourceUrl, Version, Grade, Reversed, Verified, Consensus, GroupId, Freshness, TtlDays, ObservedAt, CapturedAt, Excerpt FROM Evidence WHERE Status = 'active'";
            if (group.HasValue) { q.CommandText += " AND GroupId = @g"; q.Parameters.AddWithValue("@g", group.Value); }
            q.CommandText += " ORDER BY Id DESC";
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                bool stale = EvidenceIsStale(r.IsDBNull(13) ? null : r.GetString(13), r.IsDBNull(12) ? 7 : r.GetInt32(12));
                if (staleOnly && !stale) continue;
                rows.Add((
                    r.GetInt64(0),
                    r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? null : r.GetString(2),
                    r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.GetInt32(5),
                    r.IsDBNull(6) ? null : r.GetString(6),
                    r.GetInt32(7),
                    r.GetInt32(8),
                    r.IsDBNull(9) ? 0 : r.GetDouble(9),
                    r.IsDBNull(10) ? null : (int?)r.GetInt32(10),
                    r.IsDBNull(11) ? null : r.GetString(11),
                    stale,
                    r.IsDBNull(13) ? null : r.GetString(13),
                    r.IsDBNull(14) ? null : r.GetString(14)));
            }
        }

        if (json)
        {
            JsonOut(new
            {
                success = true,
                data = new
                {
                    count = rows.Count,
                    items = rows.Select(x => new
                    {
                        id = x.Id,
                        title = x.Title,
                        source = x.Source,
                        sourceType = x.SourceType,
                        sourceUrl = x.SourceUrl,
                        version = x.Version,
                        grade = x.Grade,
                        reversed = x.Reversed == 1,
                        verified = x.Verified == 1,
                        consensus = x.Consensus,
                        group = x.GroupId,
                        freshness = x.Freshness,
                        stale = x.Stale,
                        capturedAt = x.CapturedAt,
                        excerpt = x.Excerpt
                    })
                }
            });
            return;
        }

        if (rows.Count == 0)
        { Console.WriteLine(staleOnly ? Lang.T("No stale evidence") : Lang.T("No evidence yet. Store some: sip ingest --stdin")); return; }
        Console.WriteLine(Lang.T("Evidence ({0}):", rows.Count));
        foreach (var x in rows)
        {
            string grade = GradeLabel(x.Grade);
            string fr = x.Stale ? "stale" : (x.Freshness ?? "fresh");
            string ver = x.Verified == 1 ? Lang.T("verified by you") : Lang.T("not verified");
            Console.WriteLine($"[{x.Id}] {StripControlChars(x.Title)}  ({x.Source ?? "-"})  v{x.Version} · {fr} · {ver} · {grade}");
        }
    }

    // sip ingest show <id> [--json]
    static void IngestShow(int id, string dbPath, bool json)
    {
        using var conn = OpenDb(dbPath);
        conn.Open();
        var q = conn.CreateCommand();
        q.CommandText = "SELECT Schema, SourceType, SourceKey, SourceName, SourceUrl, Title, Excerpt, Content, Hash, Version, Status, StatusNote, Grade, Reversed, CapturedAt, ObservedAt, Verified, ConfirmedAt, Freshness, TtlDays, Consensus, GroupId, DynamicPage FROM Evidence WHERE Id = @id";
        q.Parameters.AddWithValue("@id", id);
        using var r = q.ExecuteReader();
        if (!r.Read()) { ReportError("EVIDENCE_NOT_FOUND", Lang.T("Evidence #{0} not found", id), json: json); return; }

        if (json)
        {
            JsonOut(new
            {
                success = true,
                data = new
                {
                    id,
                    schema = r.GetString(0),
                    sourceType = r.GetString(1),
                    sourceKey = r.GetString(2),
                    source = r.IsDBNull(3) ? null : r.GetString(3),
                    sourceUrl = r.IsDBNull(4) ? null : r.GetString(4),
                    title = r.IsDBNull(5) ? null : r.GetString(5),
                    excerpt = r.IsDBNull(6) ? null : r.GetString(6),
                    content = r.IsDBNull(7) ? null : r.GetString(7),
                    hash = r.IsDBNull(8) ? null : r.GetString(8),
                    version = r.GetInt32(9),
                    status = r.GetString(10),
                    statusNote = r.IsDBNull(11) ? null : r.GetString(11),
                    grade = r.IsDBNull(12) ? null : r.GetString(12),
                    reversed = r.GetInt32(13) == 1,
                    capturedAt = r.IsDBNull(14) ? null : r.GetString(14),
                    observedAt = r.IsDBNull(15) ? null : r.GetString(15),
                    verified = r.GetInt32(16) == 1,
                    confirmedAt = r.IsDBNull(17) ? null : r.GetString(17),
                    freshness = r.IsDBNull(18) ? null : r.GetString(18),
                    ttlDays = r.IsDBNull(19) ? 0 : r.GetInt32(19),
                    consensus = r.IsDBNull(20) ? 0 : r.GetDouble(20),
                    group = r.IsDBNull(21) ? null : (int?)r.GetInt32(21),
                    dynamicPage = r.GetInt32(22) == 1
                }
            });
            return;
        }

        Console.WriteLine(Lang.T("Evidence #{0} · {1} · v{2} · {3}", id, r.GetString(10), r.GetInt32(9), r.GetString(1)));
        Console.WriteLine(Lang.T("  source:  {0}", r.IsDBNull(3) ? "-" : r.GetString(3)));
        Console.WriteLine(Lang.T("  url:     {0}", r.IsDBNull(4) ? "-" : r.GetString(4)));
        Console.WriteLine(Lang.T("  title:   {0}", r.IsDBNull(5) ? "-" : r.GetString(5)));
        Console.WriteLine(Lang.T("  captured:{0}  observed:{1}", r.IsDBNull(14) ? "-" : r.GetString(14), r.IsDBNull(15) ? "-" : r.GetString(15)));
        Console.WriteLine(Lang.T("  hash:    {0}", r.IsDBNull(8) ? "-" : r.GetString(8)));
        Console.WriteLine("─────────────────────");
        string body = r.IsDBNull(7) ? "" : r.GetString(7);
        if (body.Length > 2000) body = body[..2000] + "\n…";
        Console.WriteLine(body.Length > 0 ? body : (r.IsDBNull(6) ? "-" : r.GetString(6)));
    }

    // sip ingest confirm <id> [--json] —— 审核门槛:只有你核实过才算 verified
    static void IngestConfirm(long id, string dbPath, bool json)
    {
        using var conn = OpenDb(dbPath);
        conn.Open();
        var q = conn.CreateCommand();
        q.CommandText = "SELECT GroupId FROM Evidence WHERE Id = @id";
        q.Parameters.AddWithValue("@id", id);
        using var r = q.ExecuteReader();
        if (!r.Read()) { ReportError("EVIDENCE_NOT_FOUND", Lang.T("Evidence #{0} not found", id), json: json); return; }
        int? group = r.IsDBNull(0) ? null : r.GetInt32(0);
        r.Close();

        string now = DateTime.Now.ToString("O");
        double cons = EvidenceConsensus(dbPath, group, id, verified: true);
        var u = conn.CreateCommand();
        u.CommandText = "UPDATE Evidence SET Verified = 1, ConfirmedAt = @now, Consensus = @c WHERE Id = @id";
        u.Parameters.AddWithValue("@now", now);
        u.Parameters.AddWithValue("@c", cons);
        u.Parameters.AddWithValue("@id", id);
        u.ExecuteNonQuery();

        if (json) JsonOut(new { success = true, data = new { id, verified = true, confirmedAt = now, consensus = cons } });
        else Console.WriteLine(Lang.T("Evidence #{0} confirmed — verified by you", id));
    }

    // sip ingest rm <id> [--yes] [--json] —— 轻存易删:删证据+向量,watch 首快照解除关联
    static void IngestRm(int id, string dbPath, bool json, bool yes)
    {
        using (var conn = OpenDb(dbPath))
        {
            conn.Open();
            var q = conn.CreateCommand();
            q.CommandText = "SELECT COUNT(*) FROM Evidence WHERE Id = @id";
            q.Parameters.AddWithValue("@id", id);
            var o = q.ExecuteScalar();
            if (o == null || Convert.ToInt64(o) == 0) { ReportError("EVIDENCE_NOT_FOUND", Lang.T("Evidence #{0} not found", id), json: json); return; }
        }
        if (!yes && Console.IsInputRedirected)
        { ReportError("CONFIRM_REQUIRED", Lang.T("Removal needs confirmation: add --yes (non-interactive)"), json: json); return; }
        if (!yes)
        {
            Console.Write(Lang.T("Confirm removal of evidence #{0}? (y/n): ", id));
            var line = Console.ReadLine();
            if (line == null || !line.Trim().Equals("y", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine(Lang.T("Cancelled")); return; }
        }

        using var conn2 = OpenDb(dbPath);
        conn2.Open();
        using var tx = conn2.BeginTransaction();
        var d1 = conn2.CreateCommand(); d1.Transaction = tx;
        d1.CommandText = "DELETE FROM EvidenceVectors WHERE EvidenceId = @id"; d1.Parameters.AddWithValue("@id", id); d1.ExecuteNonQuery();
        var d2 = conn2.CreateCommand(); d2.Transaction = tx;
        d2.CommandText = "UPDATE WatchTargets SET FirstEvidenceId = NULL WHERE FirstEvidenceId = @id"; d2.Parameters.AddWithValue("@id", id); d2.ExecuteNonQuery();
        var d3 = conn2.CreateCommand(); d3.Transaction = tx;
        d3.CommandText = "DELETE FROM Evidence WHERE Id = @id"; d3.Parameters.AddWithValue("@id", id); d3.ExecuteNonQuery();
        tx.Commit();

        if (json) JsonOut(new { success = true, data = new { id, deleted = true } });
        else Console.WriteLine(Lang.T("Removed evidence #{0}", id));
    }

    // ══════════ 辅助 ══════════

    // 内容指纹(版本即事实的"身份证";变了才建新版)
    static string EvidenceHash(string text)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // 保鲜:ObservedAt + TtlDays < now → stale(双时态:过期与否看"本地记录时间")
    static bool EvidenceIsStale(string? observedAt, int ttlDays)
    {
        if (string.IsNullOrEmpty(observedAt)) return true;
        if (!DateTime.TryParse(observedAt, out var at)) return true;
        return at.AddDays(ttlDays) < DateTime.Now;
    }

    // 共识分(公式写进文档):min(1, 0.5×Verified + 0.5×min(1, 同主题异源数/3))
    // 只作参考,不替你判断;producer_meta 永不进共识
    static double EvidenceConsensus(string dbPath, int? groupId, long selfId, bool verified)
    {
        double vPart = verified ? 0.5 : 0.0;
        double sPart = 0.0;
        if (groupId.HasValue)
        {
            using var conn = OpenDb(dbPath);
            conn.Open();
            var q = conn.CreateCommand();
            q.CommandText = "SELECT COUNT(DISTINCT SourceName) FROM Evidence WHERE GroupId = @g AND Status = 'active' AND Id != @id AND SourceName IS NOT NULL";
            q.Parameters.AddWithValue("@g", groupId.Value);
            q.Parameters.AddWithValue("@id", selfId);
            var o = q.ExecuteScalar();
            long n = o == null || o is DBNull ? 0 : Convert.ToInt64(o);
            sPart = 0.5 * Math.Min(1.0, n / 3.0);
        }
        return Math.Min(1.0, vPart + sPart);
    }

    // watch URL 归一化:取 scheme+host+path,去尾部斜杠(同一网页永远同一 SourceKey)
    static string NormalizeWatchUrl(string url)
    {
        url = (url ?? "").Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out var u))
            return u.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return url.TrimEnd('/');
    }

    static string FirstChars(string s, int n) => s.Length <= n ? s : s[..n];

    // --name <value> 取选项值;不存在或下一个是 -- 开头 → null
    static string? ArgValue(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i < 0 || i + 1 >= args.Length) return null;
        string v = args[i + 1];
        return v.StartsWith("--") ? null : v;
    }

    static int? ArgInt(string[] args, string name)
    {
        string? v = ArgValue(args, name);
        return int.TryParse(v, out int n) ? n : null;
    }

    // --ttl <days>:0=按源类型默认(url=7 / evidence=30);非法或超界 → 默认
    static int ParseTtl(string[] args)
    {
        string? ttl = ArgValue(args, "--ttl");
        if (!string.IsNullOrWhiteSpace(ttl) && int.TryParse(ttl, out int n) && n > 0 && n <= 3650) return n;
        return 0;
    }

    static string? StrProp(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
}
