// 只读盘点用户库的源与文章，挑一篇能当 A43 夹具的（有正文的 RSS 文章）。
import { copyFileSync, existsSync, mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";

const dir = process.argv[2];
const tmp = mkdtempSync(join(tmpdir(), "inv-"));
for (const s of ["", "-wal", "-shm"]) {
  const p = join(dir, "rss.db" + s);
  if (existsSync(p)) copyFileSync(p, join(tmp, "rss.db" + s));
}
const db = new DatabaseSync(join(tmp, "rss.db"), { readOnly: true });

console.log("=== 源 ===");
for (const r of db.prepare("SELECT Id,Title,FeedUrl FROM Feeds ORDER BY Id").all()) {
  const n = db.prepare("SELECT COUNT(*) c FROM Items WHERE FeedId = ? AND Status != 'dedup'").get(r.Id);
  console.log(`  #${r.Id} 《${r.Title}》 url=${r.FeedUrl}  文章 ${n.c}`);
}

console.log("\n=== 可当 A43 夹具的文章（RSS 源、有正文）===");
const rows = db.prepare(
  `SELECT i.Id, i.Title, LENGTH(i.Content) AS len
   FROM Items i JOIN Feeds f ON i.FeedId = f.Id
   WHERE f.FeedUrl <> 'local://import' AND i.Status != 'dedup'
   ORDER BY LENGTH(i.Content) DESC LIMIT 6`).all();
for (const x of rows) {
  console.log(`  #${x.Id} 《${String(x.Title).slice(0, 44)}》 contentLen=${x.len}`);
}
if (!rows.length) console.log("  （没有）");
db.close();
