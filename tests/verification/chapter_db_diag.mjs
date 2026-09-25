// 只读取证：咸的玩笑的 Chapters 行（ord/chapterId/kind/title），
// 以及**独立**从 EPUB 原文件里找"长顺"那句落在哪个 spine 文件 —— 两边一对照，
// 就知道"第 1 章"到底指哪一章、用户的划词该落在哪一章。
// 全程只读副本与只读 zip，不碰 sip 的库和进程。
import { copyFileSync, existsSync, mkdtempSync, readFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";
import { execFileSync } from "node:child_process";

const dir = process.argv[2];
const tmp = mkdtempSync(join(tmpdir(), "sipdiag-"));
for (const s of ["", "-wal", "-shm"]) {
  const src = join(dir, "rss.db" + s);
  if (existsSync(src)) copyFileSync(src, join(tmp, "rss.db" + s));
}
const db = new DatabaseSync(join(tmp, "rss.db"), { readOnly: true });
const q = (sql, ...a) => { try { return db.prepare(sql).all(...a); } catch (e) { return [{ error: e.message }]; } };

console.log("=== 导入项 ===");
for (const r of q(`SELECT i.Id, i.Title, i.Link, i.PageCount FROM Items i JOIN Feeds f ON i.FeedId=f.Id WHERE f.FeedUrl='local://import' ORDER BY i.Id`))
  console.log(`  #${r.Id} 《${r.Title}》 link=${r.Link}`);

const book = q(`SELECT i.Id, i.Title, i.Link FROM Items i JOIN Feeds f ON i.FeedId=f.Id WHERE f.FeedUrl='local://import' AND i.Title LIKE '%咸的玩笑%' LIMIT 1`)[0];
if (!book) { console.log("没找到咸的玩笑"); process.exit(0); }
console.log(`\n选中 #${book.Id} 《${book.Title}》`);
console.log(`文件: ${book.Link}  存在=${existsSync(book.Link)}`);

console.log("\n=== Chapters 行 ===");
for (const r of q(`SELECT Ord, ChapterId, Kind, Source, COALESCE(Title,'') Title, CharCount, Locator FROM Chapters WHERE ItemId=? ORDER BY Ord`, book.Id))
  console.log(`  ord=${String(r.Ord).padStart(2)}  ${String(r.ChapterId).padEnd(46)} kind=${String(r.Kind).padEnd(8)} tocChar=${String(r.CharCount).padStart(7)} title=${JSON.stringify(r.Title)}`);

console.log("\n=== 独立取证：'长顺' 出现在 EPUB 的哪个文件里 ===");
try {
  const py = `
import zipfile, re, sys, json
p = sys.argv[1]
kw = "长顺"
z = zipfile.ZipFile(p)
names = [n for n in z.namelist() if n.lower().endswith((".xhtml", ".html", ".htm"))]
hits = []
for n in sorted(names):
    try: raw = z.read(n).decode("utf-8", "replace")
    except Exception: continue
    txt = re.sub(r"<[^>]+>", "", raw)
    if kw in txt:
        i = txt.find(kw)
        hits.append({"file": n, "count": txt.count(kw), "snippet": txt[max(0,i-40):i+60].strip()})
print(json.dumps(hits, ensure_ascii=False, indent=1))
`;
  const out = execFileSync("python", ["-c", py, book.Link], { encoding: "utf8", maxBuffer: 32 * 1024 * 1024 });
  console.log(out);
} catch (e) {
  console.log("  解包失败: " + e.message);
}
