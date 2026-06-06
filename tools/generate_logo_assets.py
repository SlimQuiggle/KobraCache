from pathlib import Path
from PIL import Image, ImageDraw


ROOT = Path(__file__).resolve().parents[1]
ASSET_DIR = ROOT / "src" / "KobraCache.Desktop" / "Assets"
PNG_PATH = ASSET_DIR / "KobraCacheLogo.png"
ICO_PATH = ASSET_DIR / "KobraCache.ico"


def scale_points(points, scale):
    return [(round(x * scale), round(y * scale)) for x, y in points]


def draw_polyline(draw, points, scale, width, fill):
    draw.line(scale_points(points, scale), fill=fill, width=round(width * scale), joint="curve")


def draw_arc(draw, bbox, scale, start, end, width, fill):
    box = tuple(round(value * scale) for value in bbox)
    draw.arc(box, start=start, end=end, fill=fill, width=round(width * scale))


def draw_logo(size):
    scale = size / 256
    image = Image.new("RGBA", (size, size), (255, 255, 255, 0))
    draw = ImageDraw.Draw(image)
    stroke = (17, 24, 39, 255)
    accent = (14, 116, 144, 255)
    width = 10

    # 3D printer frame and bed.
    draw.rounded_rectangle(tuple(round(v * scale) for v in (46, 172, 210, 216)), radius=round(3 * scale), outline=stroke, width=round(width * scale))
    draw_polyline(draw, [(66, 172), (66, 82), (190, 82), (190, 172)], scale, width, stroke)
    draw.rounded_rectangle(tuple(round(v * scale) for v in (82, 98, 174, 154)), radius=round(2 * scale), outline=stroke, width=round(width * scale))
    draw_polyline(draw, [(96, 132), (160, 132)], scale, width, stroke)
    draw_polyline(draw, [(100, 216), (156, 216)], scale, width, stroke)
    draw_polyline(draw, [(88, 66), (168, 66)], scale, width, stroke)
    draw_polyline(draw, [(128, 66), (128, 91)], scale, width, stroke)
    draw_polyline(draw, [(114, 92), (142, 92)], scale, width, stroke)

    # Cobra rising out of the print area.
    draw_polyline(draw, [(128, 126), (128, 84)], scale, width, stroke)
    draw_polyline(
        draw,
        [(128, 84), (116, 78), (106, 66), (101, 50), (108, 39), (123, 36), (143, 43), (160, 37), (177, 40), (186, 50), (180, 66), (166, 78), (152, 84)],
        scale,
        width,
        stroke,
    )
    draw_polyline(draw, [(152, 84), (141, 91), (121, 91), (110, 84)], scale, width, stroke)
    draw_polyline(draw, [(101, 50), (116, 56), (128, 70)], scale, width, stroke)
    draw_polyline(draw, [(186, 50), (171, 57), (157, 71)], scale, width, stroke)
    draw_polyline(draw, [(110, 84), (124, 91), (141, 91), (155, 84)], scale, width - 2, accent)

    draw_arc(draw, (92, 119, 174, 179), scale, 115, 354, width, stroke)
    draw_arc(draw, (74, 120, 151, 178), scale, 18, 161, width, stroke)
    draw_polyline(draw, [(114, 170), (136, 178), (158, 170), (175, 156)], scale, width - 2, accent)
    draw_polyline(draw, [(112, 174), (128, 183), (144, 183), (155, 174)], scale, width, stroke)

    eye_radius = max(2, round(4 * scale))
    draw.ellipse((round(123 * scale) - eye_radius, round(58 * scale) - eye_radius, round(123 * scale) + eye_radius, round(58 * scale) + eye_radius), fill=stroke)
    draw.ellipse((round(154 * scale) - eye_radius, round(58 * scale) - eye_radius, round(154 * scale) + eye_radius, round(58 * scale) + eye_radius), fill=stroke)
    return image


def main():
    ASSET_DIR.mkdir(parents=True, exist_ok=True)
    base = draw_logo(1024).resize((256, 256), Image.Resampling.LANCZOS)
    base.save(PNG_PATH)
    base.save(ICO_PATH, sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    print(PNG_PATH)
    print(ICO_PATH)


if __name__ == "__main__":
    main()
