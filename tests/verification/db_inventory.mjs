// 升级前后数据盘点：把 .db/.db-wal/.db-shm 一起复制到临时目录再读 ——
// 只读副本，绝不碰活库（sip 正跑着，WAL 模式下少一个写入者就少一个变量）。
import { copyFileSync, existsSync, mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";

const [label, dir] = process.argv.slice(2);
const tmp = mkdtempSync(join(tmpdir(), "sipdb-"));
for (const suffix of ["", "-wal", "-shm"]) {
  const src = join(dir, "rss.db" + suffix);
  if (existsSync(src)) copyFileSync(src, join(tmp, "rss.db" + suffix));
}
const dbPath = join(tmp, "rss.db");
if (!existsSync(dbPath)) { console.log(`[${label}] rss.db 不存在: ${dir}`); process.exit(1); }

const db = new DatabaseSync(dbPath, { readOnly: true });
const q = (sql) => { try { return db.prepare(sql).all(); } catch (e) { return [{ error: e.message }]; } };
const one = (sql) => { const r = q(sql); return r[0] ?? {}; };

console.log(`\n######## ${label} ########`);
console.log(`目录: ${dir}`);

console.log("\n-- 基本表 --");
for (const [name, sql] of [
  ["Feeds", "SELECT COUNT(*) n FROM Feeds"],
  ["Items", "SELECT COUNT(*) n FROM Items"],
  ["Imports", "SELECT COUNT(*) n FROM Imports"],
  ["Chapters", "SELECT COUNT(*) n FROM Chapters"],
  ["PdfPages", "SELECT COUNT(*) n FROM PdfPages"],
  ["ChatSessions", "SELECT COUNT(*) n FROM ChatSessions"],
  ["ChatMessages", "SELECT COUNT(*) n FROM ChatMessages"],
  ["Vectors", "SELECT COUNT(*) n FROM Vectors"],
  ["VectorMeta", "SELECT COUNT(*) n FROM VectorMeta"],
]) {
  const r = one(sql);
  console.log(`  ${name.padEnd(16)} ${r.error ? "ERR " + r.error : r.n}`);
}

console.log("\n-- 导入的书 --");
for (const r of q("SELECT Id, Title, Kind, Pages, Chapters, ChaptersSource FROM Imports ORDER BY Id")) {
  console.log(`  #${r.Id} ${String(r.Title).slice(0, 34).padEnd(36)} kind=${r.Kind} pages=${r.Pages} chapters=${r.Chapters} src=${r.ChaptersSource}`);
}
if (!q("SELECT 1 FROM Imports LIMIT 1").length) console.log("  (无导入)");

console.log("\n-- 章节按来源 --");
for (const r of q("SELECT Source, COUNT(*) n FROM Chapters GROUP BY Source")) console.log(`  ${r.Source}: ${r.n}`);
if (!q("SELECT 1 FROM Chapters LIMIT 1").length) console.log("  (Chapters 表空)");

console.log("\n-- Chapters 列（确认新列就位）--");
const cols = q("PRAGMA table_info(Chapters)").map(r => r.name);
console.log("  " + (cols.length ? cols.join(", ") : "(表不存在)"));
const pcols = q("PRAGMA table_info(PdfPages)").map(r => r.name);
console.log("  PdfPages: " + (pcols.length ? pcols.join(", ") : "(表不存在)"));

console.log("\n-- 文章标题抽样（确认内容没被动过）--");
for (const r of q("SELECT Id, substr(Title,1,44) t FROM Items ORDER BY Id LIMIT 5")) console.log(`  #${r.Id} ${r.t}`);

db.close();
