"""Render the shared geometric icon at each size; no AI bitmap or upscaling."""
import hashlib, json, math
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
assets = root / 'src/LinkBridge.App/Assets'
design_path = assets / 'linkbridge-icon-design.json'
design = json.loads(design_path.read_text())

def color(value):
    raw = value.removeprefix('#')
    return tuple(int(raw[i:i+2], 16) for i in range(0, len(raw), 2)) + (() if len(raw) == 8 else (255,))

def render(size):
    factor = 4 if size <= 256 else 2
    n = size * factor
    unit = n / 256
    image = Image.new('RGBA', (n, n))
    for layer in design['layers']:
        a, b = color(layer['from']), color(layer['to'])
        gradient = Image.new('RGBA', (1, n))
        gradient.putdata([tuple(round(a[k] + (b[k]-a[k])*y/(n-1)) for k in range(4)) for y in range(n)])
        mask = Image.new('L', (n, n))
        radius = layer['radius'] * unit
        bounds = (n/2-radius, n/2-radius, n/2+radius, n/2+radius)
        ImageDraw.Draw(mask).ellipse(bounds, fill=255)
        surface = gradient.resize((n, n))
        surface.putalpha(Image.composite(surface.getchannel('A'), Image.new('L', (n,n)), mask))
        image.alpha_composite(surface)
        ImageDraw.Draw(image).ellipse(bounds, outline=color(layer['stroke']), width=max(1, round(layer['strokeWidth']*unit)))
    draw = ImageDraw.Draw(image)
    for screen in design['screens']:
        x,y,w,h = (screen[k]*unit for k in ('x','y','w','h'))
        width = max(1, round(design['screenStrokeWidth']*unit))
        draw.rounded_rectangle((x,y,x+w,y+h), radius=6*unit, outline='white', width=width)
        cx, bottom = x+w/2, y+h
        draw.line((cx,bottom,cx,bottom+15*unit), fill='white', width=width)
        draw.line((cx-21*unit,bottom+17*unit,cx+21*unit,bottom+17*unit), fill='white', width=width)
    points, start = [], design['start']
    for curve in design['curves']:
        x, y = start
        for j in range(81):
            t, q = j/80, 1-j/80
            points.append(((q*q*q*x+3*q*q*t*curve[0]+3*q*t*t*curve[2]+t*t*t*curve[4])*unit,
                           (q*q*q*y+3*q*q*t*curve[1]+3*q*t*t*curve[3]+t*t*t*curve[5])*unit))
        start = curve[4:]
    draw = ImageDraw.Draw(image)
    def stroke(offset, ink, width):
        path = [(x, y+offset) for x,y in points]
        draw.line(path, fill=ink, width=round(width))
        # Explicit round joins avoid Pillow's tiny-segment join seams.
        radius = width/2
        for x,y in path:
            draw.ellipse((x-radius,y-radius,x+radius,y+radius), fill=ink)
    stroke(unit*2, '#103491', (design['glyphWidth']+2)*unit)
    stroke(0, color(design['glyph']), design['glyphWidth']*unit)
    reflection, radius, begin, end, width = design['reflection']
    radius *= unit
    draw.arc((n/2-radius,n/2-radius,n/2+radius,n/2+radius), begin, end, fill=color(reflection), width=max(1, round(width*unit)))
    return image.resize((size,size), Image.Resampling.LANCZOS)

sizes = [16, 24, 32, 48, 64, 128, 256]
frames = [render(s) for s in sizes]
frames[-1].save(assets/'linkbridge.ico', format='ICO', sizes=[(s,s) for s in sizes], append_images=frames[:-1])
render(1024).save(assets/'linkbridge-icon.png')
manifest = {'IconSha256': hashlib.sha256((assets/'linkbridge.ico').read_bytes()).hexdigest(),
            'DesignSha256': hashlib.sha256(design_path.read_bytes()).hexdigest()}
(assets/'linkbridge-icon-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
print(json.dumps({'icoBytes': (assets/'linkbridge.ico').stat().st_size, **manifest}))
