"""se7en Source — icône dans la DA « Centrale » de se7en Voice.

Châssis noir arrondi avec biseau, « 7 » Michroma couleur bone avec halo blanc, coin HUD coupé et, en bas,
deux prises (la seconde allumée en cyan) reliées par une flèche : on passe d'une entrée à l'autre.

  python tools/make_icon.py <dossier de sortie>
    -> src/Se7enSource/Assets/app.ico (16 à 256 px) et <sortie>/se7en-source.png (256 px, pour Seventh OS)
"""
import sys
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter, ImageFont

ROOT = Path(__file__).resolve().parent.parent
FONT = ROOT / 'src' / 'Se7enSource' / 'Assets' / 'Fonts' / 'Michroma-400.ttf'

BONE = (230, 231, 235)
ORANGE = (255, 106, 61)
RED = (255, 61, 51)
CYAN = (31, 214, 221)


def icon(size=1024, small=False):
    S = size * 2  # suréchantillonnage, réduit à la fin
    im = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    pad, rad = S * 0.04, S * 0.2

    # Châssis : dégradé ink-200 -> noir, liseré clair en haut, bord noir
    body = Image.new('RGBA', (S, S))
    g = ImageDraw.Draw(body)
    for y in range(S):
        t = y / S
        c = tuple(int(a + (b - a) * t) for a, b in zip((34, 34, 40), (6, 6, 8)))
        g.line([(0, y), (S, y)], fill=c + (255,))
    mask = Image.new('L', (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle((pad, pad, S - pad, S - pad), rad, fill=255)
    im.paste(body, (0, 0), mask)
    d = ImageDraw.Draw(im)
    d.rounded_rectangle((pad, pad, S - pad, S - pad), rad, outline=(0, 0, 0, 255), width=max(2, S // 256))
    d.rounded_rectangle((pad + S * 0.012, pad + S * 0.012, S - pad - S * 0.012, S - pad - S * 0.012), rad - S * 0.012,
                        outline=(255, 255, 255, 34), width=max(2, S // 300))

    # Coin HUD (haut gauche) : filet blanc fin à 45°
    if not small:
        w = max(2, S // 220)
        x0, y0, L = S * 0.17, S * 0.17, S * 0.09
        d.line([(x0, y0 + L), (x0, y0 + L * 0.35), (x0 + L * 0.35, y0), (x0 + L, y0)], fill=(255, 255, 255, 150), width=w, joint='curve')

    # « 7 » Michroma, épaissi, centré
    font = ImageFont.truetype(str(FONT), int(S * (0.78 if small else 0.66)))
    sw = int(S * (0.03 if small else 0.018))
    glyph = Image.new('L', (S, S), 0)
    gd = ImageDraw.Draw(glyph)
    l, t, r, b = gd.textbbox((0, 0), '7', font=font, stroke_width=sw)
    cy = S * (0.5 if small else 0.45)
    pos = ((S - (r - l)) / 2 - l, cy - (b - t) / 2 - t)
    gd.text(pos, '7', font=font, fill=255, stroke_width=sw, stroke_fill=255)
    glow = glyph.filter(ImageFilter.GaussianBlur(S * 0.03)).point(lambda v: int(v * 0.45))
    im = Image.alpha_composite(im, Image.merge('RGBA', [Image.new('L', (S, S), 255)] * 3 + [glow]))
    im = Image.alpha_composite(im, Image.merge('RGBA', [Image.new('L', (S, S), c) for c in BONE] + [glyph]))

    # Deux prises (rectangles arrondis) et une flèche de l'une à l'autre ; la prise d'arrivée est allumée
    if not small:
        d = ImageDraw.Draw(im)
        y, w, h, gap = S * 0.80, S * 0.13, S * 0.06, S * 0.13
        boxes = [(S / 2 - gap - w, y - h / 2, S / 2 - gap, y + h / 2), (S / 2 + gap, y - h / 2, S / 2 + gap + w, y + h / 2)]
        halo = Image.new('RGBA', (S, S), (0, 0, 0, 0))
        hb = boxes[1]
        ImageDraw.Draw(halo).rounded_rectangle((hb[0] - h * 0.5, hb[1] - h * 0.5, hb[2] + h * 0.5, hb[3] + h * 0.5), h, fill=CYAN + (120,))
        im = Image.alpha_composite(im, halo.filter(ImageFilter.GaussianBlur(h * 0.45)))
        d = ImageDraw.Draw(im)
        lw = max(2, int(S * 0.011))
        d.rounded_rectangle(boxes[0], h * 0.3, outline=BONE + (255,), width=lw)
        d.rounded_rectangle(boxes[1], h * 0.3, fill=CYAN + (255,))
        x0, x1 = S / 2 - gap * 0.55, S / 2 + gap * 0.55
        d.line([(x0, y), (x1, y)], fill=BONE + (255,), width=lw)
        a = h * 0.42
        d.polygon([(x1 + a * 0.2, y), (x1 - a * 0.8, y - a * 0.7), (x1 - a * 0.8, y + a * 0.7)], fill=BONE + (255,))

    # Découpe finale aux bords du châssis
    out = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    out.paste(im, (0, 0), mask)
    return out.resize((size, size), Image.LANCZOS)


def main():
    out_dir = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / 'dist'
    out_dir.mkdir(parents=True, exist_ok=True)
    big = icon(1024)
    big.resize((256, 256), Image.LANCZOS).save(out_dir / 'se7en-source.png')
    big.save(out_dir / 'se7en-source-1024.png')
    # Petites tailles : version simplifiée (7 plus gros, sans prises ni coin) pour rester lisible
    frames = {s: (big if s >= 48 else icon(256, small=True)).resize((s, s), Image.LANCZOS) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)}
    frames[256].save(ROOT / 'src' / 'Se7enSource' / 'Assets' / 'app.ico', sizes=[(s, s) for s in frames],
                     append_images=[frames[s] for s in frames if s != 256])
    print('ok', out_dir)


if __name__ == '__main__':
    main()
