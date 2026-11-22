"""Generate every app icon from Logo.jpg. Run: python scripts/branding.py"""

from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "Logo.jpg"
SIZES = [16, 24, 32, 48, 64, 128, 256]


def main() -> None:
    source = Image.open(SOURCE).convert("RGB")

    mask = source.convert("L").point(lambda value: 255 if value > 18 else 0)
    box = mask.getbbox()
    if not box:
        raise SystemExit("logo appears empty")

    left, top, right, bottom = box
    width, height = right - left, bottom - top
    center_x, center_y = left + width // 2, top + height // 2
    side = int(max(width, height) * 1.16)

    left = max(0, center_x - side // 2)
    top = max(0, center_y - side // 2)
    side = min(side, min(source.width - left, source.height - top))

    square = Image.new("RGB", (1024, 1024), (0, 0, 0))
    square.paste(source.crop((left, top, left + side, top + side)).resize((1024, 1024), Image.LANCZOS), (0, 0))

    (ROOT / "branding").mkdir(parents=True, exist_ok=True)
    (ROOT / "agent" / "Sentinel" / "Assets").mkdir(parents=True, exist_ok=True)
    (ROOT / "mobile" / "assets").mkdir(parents=True, exist_ok=True)

    square.save(ROOT / "branding" / "logo-master.png")
    square.resize((512, 512), Image.LANCZOS).save(ROOT / "agent" / "Sentinel" / "Assets" / "logo.png")
    square.resize((512, 512), Image.LANCZOS).save(ROOT / "mobile" / "assets" / "logo.png")

    ico = [(size, size) for size in SIZES]
    square.save(ROOT / "agent" / "Sentinel" / "app.ico", format="ICO", sizes=ico)
    square.save(ROOT / "mobile" / "windows" / "runner" / "resources" / "app_icon.ico", format="ICO", sizes=ico)

    densities = {
        "mipmap-mdpi": 48,
        "mipmap-hdpi": 72,
        "mipmap-xhdpi": 96,
        "mipmap-xxhdpi": 144,
        "mipmap-xxxhdpi": 192,
    }
    for folder, size in densities.items():
        target = ROOT / "mobile" / "android" / "app" / "src" / "main" / "res" / folder
        target.mkdir(parents=True, exist_ok=True)
        square.resize((size, size), Image.LANCZOS).save(target / "ic_launcher.png")

    print(f"source bbox={box} crop={side}px")


if __name__ == "__main__":
    main()
