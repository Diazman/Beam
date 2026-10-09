#!/usr/bin/env python3
"""Lists Beam's translatable strings and checks the translation catalogs.

  python3 tools/l10n.py keys            # every English key as JSON (plurals as [one, other])
  python3 tools/l10n.py missing tr      # keys tr.json lacks, as a JSON object to translate
  python3 tools/l10n.py check           # missing / unused / placeholder problems in all catalogs

The same rules are enforced by tests/Beam.Core.Tests/LocalizationTests.cs.
"""
import json, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CATALOGS = os.path.join(ROOT, "src", "Beam.Core", "Localization")
LANGS = ["tr", "ru", "uz"]
STR = r'"((?:[^"\\]|\\.)*)"'
T_CS = re.compile(r'\bL\.T\(\s*' + STR)
PLURAL_CS = re.compile(r'\bL\.Plural\([^;]*?,\s*' + STR + r'\s*,\s*' + STR)
T_XAML = re.compile(r"\{l:T\s+'((?:[^'\\]|\\.)*)'(?:\s*,\s*Phone='((?:[^'\\]|\\.)*)')?\s*\}")
FOR_DEVICE_CS = re.compile(r'\bL\.ForDevice\(\s*' + STR + r'\s*,\s*' + STR)


def unescape_cs(s):
    return re.sub(r'\\(.)', lambda m: {"n": "\n", "t": "\t"}.get(m.group(1), m.group(1)), s)


def keys():
    plain, plurals = set(), {}
    for folder, _, files in os.walk(os.path.join(ROOT, "src")):
        if os.sep + "obj" in folder or os.sep + "bin" in folder or folder.endswith("Localization"):
            continue
        for name in files:
            path = os.path.join(folder, name)
            if name.endswith(".cs"):
                text = open(path, encoding="utf-8").read()
                plain.update(unescape_cs(m) for m in T_CS.findall(text))
                for computer, phone in FOR_DEVICE_CS.findall(text):
                    plain.update((unescape_cs(computer), unescape_cs(phone)))
                for one, other in PLURAL_CS.findall(text):
                    plurals[unescape_cs(other)] = unescape_cs(one)
            elif name.endswith(".axaml"):
                text = open(path, encoding="utf-8").read()
                for text_key, phone_key in T_XAML.findall(text):
                    plain.update(k.replace("\\'", "'") for k in (text_key, phone_key) if k)
    return plain, plurals


def placeholders(s):
    return sorted(set(re.findall(r"\{(\d+)(?:[:,][^}]*)?\}", s)))


def load(lang):
    path = os.path.join(CATALOGS, lang + ".json")
    return json.load(open(path, encoding="utf-8")) if os.path.exists(path) else {}


def main():
    plain, plurals = keys()
    cmd = sys.argv[1] if len(sys.argv) > 1 else "check"
    if cmd == "keys":
        out = {k: "" for k in sorted(plain)}
        out.update({k: [one, k] for k, one in sorted(plurals.items())})
        print(json.dumps(out, ensure_ascii=False, indent=2))
        return 0
    if cmd == "missing":
        catalog = load(sys.argv[2])
        out = {k: "" for k in sorted(plain) if k not in catalog}
        out.update({k: [one, k] for k, one in sorted(plurals.items()) if k not in catalog})
        print(json.dumps(out, ensure_ascii=False, indent=2))
        return 0
    problems = 0
    for lang in LANGS:
        catalog = load(lang)
        for k in sorted(plain | set(plurals)):
            if k not in catalog:
                print(f"{lang}: missing {k!r}"); problems += 1
                continue
            forms = catalog[k] if isinstance(catalog[k], list) else [catalog[k]]
            for f in forms:
                if not f.strip():
                    print(f"{lang}: empty {k!r}"); problems += 1
                if k in plurals and lang == "ru" and len(forms) != 3:
                    print(f"{lang}: needs 3 Russian forms {k!r}"); problems += 1
                elif not (set(placeholders(f)) <= set(placeholders(k)) and set(placeholders(k)) - set(placeholders(f)) <= {"0"}
                          if k in plurals else placeholders(f) == placeholders(k)):
                    print(f"{lang}: placeholders differ {k!r} -> {f!r}"); problems += 1
        for k in catalog:
            if k not in plain and k not in plurals:
                print(f"{lang}: unused {k!r}"); problems += 1
    print(f"{len(plain)} strings, {len(plurals)} plurals, {problems} problems")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
