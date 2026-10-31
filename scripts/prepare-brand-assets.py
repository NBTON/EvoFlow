"""Rebuild Windows image variants from the checked-in, unmodified brand artwork."""

from pathlib import Path

from PIL import Image

ASSETS = Path(__file__).resolve().parents[1] / "native" / "EvoFlow" / "Assets"


def fit(source, width, height):
    picture = source.copy()
    picture.thumbnail((width, height), Image.Resampling.LANCZOS)
    canvas = Image.new("RGBA", (width, height))
    canvas.alpha_composite(picture, ((width - picture.width) // 2, (height - picture.height) // 2))
    return canvas


def main():
    with Image.open(ASSETS / "AppIcon.png") as image:
        artwork = image.convert("RGBA")
    with Image.open(ASSETS / "TrayGlyph.png") as image:
        glyph = image.convert("RGBA")
    with Image.open(ASSETS / "AppIcon.ico") as icon:
        print("Supplied executable ICO sizes:", sorted(icon.ico.sizes()))
    for name, width, height in (
        ("Square44x44Logo", 44, 44),
        ("Square150x150Logo", 150, 150),
        ("Wide310x150Logo", 310, 150),
        ("SplashScreen", 620, 300),
        ("LockScreenLogo", 24, 24),
        ("StoreLogo", 50, 50),
    ):
        for scale in (100, 125, 150, 200, 400):
            fit(artwork, round(width * scale / 100), round(height * scale / 100)).save(
                ASSETS / f"{name}.scale-{scale}.png"
            )
    fit(artwork, 50, 50).save(ASSETS / "StoreLogo.png")
    for size in (16, 24, 32, 48, 64, 256):
        for suffix in ("", "_altform-unplated", "_altform-lightunplated"):
            fit(artwork, size, size).save(ASSETS / f"Square44x44Logo.targetsize-{size}{suffix}.png")
    glyph.save(ASSETS / "TrayIcon.ico", sizes=[(n, n) for n in (16, 20, 24, 32, 40, 48, 64, 256)])
    print("Package images and transparent tray ICO regenerated.")


if __name__ == "__main__":
    main()
