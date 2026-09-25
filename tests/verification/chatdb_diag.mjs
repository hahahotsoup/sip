// 只读查 chat.db：重启后聊天记录到底还在不在库里。
// 这决定了问题是"没存"还是"存了但没读出来"—— 两者修法完全不同。
import { copyFileSync, existsSync, mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { DatabaseSync } from "node:sqlite";

const dir = process.argv[2];
const tmp = mkdtempSync(join(tmpdir(), "chatdiag-"));
for (const s of ["", "-wal", "-shm"]) {
  const p = join(dir, "chat.db" + s);
  if (existsSync(p)) copyFileSync(p, join(tmp, "chat.db" + s));
}
const p = join(tmp, "chat.db");
if (!existsSync(p)) { console.log("chat.db 不存在: " + dir); process.exit(0); }

const db = new DatabaseSync(p, { readOnly: true });
const q = (sql, ...a) => { try { return db.prepare(sql).all(...a); } catch (e) { return [{ error: e.message }]; } };

console.log("=== 表 ===");
for (const r of q("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")) console.log("  " + r.name);

console.log("\n=== 会话 ===");
const sessions = q("SELECT Id, ItemId, Title, TurnCount, UpdatedAt, CreatedAt FROM ChatSessions ORDER BY UpdatedAt DESC LIMIT 20");
for (const r of sessions) {
  console.log(`  ${r.Id}  item=${r.ItemId}  turns=${r.TurnCount}  upd=${r.UpdatedAt}  title=${JSON.stringify(String(r.Title || "").slice(0, 30))}`);
}
if (!sessions.length) console.log("  （没有任何会话）");

console.log("\n=== 消息总数 ===");
const total = q("SELECT COUNT(*) AS n FROM ChatMessages");
console.log("  " + (total[0]?.n ?? total[0]?.error));

console.log("\n=== 最近的 12 条消息 ===");
for (const r of q(`SELECT Id, SessionId, TurnIndex, Role, Status, ErrorCode, CreatedAt,
                          substr(Content,1,50) AS head, LENGTH(Content) AS len
                   FROM ChatMessages ORDER BY Id DESC LIMIT 12`)) {
  console.log(`  #${r.Id} sess=${String(r.SessionId).slice(0, 18)}… turn=${r.TurnIndex} ${r.Role} st=${r.Status} len=${r.len}  ${JSON.stringify(String(r.head || ""))}`);
}

console.log("\n=== 每个会话的消息数（对账 TurnCount）===");
for (const r of q(`SELECT s.Id, s.ItemId, s.TurnCount,
                          (SELECT COUNT(*) FROM ChatMessages m WHERE m.SessionId = s.Id) AS msgs
                   FROM ChatSessions s ORDER BY s.UpdatedAt DESC LIMIT 10`)) {
  console.log(`  ${r.Id}  item=${r.ItemId}  TurnCount=${r.TurnCount}  实际消息=${r.msgs}`);
}
db.close();
