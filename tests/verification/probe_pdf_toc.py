#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""PDF 专属嫌疑探针：导入一本真 PDF，把 --toc/--page/--locate 的 rc/stdout/stderr 与库状态全量落下来。

来由（anchor-eng 交底，非我推测）：他的自测里 `1123.pdf` 跑 `--toc 1 --json` **stdout 为空、`PdfPages` 0 行**（两次），
而我的合成 EPUB2 跑同一命令 **rc=0、3 章**。两相对照 → 嫌疑收窄到 PDF 分支：
`EnsurePdfPages`（抽取/写库/指纹）、`PdfHasTextLayerFile`（重开一次 PDF 抽样 8 页）、`TocPayload` 的 PDF 分支。
他明确要求：**在 PDF 上跑并把 stderr 留下**，且"别排除真有 PDF 专属缺陷"。

用法：python tests/verification/probe_pdf_toc.py "<path.pdf>" [关键词]
输出 JSON（含产品快照哈希，因为产品树在动）。
"""
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
sys.stdout.reconfigure(encoding="utf-8")
from sip_driver import SipInstance, SCRATCH_ROOT  # noqa: E402


def sha12(path):
    import hashlib
    if not os.path.exists(path):
        return None
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for c in iter(lambda: fh.read(1 << 20), b""):
            h.update(c)
    return h.hexdigest()[:12]


def probe(pdf, keyword="第"):
    out = {"pdf": pdf, "steps": []}

    def step(label, rc, so, se, extra=None):
        rec = {"label": label, "rc": rc, "stdoutLen": len(so or ""), "stderrLen": len(se or ""),
               "stdout": (so or "")[:1500], "stderr": (se or "")[:1500]}
        if extra:
            rec.update(extra)
        out["steps"].append(rec)
        return rec

    with SipInstance() as inst:
        out["snapshot"] = {
            "sipDll": sha12(os.path.join(inst.root, "sip.dll")),
            "sipExeMtime": time.strftime("%m-%d %H:%M:%S",
                                         time.localtime(os.path.getmtime(os.path.join(inst.root, "sip.dll")))),
            "scratch": SCRATCH_ROOT,
        }
        out["gateVerified"] = getattr(inst, "_gate_verified", None)

        rc, so, se = inst.run("--import", pdf, "--title", "PDFProbe", timeout=600)
        step("--import <pdf>", rc, so, se)
        rows = inst.sql("SELECT Id,Title,Status,PageCount,Link FROM Items")
        out["items"] = rows
        if not rows:
            out["verdict"] = "import 未产生 Items 行 —— 后续无从进行"
            print(json.dumps(out, ensure_ascii=False, indent=2))
            return 1
        item_id = str(rows[0][0])
        out["pageCountColumn"] = rows[0][3]
        out["link"] = rows[0][4]

        for label, args in (
            ("--toc <id>（人类可读）", ("--toc", item_id),),
            ("--toc <id> --json", ("--toc", item_id, "--json"),),
            ("--page <id> 1", ("--page", item_id, "1"),),
            ("--page <id> 1 --json", ("--page", item_id, "1", "--json"),),
            ("--locate <kw> --book <id>", ("--locate", keyword, "--book", item_id),),
            ("--chapterize <id>（回填）", ("--chapterize", item_id),),
        ):
            t0 = time.time()
            rc, so, se = inst.run(*args, timeout=600)
            step(label, rc, so, se, extra={"ms": int((time.time() - t0) * 1000)})

        # 库状态：这是"到底写没写"的唯一硬证据
        def count(sql):
            try:
                return inst.sql(sql)[0][0]
            except Exception as ex:  # noqa: BLE001
                return f"ERR {ex}"

        out["db"] = {
            "items": count("SELECT COUNT(*) FROM Items"),
            "chapters": count("SELECT COUNT(*) FROM Chapters") if "Chapters" in inst.tables() else "no table",
            "pdfPages": count("SELECT COUNT(*) FROM PdfPages") if "PdfPages" in inst.tables() else "no table",
            "dbMeta": inst.sql("SELECT Key,Value FROM DbMeta") if "DbMeta" in inst.tables() else "no table",
        }
        if "PdfPages" in inst.tables():
            out["db"]["pdfPagesSample"] = inst.sql(
                "SELECT Page,CharCount,substr(Text,1,60) FROM PdfPages ORDER BY Page LIMIT 5")
        if "Chapters" in inst.tables():
            out["db"]["chaptersSample"] = inst.sql(
                "SELECT ChapterId,Ord,Depth,Title,PageStart,PageEnd,ZeroLength,Source FROM Chapters ORDER BY Ord LIMIT 8") \
                if "ZeroLength" in inst.columns("Chapters") else \
                inst.sql("SELECT * FROM Chapters ORDER BY Ord LIMIT 8")
            out["db"]["chaptersCols"] = inst.columns("Chapters")
        out["tables"] = inst.tables()
        out["verdict"] = ("--toc 有输出" if any(s["label"].startswith("--toc <id> --json") and s["stdoutLen"] > 0
                                                 for s in out["steps"]) else "--toc --json stdout 为空（复现 anchor-eng 的现象）")
    print(json.dumps(out, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(__doc__)
        raise SystemExit(3)
    raise SystemExit(probe(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else "第"))
