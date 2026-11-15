"""Export the GitNest vector design to Windows ICO and cross-platform PNG assets (requires Pillow)."""

from pathlib import Path

from PIL import Image, ImageDraw


root = Path(__file__).resolve().parents[2]
scale = 4
image = Image.new("RGBA", (256 * scale, 256 * scale))
draw = ImageDraw.Draw(image)
accent = "#61D9B5"
draw.rounded_rectangle((0, 0, 256 * scale - 1, 256 * scale - 1), radius=56 * scale, fill="#162B32")


def line(points, width):
    scaled = [(x * scale, y * scale) for x, y in points]
    draw.line(scaled, fill=accent, width=width * scale, joint="curve")
    radius = width * scale / 2
    for x, y in scaled:
        draw.ellipse((x - radius, y - radius, x + radius, y + radius), fill=accent)


line([(56, 150), (128, 194), (200, 150)], 14)
line([(56, 180), (128, 224), (200, 180)], 14)
line([(128, 151), (128, 70)], 16)
line([(128, 118), (184, 86)], 16)
for x, y in [(128, 55), (184, 76), (128, 151)]:
    draw.ellipse(((x - 20) * scale, (y - 20) * scale, (x + 20) * scale, (y + 20) * scale), fill=accent)

icon_1024 = image
icon_256 = image.resize((256, 256), Image.Resampling.LANCZOS)

icon_256.save(root / "src/App.ico", sizes=[(n, n) for n in (16, 24, 32, 48, 64, 128, 256)])
icon_256_path = root / "build/resources/_common/icons/gitnest.png"
icon_256_path.parent.mkdir(parents=True, exist_ok=True)
icon_256.save(icon_256_path)

icon_1024_path = root / "build/resources/app/icon_1024.png"
icon_1024_path.parent.mkdir(parents=True, exist_ok=True)
icon_1024.save(icon_1024_path)
