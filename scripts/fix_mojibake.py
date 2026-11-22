import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
EXTS = {".cs", ".xaml", ".csproj", ".sln", ".py", ".dart", ".kt", ".xml", ".kts",
        ".ps1", ".bat", ".md", ".txt", ".json", ".yaml", ".yml"}
SKIP = {"build", "obj", "bin", ".gradle", "__pycache__", "ephemeral", ".git", "out"}

def build_map():
    mapping = {}
    for cp in range(0x80, 0x110000):
        ch = chr(cp)
        try:
            encoded = ch.encode("utf-8")
        except UnicodeEncodeError:
            continue
        try:
            garbled = encoded.decode("cp1252")
        except UnicodeDecodeError:
            continue
        if garbled != ch and len(garbled) > 1:
            mapping[garbled] = ch
    return mapping

def main():
    mapping = build_map()
    keys = sorted(mapping, key=len, reverse=True)
    changed = 0
    for path in ROOT.rglob("*"):
        if not path.is_file() or path.suffix.lower() not in EXTS:
            continue
        if any(part in SKIP for part in path.parts):
            continue
        try:
            text = path.read_text(encoding="utf-8")
        except (UnicodeDecodeError, OSError):
            continue
        original = text
        for key in keys:
            if key in text:
                text = text.replace(key, mapping[key])
        if text != original:
            path.write_text(text, encoding="utf-8", newline="")
            changed += 1
            print(f"fixed {path.relative_to(ROOT)}")
    print(f"{changed} files repaired")

if __name__ == "__main__":
    sys.exit(main())
