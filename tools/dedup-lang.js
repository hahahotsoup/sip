// 语言文件去重:只删除「顶层重复 key」的重复条目行(保留第一个)。
// 带字符串状态机,忽略字符串内的 { } 与引号,避免占位符 {0} 干扰深度计数。
// 用法: node dedup-lang.js <file.json> [<file.json> ...]
const fs = require('fs');

for (const file of process.argv.slice(2)) {
  const raw = fs.readFileSync(file, 'utf8');
  const lines = raw.split(/\r?\n/);
  let depth = 0;
  const seen = new Set();
  const out = [];
  let removed = 0;
  let lastTopLevelIdx = -1;   // 最后一个「顶层条目起始行」的索引

  for (const line of lines) {
    // 1) 判断当前行是否为「顶层条目」(深度1 + 行首为 "key": )
    if (depth === 1) {
      const m = line.trimStart().match(/^"((?:[^"\\]|\\.)*)"\s*:/);
      if (m) {
        const k = m[1];
        if (seen.has(k)) { removed++; continue; }
        seen.add(k);
        lastTopLevelIdx = out.length;
      }
    }
    out.push(line);

    // 2) 更新深度:只统计字符串外的 { }(处理转义)
    let inStr = false, esc = false;
    for (const ch of line) {
      if (esc) { esc = false; continue; }
      if (inStr) {
        if (ch === '\\') esc = true;
        else if (ch === '"') inStr = false;
      } else {
        if (ch === '"') inStr = true;
        else if (ch === '{') depth++;
        else if (ch === '}') depth--;
      }
    }
  }

  // 3) 清理「顶层最后条目」的悬空尾逗号:去重删行后,最后条目的尾逗号可能悬空
  if (lastTopLevelIdx >= 0) {
    const last = out[lastTopLevelIdx];
    const t = last.trimEnd();
    if (t.endsWith(',')) out[lastTopLevelIdx] = t.slice(0, -1);
  }

  fs.writeFileSync(file, out.join('\r\n'));
  console.log(`${file}: removed ${removed} duplicate top-level keys`);
}
