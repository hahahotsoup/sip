#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""独立取证探针：不经过 sip 自己的解析器，直接读真实 EPUB / PDF 的真值（ground truth）。

存在理由：验证「章节/页码识别是否真实」时，若拿产品自己的解析结果当期望值，
就是自己验证自己。这里用第三方库（zipfile / PyMuPDF / pypdf）独立取真值：

  EPUB : OPF 版本、toc.ncx 的 navMap（标题+顺序+层级）、nav.xhtml（EPUB3）、spine 顺序
  PDF  : 书签目录 get_toc()（标题/层级/页号）、页数、逐页文本层长度（判定扫描件）

用法：
  python tests/verification/fixture_probe.py epub "<path.epub>"
  python tests/verification/fixture_probe.py pdf  "<path.pdf>"
  python tests/verification/fixture_probe.py scan-dir "<dir>" [--limit N]
  python tests/verification/fixture_probe.py pick            # 从 Downloads 里挑候选夹具

输出统一为 UTF-8 JSON（stdout），退出码 0=成功 / 2=解析失败 / 3=参数错误。
"""
import json
import os
import sys
import zipfile

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")


# ───────────────────────── EPUB ─────────────────────────

def _read(zf, name):
    with zf.open(name) as fh:
        return fh.read()


def probe_epub(path):
    out = {"path": path, "kind": "epub", "ok": False}
    if not zipfile.is_zipfile(path):
        out["error"] = "not a zip/epub container (End of Central Directory missing)"
        return out
    with zipfile.ZipFile(path) as zf:
        names = zf.namelist()
        out["entryCount"] = len(names)

        # container.xml -> OPF 路径（EPUB 规范路径；不猜）
        opf_path = None
        if "META-INF/container.xml" in names:
            c = _read(zf, "META-INF/container.xml").decode("utf-8", "replace")
            i = c.find("full-path=")
            if i >= 0:
                q = c[i + len("full-path="):]
                quote = q[0]
                opf_path = q[1:q.find(quote, 1)]
        if not opf_path:
            cands = [n for n in names if n.lower().endswith(".opf")]
            opf_path = cands[0] if cands else None
        out["opfPath"] = opf_path
        if not opf_path:
            out["error"] = "no OPF package document"
            return out

        import xml.etree.ElementTree as ET
        opf = ET.fromstring(_read(zf, opf_path))
        pkg_ver = opf.get("version")
        out["opfVersion"] = pkg_ver
        # EPUB3 判据用 package version，不用书里有没有 ncx（不少 EPUB3 也带 ncx 兜底）
        out["isEpub3"] = bool(pkg_ver and pkg_ver.startswith("3"))

        ns = {"o": "http://www.idpf.org/2007/opf", "x": "http://www.w3.org/1999/xhtml"}
        manifest = {}
        for it in opf.findall(".//o:manifest/o:item", ns) or opf.findall(".//manifest/item"):
            manifest[it.get("id")] = (it.get("href"), it.get("media-type"), it.get("properties") or "")
        base = os.path.dirname(opf_path)

        def resolve(href):
            p = os.path.normpath(os.path.join(base, href)).replace("\\", "/")
            return p

        spine = []
        for ir in (opf.findall(".//o:spine/o:itemref", ns) or opf.findall(".//spine/itemref")):
            href = manifest.get(ir.get("idref"), (None, None, None))[0]
            if href:
                spine.append(resolve(href))
        out["spine"] = spine
        out["spineCount"] = len(spine)

        # nav.xhtml（EPUB3 目录）
        nav_props = [i for i in manifest.items() if "nav" in (i[1][2] or "")]
        out["navHref"] = resolve(nav_props[0][1][0]) if nav_props else None
        if out["navHref"] and out["navHref"] in names:
            try:
                root = ET.fromstring(_read(zf, out["navHref"]))
                nav = None
                for n in root.iter("{http://www.w3.org/1999/xhtml}nav"):
                    if (n.get("epub:type") or n.get("{http://www.idpf.org/2007/ops}type")) == "toc":
                        nav = n
                        break
                if nav is None:
                    navs = list(root.iter("{http://www.w3.org/1999/xhtml}nav"))
                    nav = navs[0] if navs else None
                items = []
                if nav is not None:
                    def walk(ol, depth):
                        for li in ol.findall("{http://www.w3.org/1999/xhtml}li"):
                            a = li.find("{http://www.w3.org/1999/xhtml}a")
                            if a is not None:
                                items.append({"depth": depth, "title": (a.text or "").strip(),
                                              "href": resolve(a.get("href") or "")})
                            for sub in li.findall("{http://www.w3.org/1999/xhtml}ol"):
                                walk(sub, depth + 1)
                    for ol in nav.findall("{http://www.w3.org/1999/xhtml}ol"):
                        walk(ol, 0)
                out["navToc"] = items
                out["navTocCount"] = len(items)
            except Exception as e:  # noqa: BLE001
                out["navError"] = f"{type(e).__name__}: {e}"

        # toc.ncx（EPUB2 目录）
        ncx = [n for n in names if n.lower().endswith(".ncx")]
        out["ncxCount"] = len(ncx)
        if ncx:
            path_ncx = ncx[0]
            out["ncxPath"] = path_ncx
            try:
                root = ET.fromstring(_read(zf, path_ncx))
                nns = {"n": "http://www.daisy.org/z3986/2005/ncx/"}
                navmap = root.find("n:navMap", nns)
                items = []
                if navmap is not None:
                    def walk_ncx(np, depth):
                        for npv in np.findall("n:navPoint", nns):
                            lbl = npv.find("n:navLabel/n:text", nns)
                            cnt = npv.find("n:content", nns)
                            items.append({"depth": depth,
                                          "title": (lbl.text or "").strip() if lbl is not None else "",
                                          "src": cnt.get("src") if cnt is not None else ""})
                            walk_ncx(npv, depth + 1)
                    walk_ncx(navmap, 0)
                out["ncxToc"] = items
                out["ncxTocCount"] = len(items)
            except Exception as e:  # noqa: BLE001
                out["ncxError"] = f"{type(e).__name__}: {e}"

    out["ok"] = True
    return out


# ───────────────────────── PDF ─────────────────────────

def probe_pdf(path):
    import fitz  # PyMuPDF
    out = {"path": path, "kind": "pdf", "ok": False}
    doc = fitz.open(path)
    try:
        out["pageCount"] = doc.page_count
        toc = doc.get_toc(simple=True)  # [[level, title, page1based], ...]
        out["toc"] = toc
        out["tocCount"] = len(toc)
        out["hasBookmarks"] = len(toc) > 0
        textlens = []
        for pno in range(doc.page_count):
            textlens.append(len(doc.load_page(pno).get_text("text").strip()))
        out["textLenPerPage"] = textlens[:200]
        total = sum(textlens)
        out["totalTextChars"] = total
        # 扫描件判据：几乎全页无文本层
        pages_with_text = sum(1 for n in textlens if n > 20)
        out["pagesWithText"] = pages_with_text
        out["looksScanned"] = (doc.page_count > 0 and pages_with_text <= max(1, doc.page_count // 20))
        out["meta"] = {k: v for k, v in (doc.metadata or {}).items() if v}
        # 层级是否越级（level 只能 +1 递增）
        violations = []
        prev = 0
        for lvl, title, page in toc:
            if lvl > prev + 1 and prev > 0:
                violations.append({"title": title, "page": page, "level": lvl, "prevLevel": prev})
            prev = lvl
        out["levelJumps"] = violations
    finally:
        doc.close()
    out["ok"] = True
    return out


# ─────────────────── 目录映射分析（对准契约 §1.4.1）───────────────────
# 逐条预测：按 navPoint 建行应得多少行、哪些条目会被丢弃、哪些会撞键、
# 哪些会是零长度章、会压平多少层、锚点找不找得到。
# 这些数字就是验收 A22 的**精确**期望值（不用"约等于"）。
def toc_map(path):
    import re
    import urllib.parse as up
    out = {"path": path, "kind": "epub-tocmap", "ok": False}
    if not zipfile.is_zipfile(path):
        out["error"] = "not a zip/epub container"
        return out
    with zipfile.ZipFile(path) as zf:
        names = zf.namelist()
        opf_path = None
        if "META-INF/container.xml" in names:
            c = _read(zf, "META-INF/container.xml").decode("utf-8", "replace")
            i = c.find("full-path=")
            if i >= 0:
                q = c[i + len("full-path="):]
                opf_path = q[1:q.find(q[0], 1)]
        opf_path = opf_path or next((n for n in names if n.lower().endswith(".opf")), None)
        if not opf_path:
            out["error"] = "no OPF"
            return out
        import xml.etree.ElementTree as ET
        opf = ET.fromstring(_read(zf, opf_path))
        ns = {"o": "http://www.idpf.org/2007/opf"}
        base = os.path.dirname(opf_path)
        manifest = {it.get("id"): it.get("href") for it in
                    (opf.findall(".//o:manifest/o:item", ns) or opf.findall(".//manifest/item"))}

        def resolve(href):
            return os.path.normpath(os.path.join(base, href)).replace("\\", "/")

        def resolve_from(dirname, href):
            """NCX 的 content/@src 与 nav 的 a/@href 都相对**它们自己所在文件**的目录解析。"""
            return os.path.normpath(os.path.join(dirname, href)).replace("\\", "/")

        spine = [resolve(manifest[r.get("idref")]) for r in
                 (opf.findall(".//o:spine/o:itemref", ns) or opf.findall(".//spine/itemref"))
                 if manifest.get(r.get("idref"))]
        spine_set = set(spine)
        out["spineCount"] = len(spine)

        # 目录项：EPUB3 nav 优先，退化到 ncx（与契约优先级一致）
        items = []          # {title, depth, file, anchorRaw}
        source = None
        nav_items = [i for i in manifest.items() if i[1]]
        nav_href = None
        for _iid, href in nav_items:
            it = opf.find(f".//o:manifest/o:item[@id='{_iid}']", ns)
            if it is not None and "nav" in (it.get("properties") or ""):
                nav_href = resolve(href)
        if nav_href and nav_href in names:
            source = "nav"
            nav_dir = os.path.dirname(nav_href)
            root = ET.fromstring(_read(zf, nav_href))
            X = "{http://www.w3.org/1999/xhtml}"
            nav = None
            for n in root.iter(X + "nav"):
                if (n.get("{http://www.idpf.org/2007/ops}type") or "") == "toc":
                    nav = n
                    break
            if nav is None:
                nav = next(iter(root.iter(X + "nav")), None)

            def walk3(ol, depth):
                for li in ol.findall(X + "li"):
                    a = li.find(X + "a")
                    if a is not None:
                        items.append({"title": (a.text or "").strip(), "depth": depth,
                                      "href": resolve_from(nav_dir, a.get("href") or "")})
                    for sub in li.findall(X + "ol"):
                        walk3(sub, depth + 1)
            for ol in (nav.findall(X + "ol") if nav is not None else []):
                walk3(ol, 0)
        else:
            ncx = next((n for n in names if n.lower().endswith(".ncx")), None)
            if ncx:
                source = "ncx"
                ncx_dir = os.path.dirname(ncx)
                root = ET.fromstring(_read(zf, ncx))
                nns = {"n": "http://www.daisy.org/z3986/2005/ncx/"}
                navmap = root.find("n:navMap", nns)

                def walk2(np, depth):
                    for npv in np.findall("n:navPoint", nns):
                        lbl = npv.find("n:navLabel/n:text", nns)
                        cnt = npv.find("n:content", nns)
                        items.append({"title": (lbl.text or "").strip() if lbl is not None else "",
                                      "depth": depth,
                                      "href": resolve_from(ncx_dir, cnt.get("src") or "") if cnt is not None else ""})
                        walk2(npv, depth + 1)
                if navmap is not None:
                    walk2(navmap, 0)
        out["tocSource"] = source
        out["navPointCount"] = len(items)
        if not items:
            out["ok"] = True
            return out

        depths = {}
        for it in items:
            depths[it["depth"]] = depths.get(it["depth"], 0) + 1
        out["depthHistogram"] = {str(k): v for k, v in sorted(depths.items())}
        out["maxDepth"] = max(depths) if depths else 0
        out["depth7Plus"] = sum(v for k, v in depths.items() if k + 1 > 6)  # 需压平的条目数

        frag_cache = {}

        def fragment(f):
            if f not in frag_cache:
                try:
                    frag_cache[f] = _read(zf, f).decode("utf-8", "replace")
                except KeyError:
                    frag_cache[f] = None
            return frag_cache[f]

        dropped, no_anchor, html4_name, html5_id, same_file_groups = [], [], 0, 0, {}
        located = []      # (spineIdx, offset, order, item)
        for order, it in enumerate(items):
            href = it["href"] or ""
            if "#" in href:
                f, _, raw_anchor = href.partition("#")
            else:
                f, raw_anchor = href, ""
            it["file"], it["anchor"] = f, raw_anchor
            if f not in spine_set:
                dropped.append({"title": it["title"], "file": f})
                it["dropped"] = True
                continue
            text = fragment(f)
            if text is None:
                dropped.append({"title": it["title"], "file": f, "reason": "entry missing"})
                it["dropped"] = True
                continue
            anchor = up.unquote(raw_anchor)
            off = -1
            if anchor:
                m = re.search(r'id\s*=\s*["\']' + re.escape(anchor) + r'["\']', text, re.I)
                if m:
                    html5_id += 1
                    off = m.start()
                else:
                    m = re.search(r'name\s*=\s*["\']' + re.escape(anchor) + r'["\']', text, re.I)
                    if m:
                        html4_name += 1
                        off = m.start()
                if off < 0:
                    no_anchor.append({"title": it["title"], "file": f, "anchor": anchor})
            located.append((spine.index(f), off, order, it))
            key = (f, anchor if off >= 0 else "")
            same_file_groups.setdefault(key, []).append(it)

        out["droppedCount"] = len(dropped)
        out["droppedSample"] = dropped[:10]
        out["anchorMissingCount"] = len(no_anchor)
        out["anchorMissingSample"] = no_anchor[:10]
        out["anchorFoundByIdCount"] = html5_id
        out["anchorFoundByNameCount"] = html4_name

        # 契约 §1.4.1-1：chapterId 撞键（同 spine+同 anchor）→ 需要 ~n<k> 消歧的条目数
        collisions = {f"{k[0]}#{k[1]}": len(v) for k, v in same_file_groups.items() if len(v) > 1}
        out["keyCollisionGroups"] = len(collisions)
        out["keyCollisionEntriesNeedingSuffix"] = sum(len(v) - 1 for v in same_file_groups.values() if len(v) > 1)
        out["keyCollisionSample"] = dict(list(collisions.items())[:10])
        # 撞键的**逐条明细**：谁先谁后、落在第几个 spine、预期 chapterId 长什么样
        # （契约要把真实书那个例子写死，就必须能报出精确的 spine 序号）
        detail = []
        for (f, anc), lst in same_file_groups.items():
            if len(lst) < 2:
                continue
            idx = spine.index(f) if f in spine else None
            for k, it in enumerate(lst):
                detail.append({"file": f, "spineIndex": idx, "anchor": anc, "tocOrderInGroup": k + 1,
                               "title": it["title"],
                               "expectedChapterId": (f"epub:{idx}" + (f"~{anc}" if anc else ""))
                                                     + ("" if k == 0 else f"~n{k + 1}")})
        out["keyCollisionDetail"] = detail[:20]

        # N7「卷首合成章」相关：目录覆盖不到的正文文件（差值越大，越需要 front 章兜住）
        toc_files = {it.get("file") for it in items if not it.get("dropped")}
        out["contentFilesNotInTocCount"] = len(spine_set - toc_files)
        out["contentFilesNotInToc"] = sorted(spine_set - toc_files)[:20]

        # 契约 §1.4.1-3：同文件内按 (锚点偏移, 目录顺序) 排序后，与下一项起点相同的 = 零长度章
        by_file = {}
        for spine_idx, off, order, it in located:
            by_file.setdefault(it["file"], []).append((off if off >= 0 else -1, order, it))
        zero = []
        overlap = 0
        for f, lst in by_file.items():
            lst.sort(key=lambda t: (t[0], t[1]))
            for i in range(len(lst) - 1):
                if lst[i][0] == lst[i + 1][0]:
                    zero.append({"title": lst[i][2]["title"], "file": f, "offset": lst[i][0]})
        out["predictedZeroLength"] = len(zero)
        out["predictedZeroLengthSample"] = zero[:10]
        # 契约 §1.4.1-3 的立论依据：同文件内「目录顺序」是否真的与「文档顺序」不一致
        out_of_order = []
        for f, lst in by_file.items():
            if len(lst) < 2:
                continue
            by_toc_order = [t[1] for t in lst]
            by_offset = [t[1] for t in sorted(lst, key=lambda t: (t[0], t[1]))]
            if by_toc_order != by_offset:
                out_of_order.append({"file": f, "items": len(lst)})
        out["outOfOrderFiles"] = len(out_of_order)
        out["outOfOrderSample"] = out_of_order[:10]
        out["predictedRowCount"] = len(located)
        out["expectedRowCheck"] = {
            "rowsIfPerNavPoint": out["predictedRowCount"],
            "rowsIfPerSpine": len(spine),
            "discriminates": out["predictedRowCount"] != len(spine),
        }
        out["filesWithMultipleLocatedItems"] = sum(1 for v in by_file.values() if len(v) > 1)
    out["ok"] = True
    return out


# ───────────────────────── 候选挑选 ─────────────────────────

def pick(d):
    """从目录里挑出验证维度 2/3 需要的三类夹具：有书签 / 无书签有文本 / 扫描件。"""
    res = {"epub": [], "pdfWithBookmarks": None, "pdfNoBookmarks": None,
           "pdfScanned": None, "pdfSmallText": None}
    for n in sorted(os.listdir(d)):
        p = os.path.join(d, n)
        if not os.path.isfile(p):
            continue
        low = n.lower()
        if low.endswith(".epub"):
            try:
                e = probe_epub(p)
            except Exception as ex:  # noqa: BLE001
                e = {"path": p, "ok": False, "error": f"{type(ex).__name__}: {ex}"}
            res["epub"].append({k: e.get(k) for k in
                                ("path", "ok", "error", "opfVersion", "isEpub3", "ncxTocCount",
                                 "navTocCount", "spineCount")})
        elif low.endswith(".pdf"):
            try:
                q = probe_pdf(p)
            except Exception as ex:  # noqa: BLE001
                continue
            q2 = {k: q.get(k) for k in
                  ("path", "pageCount", "tocCount", "looksScanned", "totalTextChars", "levelJumps")}
            if q["hasBookmarks"] and res["pdfWithBookmarks"] is None:
                res["pdfWithBookmarks"] = q2
            if not q["hasBookmarks"] and not q["looksScanned"] and q["totalTextChars"] > 2000:
                if res["pdfNoBookmarks"] is None:
                    res["pdfNoBookmarks"] = q2
                if res["pdfSmallText"] is None and os.path.getsize(p) < 2_000_000:
                    res["pdfSmallText"] = q2
            if q["looksScanned"] and res["pdfScanned"] is None:
                res["pdfScanned"] = q2
    return res


def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 3
    cmd = sys.argv[1]
    try:
        if cmd == "epub":
            print(json.dumps(probe_epub(sys.argv[2]), ensure_ascii=False, indent=2))
        elif cmd == "pdf":
            print(json.dumps(probe_pdf(sys.argv[2]), ensure_ascii=False, indent=2))
        elif cmd == "toc-map":
            print(json.dumps(toc_map(sys.argv[2]), ensure_ascii=False, indent=2))
        elif cmd == "pick":
            d = sys.argv[2] if len(sys.argv) > 2 else r"C:\Users\hahahotsoup\Downloads"
            print(json.dumps(pick(d), ensure_ascii=False, indent=2))
        else:
            print(__doc__)
            return 3
    except Exception as e:  # noqa: BLE001
        print(json.dumps({"ok": False, "error": f"{type(e).__name__}: {e}"}, ensure_ascii=False))
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
