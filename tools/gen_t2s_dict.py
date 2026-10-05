# -*- coding: utf-8 -*-
"""
重新生成 NameTool 的「繁=>简」词典 Resources\\t2s-dict.txt。

用法（默认输出到 E:\\Visual\\NameTool\\Resources\\t2s-dict.txt）：

    python tools\\gen_t2s_dict.py
    python tools\\gen_t2s_dict.py  <输出路径>

词典来源（都会自动下载到系统临时目录，源文件也已可手工准备在 --src 指定目录）：

  主表  HanLP      https://raw.githubusercontent.com/hankcs/HanLP/1.x/data/dictionary/tc/t2s.txt
        —— 用户指定的 quick-chinese-transfer 项目，其词典即取自 HanLP 的 tc 目录，故此处用同一份。
  补表  OpenCC     https://raw.githubusercontent.com/BYVoid/OpenCC/master/data/dictionary/TSCharacters.txt
                   https://raw.githubusercontent.com/BYVoid/OpenCC/master/data/dictionary/TSPhrases.txt
        —— 只补主表里没有的键，保证主表语义（即所引库的行为）不被覆盖。

两个必须遵守的约束（改脚本时别破坏）：
  1. **「不变条目」要保留**，如 `乾隆=乾隆`。最长匹配靠它们挡住单字规则 `乾=干`，
     否则「乾隆」会被转成「干隆」。
  2. 输出是每行 `繁体=简体` 的纯文本，交给 Services\\ChineseConverter.cs 按
     「首字索引 + 最长匹配优先」加载；键里不能出现 `=` 或制表符。
"""
import io
import os
import sys
import collections
import urllib.request

SOURCES = {
    "hanlp_t2s.txt": "https://raw.githubusercontent.com/hankcs/HanLP/1.x/data/dictionary/tc/t2s.txt",
    "opencc_TSCharacters.txt": "https://raw.githubusercontent.com/BYVoid/OpenCC/master/data/dictionary/TSCharacters.txt",
    "opencc_TSPhrases.txt": "https://raw.githubusercontent.com/BYVoid/OpenCC/master/data/dictionary/TSPhrases.txt",
}

DEFAULT_OUT = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "Resources", "t2s-dict.txt")


def fetch(name, url, cache_dir):
    """取源词典：优先用缓存，没有再下载。"""
    path = os.path.join(cache_dir, name)
    if os.path.exists(path) and os.path.getsize(path) > 0:
        return path
    print("下载 %s" % url)
    os.makedirs(cache_dir, exist_ok=True)
    with urllib.request.urlopen(url, timeout=60) as resp:
        data = resp.read()
    with open(path, "wb") as f:
        f.write(data)
    return path


def read_pairs(path, sep):
    """读「键<sep>值」格式；跳过注释与空行；多值时取第一个。"""
    pairs = []
    with io.open(path, encoding="utf-8") as f:
        for raw in f:
            line = raw.rstrip("\r\n")
            if not line.strip() or line.lstrip().startswith("#"):
                continue
            if sep not in line:
                continue
            key, _, val = line.partition(sep)
            key = key.strip()
            val = (val.split() or [""])[0].strip()
            if key and val:
                pairs.append((key, val))
    return pairs


def build(src_dir):
    main = read_pairs(fetch("hanlp_t2s.txt", SOURCES["hanlp_t2s.txt"], src_dir), "=")
    chars = read_pairs(fetch("opencc_TSCharacters.txt", SOURCES["opencc_TSCharacters.txt"], src_dir), "\t")
    phrases = read_pairs(fetch("opencc_TSPhrases.txt", SOURCES["opencc_TSPhrases.txt"], src_dir), "\t")

    table = {}
    for k, v in main:                 # 主表先入，重复键以先到的为准
        table.setdefault(k, v)
    added_chars = added_phrases = 0
    for k, v in chars:
        if k not in table:
            table[k] = v
            added_chars += 1
    for k, v in phrases:
        if k not in table:
            table[k] = v
            added_phrases += 1

    bad = [k for k in table if "=" in k or "\t" in k]
    if bad:
        raise SystemExit("键里含非法字符: %r" % bad[:5])

    print("主表 %d 条；补字 %d；补词 %d" % (len(main), added_chars, added_phrases))
    return sorted(table.items(), key=lambda kv: (-len(kv[0]), kv[0]))


def convert(text, by_first):
    out = []
    i = 0
    while i < len(text):
        hit = None
        for k, v in by_first.get(text[i], ()):
            if text.startswith(k, i):
                hit = (k, v)
                break
        if hit:
            out.append(hit[1])
            i += len(hit[0])
        else:
            out.append(text[i])
            i += 1
    return "".join(out)


def self_check(items):
    """用所引库 readme 里的 t2s 样例自检：不一致就别写文件。"""
    by_first = collections.defaultdict(list)
    for k, v in items:
        by_first[k[0]].append((k, v))
    for lst in by_first.values():
        lst.sort(key=lambda kv: -len(kv[0]))

    sample_in = ("這斜月三星洞…… 長壽麪，孫悟空，豬八戒，唐僧，沙和尚，白龍馬，李靖，托塔天王, "
                 "戲說西遊，許多人都這樣說，收拾一下，拾金不昧；纔=才")
    sample_out = ("这斜月三星洞…… 长寿面，孙悟空，猪八戒，唐僧，沙和尚，白龙马，李靖，托塔天王, "
                  "戏说西游，许多人都这样说，收拾一下，拾金不昧；才=才")
    got = convert(sample_in, by_first)
    if got != sample_out:
        print("自检失败！\n  got =%s\n  want=%s" % (got, sample_out))
        return False

    extra = [("乾隆年間的乾", "乾隆年间的干"), ("简体中文不变", "简体中文不变")]
    for src, want in extra:
        got = convert(src, by_first)
        if got != want:
            print("自检失败：%s -> %s（期望 %s）" % (src, got, want))
            return False

    print("自检通过（readme 样例 + 词语保护 + 幂等）")
    return True


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    out_path = args[0] if args else DEFAULT_OUT
    src_dir = os.path.join(os.environ.get("TEMP", "/tmp"), "nametool-t2s-src")

    items = build(src_dir)
    if not self_check(items):
        raise SystemExit(1)

    with io.open(out_path, "w", encoding="utf-8", newline="\n") as f:
        for k, v in items:
            f.write("%s=%s\n" % (k, v))

    single = sum(1 for k, _ in items if len(k) == 1)
    print("写出 %s" % out_path)
    print("  共 %d 条（单字 %d / 词语 %d，最长键 %d），%d 字节"
          % (len(items), single, len(items) - single, max(len(k) for k, _ in items),
             os.path.getsize(out_path)))


if __name__ == "__main__":
    main()
