// 向语言文件追加缺失的 key(不覆盖已有;保持顺序与 2 空格格式;保留 BOM)
// 用法: node add-lang-keys.js <lang.json> <newkeys.json>
// newkeys.json: { "key": "value", ... }(顶层对象)
const fs = require('fs');
const file = process.argv[2];
let addRaw = fs.readFileSync(process.argv[3], 'utf8');
if (addRaw.charCodeAt(0) === 0xFEFF) addRaw = addRaw.slice(1);   // 兼容 BOM 输入
const add = JSON.parse(addRaw);
let raw = fs.readFileSync(file, 'utf8');
const hasBom = raw.charCodeAt(0) === 0xFEFF;
if (hasBom) raw = raw.slice(1);
const obj = JSON.parse(raw);
let added = 0;
for (const [k, v] of Object.entries(add)) {
  if (!(k in obj)) { obj[k] = v; added++; }
}
const out = JSON.stringify(obj, null, 2) + '\n';
fs.writeFileSync(file, (hasBom ? '\uFEFF' : '') + out);
console.log(`${file}: added ${added} keys`);
