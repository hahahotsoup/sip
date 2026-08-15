// 百万级基准库生成器:100 个源 × 每源 1 万篇 = 100 万篇。
// 近期(48h 内)1 万篇,其余历史(用于窗口类命令的基准)。
// 用法: node bench-gen.js <rss.db 路径>
const { DatabaseSync } = require('node:sqlite');
const db = new DatabaseSync(process.argv[2]);

const FEEDS = 100;
const PER_FEED = 10000;          // 100 万篇
const RECENT = 10000;            // 48h 内的篇数(窗口命令只处理这些)
const KEYWORDS = ['熊猫', '量子计算', 'RAG架构', '开源许可证', '终端安全', '数据库', '分布式', '编译器'];

db.exec('PRAGMA journal_mode = WAL');
db.exec('PRAGMA synchronous = OFF');
db.exec('PRAGMA foreign_keys = ON');

console.log('建 Feeds...');
db.exec('BEGIN');
const feedStmt = db.prepare("INSERT INTO Feeds (Id, Title, FeedUrl, LastCheckedAt) VALUES (?, ?, ?, ?)");
for (let f = 1; f <= FEEDS; f++) {
  feedStmt.run(f, `源${f}`, `http://feed${f}.example.com/feed.xml`, null);
}
db.exec('COMMIT');

console.log('建 Items(100 万)...');
const kwPool = [];
for (let i = 0; i < 200; i++) {
  const kw = KEYWORDS[i % KEYWORDS.length];
  const body = [];
  for (let j = 0; j < 12; j++) body.push(`这是第 ${i}-${j} 段关于${kw}的正文内容,讨论${kw}的实践与${kw}的工程问题。`);
  kwPool.push(kw + ' ' + body.join('\n'));
}

const now = Date.now();
const HOUR = 3600_000;
const day = 24 * HOUR;
const itemStmt = db.prepare(
  "INSERT INTO Items (Id, FeedId, Title, Link, Description, Content, Guid, Status, Version, PublishDate) VALUES (?,?,?,?,?,?,?,?,1,?)"
);
let recentCount = 0;
let id = 0;
db.exec('BEGIN');
const t0 = Date.now();
for (let f = 1; f <= FEEDS; f++) {
  for (let k = 0; k < PER_FEED; k++) {
    id++;
    const pool = kwPool[(f * 31 + k) % kwPool.length];
    const kw = pool.split(' ')[0];
    // 前 RECENT 篇(按 id 顺序)为近期,其余历史
    const isRecent = id <= RECENT;
    const pub = isRecent
      ? new Date(now - (id % 40) * HOUR).toISOString()
      : new Date(now - (30 + (id % 700)) * day).toISOString();
    if (isRecent) recentCount++;
    const content = isRecent ? pool : `历史文章 ${id}:` + pool.substring(0, 200);
    itemStmt.run(
      id, f,
      `标题${id} ${kw}`, `http://feed${f}.example.com/a${id}`,
      `摘要 ${kw} 相关内容。`, content,
      `guid-${id}`, 'active', pub
    );
  }
  if (f % 20 === 0) {
    db.exec('COMMIT'); db.exec('BEGIN');
    console.log(`  ${f}/${FEEDS} 源,${id} 篇,${((Date.now() - t0) / 1000).toFixed(1)}s`);
  }
}
db.exec('COMMIT');
console.log(`完成: ${id} 篇(近期 ${recentCount}),耗时 ${((Date.now() - t0) / 1000).toFixed(1)}s`);
db.close();
