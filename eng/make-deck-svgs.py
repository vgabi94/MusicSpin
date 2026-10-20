"""Writes the turntable designs' SVG layers (src/MusicSpin.App/Assets/Decks) from the deck studies' CSS and SVG
(docs/design/mockup/decks, #25-#30). Edit the drawings here, then run it again:

    python eng/make-deck-svgs.py src/MusicSpin.App/Assets/Decks

CSS gradients become SVG gradients in user space with the same gradient line: for an angle a on a W x H box the line
runs through the centre along (sin a, -cos a) and is |W sin a| + |H cos a| long. CSS px are 2 deck units (the studies
show the 1320-unit deck about 690 px wide).

Svg.Controls.Avalonia draws objectBoundingBox gradients flat and doesn't draw <pattern>, so every gradient a shape uses
is written in user space over that shape's own box (TEMPLATES, boxed()), and the carbon-fibre weave is XAML.
"""
import math, os, re, sys

OUT = sys.argv[1]
PX = 2.0


def hexa(c):
    """'#rrggbbaa' or '#rrggbb' -> (rgb, opacity)."""
    c = c.lstrip('#')
    if len(c) == 8:
        return '#' + c[:6], int(c[6:], 16) / 255
    return '#' + c, 1.0


def stop(offset, colour):
    rgb, a = hexa(colour) if colour != 'transparent' else ('#000000', 0.0)
    return f'<stop offset="{offset:.4f}" stop-color="{rgb}" stop-opacity="{a:.3f}"/>'


def line(angle, w, h, x0=0, y0=0):
    a = math.radians(angle)
    d = (math.sin(a), -math.cos(a))
    length = abs(w * d[0]) + abs(h * d[1])
    cx, cy = x0 + w / 2, y0 + h / 2
    start = (cx - d[0] * length / 2, cy - d[1] * length / 2)
    return start, d, length


def linear(id_, angle, stops, w, h, x0=0, y0=0):
    """stops: [(colour, fraction)]"""
    start, d, length = line(angle, w, h, x0, y0)
    end = (start[0] + d[0] * length, start[1] + d[1] * length)
    s = ''.join(stop(f, c) for c, f in stops)
    return (f'<linearGradient id="{id_}" gradientUnits="userSpaceOnUse" x1="{start[0]:.2f}" y1="{start[1]:.2f}" '
            f'x2="{end[0]:.2f}" y2="{end[1]:.2f}">{s}</linearGradient>')


def repeating(id_, angle, stops, w, h, x0=0, y0=0):
    """stops: [(colour, position in deck units)]; the period is the last position."""
    start, d, _ = line(angle, w, h, x0, y0)
    period = stops[-1][1]
    end = (start[0] + d[0] * period, start[1] + d[1] * period)
    s = ''.join(stop(p / period, c) for c, p in stops)
    return (f'<linearGradient id="{id_}" gradientUnits="userSpaceOnUse" spreadMethod="repeat" x1="{start[0]:.2f}" '
            f'y1="{start[1]:.2f}" x2="{end[0]:.2f}" y2="{end[1]:.2f}">{s}</linearGradient>')


def ellipse(id_, cx, cy, rx, ry, stops):
    """CSS radial-gradient(rx ry at cx cy, ...): stops [(colour, fraction of the radius)]."""
    s = ''.join(stop(f, c) for c, f in stops)
    return (f'<radialGradient id="{id_}" gradientUnits="userSpaceOnUse" cx="0" cy="0" r="1" '
            f'gradientTransform="translate({cx:.2f} {cy:.2f}) scale({rx:.2f} {ry:.2f})">{s}</radialGradient>')


BOXED = []  # gradients made for one shape each (objectBoundingBox gradients draw flat in Svg.Controls.Avalonia)

TEMPLATES = {
    # id: (kind, geometry relative to the shape's box, stops)
    'brassR': ('r', (.38, .32, .8), '<stop offset="0" stop-color="#fff1c4"/><stop offset=".35" stop-color="#d9b56b"/><stop offset=".8" stop-color="#8a6527"/><stop offset="1" stop-color="#5c4116"/>'),
    'brassPlate': ('l', (0, 0, 0, 1), '<stop offset="0" stop-color="#f0d699"/><stop offset=".5" stop-color="#c9a35a"/><stop offset="1" stop-color="#9c7834"/>'),
    'brassCyl': ('l', (0, 0, 1, 0), '<stop offset="0" stop-color="#6e5020"/><stop offset=".42" stop-color="#e9cf8f"/><stop offset=".52" stop-color="#fff3cc"/><stop offset=".75" stop-color="#b38b45"/><stop offset="1" stop-color="#5e4318"/>'),
    'brassR2': ('r', (.38, .32, .8), '<stop offset="0" stop-color="#fff1c4"/><stop offset=".4" stop-color="#d9b56b"/><stop offset="1" stop-color="#6e5020"/>'),
    'aluInset': ('l', (0, 0, 0, 1), '<stop offset="0" stop-color="#9da1a7"/><stop offset="1" stop-color="#d7dadd"/>'),
    'blackCyl': ('l', (0, 0, 1, 0), '<stop offset="0" stop-color="#0d0e10"/><stop offset=".45" stop-color="#3a3c42"/><stop offset=".6" stop-color="#22242a"/><stop offset="1" stop-color="#0a0b0c"/>'),
    'silverCyl': ('l', (0, 0, 1, 0), '<stop offset="0" stop-color="#7c8188"/><stop offset=".45" stop-color="#f3f4f6"/><stop offset="1" stop-color="#83878e"/>'),
    'cfA': ('l', (0, 0, 1, 0), '<stop offset="0" stop-color="#1a1b1e"/><stop offset=".5" stop-color="#2a2c30"/><stop offset="1" stop-color="#1a1b1e"/>'),
    'cfB': ('l', (0, 0, 0, 1), '<stop offset="0" stop-color="#121315"/><stop offset=".5" stop-color="#1d1e21"/><stop offset="1" stop-color="#121315"/>'),
    'sheen': ('l', (0, 0, 1, 1), '<stop offset="0" stop-color="#fff" stop-opacity=".09"/><stop offset=".45" stop-color="#fff" stop-opacity="0"/><stop offset="1" stop-color="#000" stop-opacity=".4"/>'),
    'gun': ('l', (0, 0, 1, 1), '<stop offset="0" stop-color="#3a3d44"/><stop offset="1" stop-color="#16171a"/>'),
    'gunCyl': ('l', (0, 0, 1, 0), '<stop offset="0" stop-color="#121316"/><stop offset=".4" stop-color="#4a4e57"/><stop offset=".55" stop-color="#2c2f35"/><stop offset="1" stop-color="#0e0f11"/>'),
    'chromeR': ('r', (.35, .3, .85), '<stop offset="0" stop-color="#ffffff"/><stop offset=".35" stop-color="#c3c7cc"/><stop offset=".7" stop-color="#5b6068"/><stop offset="1" stop-color="#2a2d32"/>'),
    'silverInlay': ('l', (0, 0, 1, 0), '<stop offset="0" stop-color="#8d9299"/><stop offset=".45" stop-color="#f4f5f7"/><stop offset=".55" stop-color="#c7cbd0"/><stop offset="1" stop-color="#8d9299"/>'),
    'chromeCyl': ('l', (0, 0, 1, 0), '<stop offset="0" stop-color="#3e4248"/><stop offset=".28" stop-color="#f6f7f9"/><stop offset=".45" stop-color="#9ba0a7"/><stop offset=".6" stop-color="#ffffff"/><stop offset=".85" stop-color="#565b62"/><stop offset="1" stop-color="#b9bdc2"/>'),
    'rail': ('l', (0, 0, 0, 1), '<stop offset="0" stop-color="#f4f1ea"/><stop offset=".5" stop-color="#b9b2a5"/><stop offset="1" stop-color="#7d7669"/>'),
    'housing': ('l', (0, 0, 0, 1), '<stop offset="0" stop-color="#e3ddd2"/><stop offset="1" stop-color="#a9a194"/>'),
    'carriage': ('l', (0, 0, 0, 1), '<stop offset="0" stop-color="#f8f5ef"/><stop offset="1" stop-color="#a39b8e"/>'),
    'whiteCyl': ('l', (0, 0, 1, 0), '<stop offset="0" stop-color="#b9bcc2"/><stop offset=".45" stop-color="#ffffff"/><stop offset="1" stop-color="#c4c7cc"/>'),
}


def G(template, x, y, w, h):
    """url() of the template's gradient laid over the box (x, y, w, h) in user space."""
    kind, g, stops = TEMPLATES[template]
    id_ = f'{template}{len(BOXED)}'
    if kind == 'l':
        x1, y1, x2, y2 = g
        BOXED.append(f'<linearGradient id="{id_}" gradientUnits="userSpaceOnUse" x1="{x + x1 * w:g}" y1="{y + y1 * h:g}" '
                     f'x2="{x + x2 * w:g}" y2="{y + y2 * h:g}">{stops}</linearGradient>')
    else:
        cx, cy, r = g
        BOXED.append(f'<radialGradient id="{id_}" gradientUnits="userSpaceOnUse" cx="{x + cx * w:g}" cy="{y + cy * h:g}" '
                     f'r="{r * w:g}">{stops}</radialGradient>')
    return f'url(#{id_})'


def C(template, cx, cy, r):
    """G for a circle."""
    return G(template, cx - r, cy - r, 2 * r, 2 * r)


def boxed(text):
    """Gives each rect, circle and polygon filled with a template gradient its own gradient over its own box."""
    def attr(tag, name, default=0.0):
        m = re.search(rf'\s{name}="([^"]+)"', tag)
        return float(m.group(1)) if m else default

    def repl(m):
        tag = m.group(0)
        g = re.search(r'fill="url\(#(\w+)\)"', tag)
        if not g or g.group(1) not in TEMPLATES:
            return tag
        kind = m.group(1)
        if kind == 'rect':
            box = (attr(tag, 'x'), attr(tag, 'y'), attr(tag, 'width'), attr(tag, 'height'))
        elif kind == 'circle':
            r = attr(tag, 'r')
            box = (attr(tag, 'cx') - r, attr(tag, 'cy') - r, 2 * r, 2 * r)
        else:
            pts = [tuple(map(float, p.split(','))) for p in re.search(r'points="([^"]+)"', tag).group(1).split()]
            xs, ys = [p[0] for p in pts], [p[1] for p in pts]
            box = (min(xs), min(ys), max(xs) - min(xs), max(ys) - min(ys))
        return tag.replace(g.group(0), f'fill="{G(g.group(1), *box)}"')

    return re.sub(r'<(rect|circle|polygon)\b[^>]*>', repl, text)


def svg(name, comment, view, defs, body):
    body = boxed(body)
    defs = [boxed(d) for d in defs if not re.match(r'<(linear|radial)Gradient id="(\w+)"', d)
            or re.match(r'<(linear|radial)Gradient id="(\w+)"', d).group(2) not in TEMPLATES] + BOXED
    BOXED.clear()
    text = (f'<!-- {comment} -->\n<svg xmlns="http://www.w3.org/2000/svg" viewBox="{view}">\n'
            f'  <defs>\n    ' + '\n    '.join(defs) + '\n  </defs>\n' + body.strip('\n') + '\n</svg>\n')
    with open(os.path.join(OUT, name), 'w', encoding='utf-8', newline='\n') as f:
        f.write(text)


W, H = 1320, 1080
STUDY = 'docs/design/mockup/decks'


def layers(ids):
    return '\n'.join(f'    <rect width="{W}" height="{H}" fill="url(#{i})"/>' for i in ids)


# ---------------------------------------------------------------- 1 · Walnut & Brass
svg('walnut-deck.svg',
    f'Walnut & Brass ({STUDY}/1-walnut-brass.html): the oiled walnut plinth and its brass inlay line, the brass arm board, '
    'rest, cue lever and the screwed name plate. Deck units. The name, the platter, the record and the arm are drawn on top.',
    f'0 0 {W} {H}',
    [f'<clipPath id="plinth"><rect width="{W}" height="{H}" rx="22"/></clipPath>',
     linear('light', 160, [('#ffffff17', 0), ('transparent', .38), ('#00000052', 1)], W, H),
     ellipse('knotA', W * .28, H * .30, W * .9, H * .18, [('#2a150855', 0), ('#2a150800', .7)]),
     ellipse('knotB', W * .70, H * .74, W * .7, H * .14, [('#2a150844', 0), ('#2a150800', .7)]),
     repeating('grainA', 179, [('transparent', 0), ('transparent', 7), ('#1f0f0526', 8), ('#1f0f0526', 10), ('transparent', 11), ('transparent', 19)], W, H),
     repeating('grainB', 181.5, [('#ffffff09', 0), ('#ffffff09', 3), ('transparent', 4), ('transparent', 13)], W, H),
     linear('wood', 180, [('#5d3920', 0), ('#4b2d19', .35), ('#613c23', .62), ('#4a2c18', 1)], W, H),
     '<radialGradient id="brassR" cx=".38" cy=".32" r=".8"><stop offset="0" stop-color="#fff1c4"/><stop offset=".35" stop-color="#d9b56b"/><stop offset=".8" stop-color="#8a6527"/><stop offset="1" stop-color="#5c4116"/></radialGradient>',
     '<linearGradient id="brassPlate" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#f0d699"/><stop offset=".5" stop-color="#c9a35a"/><stop offset="1" stop-color="#9c7834"/></linearGradient>'],
    f'''
  <g clip-path="url(#plinth)">
{layers(['wood', 'grainB', 'grainA', 'knotB', 'knotA', 'light'])}
    <rect width="{W}" height="2" fill="#ffffff" fill-opacity=".18"/>
  </g>
  <!-- the brass inlay line -->
  <rect x="17.5" y="17.5" width="1285" height="1045" rx="10" fill="none" stroke="#d2ad66" stroke-opacity=".6" stroke-width="3"/>
  <!-- arm board -->
  <circle cx="1180" cy="190" r="96" fill="url(#brassR)"/>
  <circle cx="1180" cy="190" r="82" fill="none" stroke="#6e4f1d" stroke-width="2" opacity=".6"/>
  <!-- arm rest -->
  <circle cx="1152" cy="648" r="17" fill="url(#brassR)"/>
  <path d="M1134,640 q18,22 36,0" fill="none" stroke="#2a1a0c" stroke-width="5" stroke-linecap="round"/>
  <!-- cue lever -->
  <circle cx="1272" cy="340" r="20" fill="url(#brassR)"/>
  <rect x="1264" y="340" width="16" height="90" rx="8" fill="url(#brassPlate)"/>
  <!-- the brass name plate, screwed on -->
  <rect x="36" y="978" width="250" height="70" rx="6" fill="url(#brassPlate)" stroke="#6e4f1d" stroke-width="2"/>
  <circle cx="52" cy="1013" r="6" fill="#8a6527"/><path d="M47,1013 h10" stroke="#4a3310" stroke-width="2"/>
  <circle cx="270" cy="1013" r="6" fill="#8a6527"/><path d="M265,1013 h10" stroke="#4a3310" stroke-width="2"/>
''')

ARM = '-300 -300 600 1200'
ARM_NOTE = 'Arm units: the pivot at (0, 0), the stylus at (0, 740).'

svg('walnut-arm.svg', f'Walnut & Brass\'s needle: a brass S-arm, the offset headshell with its finger lift. {ARM_NOTE}', ARM,
    ['<linearGradient id="brassCyl" x1="0" x2="1"><stop offset="0" stop-color="#6e5020"/><stop offset=".42" stop-color="#e9cf8f"/><stop offset=".52" stop-color="#fff3cc"/><stop offset=".75" stop-color="#b38b45"/><stop offset="1" stop-color="#5e4318"/></linearGradient>',
     '<radialGradient id="brassR2" cx=".38" cy=".32" r=".8"><stop offset="0" stop-color="#fff1c4"/><stop offset=".4" stop-color="#d9b56b"/><stop offset="1" stop-color="#6e5020"/></radialGradient>'],
    '''
  <rect x="-7" y="-100" width="14" height="110" fill="url(#brassCyl)"/>
  <rect x="-40" y="-172" width="80" height="80" rx="12" fill="url(#brassCyl)"/>
  <rect x="-40" y="-122" width="80" height="5" fill="#5e4318" opacity=".6"/>
  <path d="M0,30 C0,220 -50,330 -30,450 C-10,560 60,580 34,657" fill="none" stroke="#5e4318" stroke-width="18" stroke-linecap="round"/>
  <path d="M0,30 C0,220 -50,330 -30,450 C-10,560 60,580 34,657" fill="none" stroke="#c9a35a" stroke-width="13" stroke-linecap="round"/>
  <path d="M-3,30 C-3,220 -53,330 -33,450 C-13,560 57,580 31,657" fill="none" stroke="#fff0c2" stroke-width="3.5" stroke-linecap="round" opacity=".9"/>
  <circle r="58" fill="url(#brassR2)"/>
  <circle r="34" fill="#2a1a0c"/>
  <circle r="20" fill="url(#brassR2)"/>
  <g transform="rotate(22 0 740)">
    <path d="M28,668 C56,660 70,646 78,626" fill="none" stroke="#9c7834" stroke-width="8" stroke-linecap="round"/>
    <rect x="-30" y="645" width="60" height="118" rx="10" fill="url(#brassCyl)"/>
    <rect x="-21" y="680" width="42" height="74" rx="5" fill="#1b1410"/>
    <rect x="-21" y="690" width="42" height="6" fill="#d8b866"/>
  </g>
''')

# ---------------------------------------------------------------- 2 · Brushed Aluminium
svg('aluminium-deck.svg',
    f'Brushed Aluminium ({STUDY}/2-aluminium.html): the brushed slab, the milled arm pocket and the arm rest. Deck units. '
    'The lamp and the name are drawn on top.',
    f'0 0 {W} {H}',
    [f'<clipPath id="plinth"><rect width="{W}" height="{H}" rx="16"/></clipPath>',
     linear('light', 168, [('#ffffff4d', 0), ('transparent', .30), ('#0000001a', .65), ('#00000033', 1)], W, H),
     repeating('brush', 0, [('#ffffff14', 0), ('#ffffff14', 1 * PX), ('transparent', 1 * PX), ('transparent', 2 * PX), ('#0000000f', 2 * PX), ('#0000000f', 3 * PX)], W, H),
     repeating('brush2', 0, [('transparent', 0), ('transparent', 7 * PX), ('#ffffff0d', 7 * PX), ('#ffffff0d', 8 * PX), ('transparent', 8 * PX), ('transparent', 13 * PX)], W, H),
     linear('metal', 90, [('#b3b7bd', 0), ('#d8dbdf', .38), ('#c6c9ce', .58), ('#aaaeb4', 1)], W, H),
     '<linearGradient id="aluInset" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#9da1a7"/><stop offset="1" stop-color="#d7dadd"/></linearGradient>'],
    f'''
  <g clip-path="url(#plinth)">
{layers(['metal', 'brush2', 'brush', 'light'])}
    <rect width="{W}" height="2" fill="#ffffff" fill-opacity=".69"/>
    <rect y="{H - 4}" width="{W}" height="4" fill="#000000" fill-opacity=".15"/>
  </g>
  <!-- arm board: a recessed square, like a milled pocket -->
  <rect x="1095" y="115" width="170" height="170" rx="26" fill="url(#aluInset)" stroke="#8a8e94" stroke-width="2"/>
  <rect x="1095" y="117" width="170" height="170" rx="26" fill="none" stroke="#ffffff" stroke-opacity=".69" stroke-width="1.5" opacity=".6"/>
  <!-- arm rest -->
  <rect x="1184" y="636" width="38" height="26" rx="8" fill="#1d1e21"/>
  <rect x="1190" y="641" width="26" height="6" rx="3" fill="#3a3c41"/>
''')

svg('aluminium-arm.svg', f'Brushed Aluminium\'s needle: a straight matte black tube, the offset headshell and its silver cartridge, the '
    f'cylinder counterweight (its ring takes the album colour, drawn on top). {ARM_NOTE}', ARM,
    ['<linearGradient id="blackCyl" x1="0" x2="1"><stop offset="0" stop-color="#0d0e10"/><stop offset=".45" stop-color="#3a3c42"/><stop offset=".6" stop-color="#22242a"/><stop offset="1" stop-color="#0a0b0c"/></linearGradient>',
     '<linearGradient id="silverCyl" x1="0" x2="1"><stop offset="0" stop-color="#7c8188"/><stop offset=".45" stop-color="#f3f4f6"/><stop offset="1" stop-color="#83878e"/></linearGradient>'],
    '''
  <rect x="-36" y="-170" width="72" height="88" rx="6" fill="url(#blackCyl)"/>
  <rect x="-6" y="-84" width="12" height="90" fill="url(#silverCyl)"/>
  <path d="M0,0 L34,657" stroke="#111214" stroke-width="15" stroke-linecap="round"/>
  <path d="M-3,10 L31,650" stroke="#ffffff" stroke-opacity=".22" stroke-width="3" stroke-linecap="round"/>
  <circle r="46" fill="url(#blackCyl)"/>
  <circle r="16" fill="url(#silverCyl)"/>
  <g transform="rotate(22 0 740)">
    <rect x="-24" y="648" width="48" height="112" rx="4" fill="#16171a"/>
    <rect x="-24" y="648" width="48" height="10" rx="3" fill="#2c2e33"/>
    <rect x="-18" y="684" width="36" height="66" rx="3" fill="url(#silverCyl)"/>
    <path d="M24,664 L74,642" stroke="#16171a" stroke-width="7" stroke-linecap="round"/>
  </g>
''')

# ---------------------------------------------------------------- 3 · Carbon Signal
CHAMFER = '60,0 1260,0 1320,60 1320,1020 1260,1080 60,1080 0,1020 0,60'
svg('carbon-deck.svg',
    f'Carbon Signal ({STUDY}/3-carbon-signal.html): the chamfered slab\'s sheen and edge (its carbon-fibre weave is drawn '
    'under it, in Styles/Decks.axaml), the hex arm board, the rest and the readout window. Deck units. The edge light, the '
    'readout, the name and the platter ticks are drawn on top.',
    f'0 0 {W} {H}',
    ['<linearGradient id="sheen" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#fff" stop-opacity=".09"/><stop offset=".45" stop-color="#fff" stop-opacity="0"/><stop offset="1" stop-color="#000" stop-opacity=".4"/></linearGradient>',
     '<linearGradient id="gun" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#3a3d44"/><stop offset="1" stop-color="#16171a"/></linearGradient>'],
    f'''
  <polygon points="{CHAMFER}" fill="url(#sheen)" stroke="#3a3d44" stroke-width="2"/>
  <!-- hex arm board -->
  <polygon points="1280,200 1230,286.6 1130,286.6 1080,200 1130,113.4 1230,113.4" fill="url(#gun)" stroke="#45484f" stroke-width="2"/>
  <!-- arm rest -->
  <path d="M1183,636 h16 l6,12 h-4 M1223,636 h-16 l-6,12" fill="none" stroke="#5a5e66" stroke-width="5" stroke-linejoin="round"/>
  <!-- the speed readout's window -->
  <rect x="52" y="52" width="150" height="62" rx="4" fill="#060607" stroke="#2a2c31" stroke-width="2"/>
''')

svg('carbon-arm.svg', f'Carbon Signal\'s needle: a tapered gunmetal blade (its signal line and the dot in the headshell light up, '
    f'drawn on top). {ARM_NOTE}', ARM,
    ['<linearGradient id="gunCyl" x1="0" x2="1"><stop offset="0" stop-color="#121316"/><stop offset=".4" stop-color="#4a4e57"/><stop offset=".55" stop-color="#2c2f35"/><stop offset="1" stop-color="#0e0f11"/></linearGradient>'],
    '''
  <polygon points="-38,-176 38,-176 38,-96 30,-88 -30,-88 -38,-96" fill="url(#gunCyl)"/>
  <path d="M-38,-160 h14 M-38,-148 h8 M-38,-136 h14 M-38,-124 h8 M-38,-112 h14" stroke="#6a6e77" stroke-width="2"/>
  <rect x="-7" y="-88" width="14" height="90" fill="url(#gunCyl)"/>
  <polygon points="-12,10 12,10 40,652 27,652" fill="url(#gunCyl)" stroke="#555961" stroke-opacity=".5" stroke-width="1"/>
  <polygon points="-44,-18 -18,-44 18,-44 44,-18 44,18 18,44 -18,44 -44,18" fill="url(#gunCyl)" stroke="#555961" stroke-opacity=".5" stroke-width="1"/>
  <circle r="13" fill="#0a0a0b" stroke="#5a5e66" stroke-width="2"/>
  <g transform="rotate(22 0 740)">
    <polygon points="-24,648 24,648 28,660 28,752 20,764 -20,764 -28,752 -28,660" fill="url(#gunCyl)" stroke="#555961" stroke-opacity=".5" stroke-width="1"/>
    <rect x="-18" y="682" width="36" height="70" rx="2" fill="#08090a"/>
    <path d="M28,672 L80,660 L92,640" fill="none" stroke="#2c2f35" stroke-width="7" stroke-linejoin="round" stroke-linecap="round"/>
  </g>
''')

# ---------------------------------------------------------------- 4 · Piano Black & Chrome
svg('piano-deck.svg',
    f'Piano Black & Chrome ({STUDY}/4-piano-chrome.html): the gloss black lacquer and its long reflections, the chrome arm '
    'collar, the anti-skate dial, the cue lever and the rest. Deck units. The silver-filled name is drawn on top.',
    f'0 0 {W} {H}',
    [f'<clipPath id="plinth"><rect width="{W}" height="{H}" rx="20"/></clipPath>',
     linear('reflectA', 116, [('transparent', 0), ('transparent', .36), ('#ffffff10', .364), ('#ffffff10', .45), ('transparent', .454), ('transparent', 1)], W, H),
     linear('reflectB', 116, [('transparent', 0), ('transparent', .47), ('#ffffff08', .473), ('#ffffff08', .50), ('transparent', .503), ('transparent', 1)], W, H),
     linear('lacquer', 162, [('#2a2b30', 0), ('#0b0b0d', .52), ('#050506', 1)], W, H),
     '<radialGradient id="chromeR" cx=".35" cy=".3" r=".85"><stop offset="0" stop-color="#ffffff"/><stop offset=".35" stop-color="#c3c7cc"/><stop offset=".7" stop-color="#5b6068"/><stop offset="1" stop-color="#2a2d32"/></radialGradient>',
     '<linearGradient id="silverInlay" x1="0" y1="0" x2="1" y2="0"><stop offset="0" stop-color="#8d9299"/><stop offset=".45" stop-color="#f4f5f7"/><stop offset=".55" stop-color="#c7cbd0"/><stop offset="1" stop-color="#8d9299"/></linearGradient>'],
    f'''
  <g clip-path="url(#plinth)">
{layers(['lacquer', 'reflectB', 'reflectA'])}
    <rect width="{W}" height="{H}" rx="20" fill="none" stroke="#ffffff" stroke-opacity=".06" stroke-width="4"/>
    <rect width="{W}" height="2" fill="#ffffff" fill-opacity=".35"/>
  </g>
  <!-- arm board: chrome collar around a black well -->
  <circle cx="1180" cy="200" r="100" fill="url(#chromeR)"/>
  <circle cx="1180" cy="200" r="78" fill="#0b0b0d" stroke="#000" stroke-width="2"/>
  <!-- anti-skate dial and cue lever -->
  <circle cx="1080" cy="345" r="24" fill="url(#chromeR)"/><circle cx="1080" cy="345" r="10" fill="#111"/>
  <circle cx="1276" cy="330" r="22" fill="url(#chromeR)"/>
  <rect x="1268" y="330" width="16" height="96" rx="8" fill="url(#silverInlay)"/>
  <!-- arm rest -->
  <circle cx="1180" cy="656" r="18" fill="url(#chromeR)"/>
  <path d="M1163,648 q17,22 34,0" fill="none" stroke="#111" stroke-width="6" stroke-linecap="round"/>
  <!-- the rule over the name -->
  <path d="M42,970 h200" stroke="#c7cbd0" stroke-width="1.5" opacity=".45"/>
''')

svg('piano-arm.svg', f'Piano Black & Chrome\'s needle: a chrome J-arm, the knurled counterweight, the black headshell and its chrome '
    f'finger lift. {ARM_NOTE}', ARM,
    ['<linearGradient id="chromeCyl" x1="0" x2="1"><stop offset="0" stop-color="#3e4248"/><stop offset=".28" stop-color="#f6f7f9"/><stop offset=".45" stop-color="#9ba0a7"/><stop offset=".6" stop-color="#ffffff"/><stop offset=".85" stop-color="#565b62"/><stop offset="1" stop-color="#b9bdc2"/></linearGradient>',
     repeating('knurl', 180, [('transparent', 0), ('transparent', 6), ('#00000059', 6), ('#00000059', 9)], 92, 90, -46, -180)],
    '''
  <rect x="-46" y="-180" width="92" height="90" rx="8" fill="url(#chromeCyl)"/>
  <rect x="-46" y="-180" width="92" height="90" rx="8" fill="url(#knurl)"/>
  <rect x="-8" y="-92" width="16" height="96" fill="url(#chromeCyl)"/>
  <path d="M0,30 L0,470 C0,580 20,620 34,657" fill="none" stroke="#3e4248" stroke-width="17" stroke-linecap="round"/>
  <path d="M0,30 L0,470 C0,580 20,620 34,657" fill="none" stroke="#c9cdd2" stroke-width="12" stroke-linecap="round"/>
  <path d="M-2.5,30 L-2.5,470 C-2.5,580 17.5,620 31.5,657" fill="none" stroke="#fff" stroke-width="3.5" stroke-linecap="round"/>
  <path d="M3.5,40 L3.5,470 C3.5,580 23.5,620 37.5,650" fill="none" stroke="#6b7078" stroke-width="2"/>
  <circle r="50" fill="url(#chromeCyl)"/>
  <circle r="32" fill="#0b0b0d"/>
  <circle r="16" fill="url(#chromeCyl)"/>
  <g transform="rotate(22 0 740)">
    <path d="M24,666 C60,662 82,646 96,614" fill="none" stroke="#c9cdd2" stroke-width="7" stroke-linecap="round"/>
    <rect x="-28" y="645" width="56" height="120" rx="6" fill="#0c0c0e" stroke="#3a3d43" stroke-width="2"/>
    <rect x="-20" y="682" width="40" height="72" rx="4" fill="url(#chromeCyl)"/>
    <rect x="-20" y="694" width="40" height="5" fill="#0c0c0e"/>
  </g>
''')

# ---------------------------------------------------------------- 5 · Linear Tracking
END = 30
svg('linear-deck.svg',
    f'Linear Tracking ({STUDY}/5-linear-tracking.html): champagne aluminium between rosewood ends. Deck units. The rails, '
    'the housing and the carriage go over the record (linear-rails.svg, linear-arm.svg); the name is drawn on top.',
    f'0 0 {W} {H}',
    [f'<clipPath id="plinth"><rect width="{W}" height="{H}" rx="10"/></clipPath>',
     linear('light', 168, [('#ffffff40', 0), ('transparent', .30), ('#00000014', .65), ('#0000002e', 1)], W, H),
     repeating('brush', 0, [('#ffffff12', 0), ('#ffffff12', 1 * PX), ('transparent', 1 * PX), ('transparent', 2 * PX), ('#0000000d', 2 * PX), ('#0000000d', 3 * PX)], W, H),
     linear('metal', 90, [('#b9b1a3', 0), ('#d9d3c8', .40), ('#c9c2b6', .60), ('#b2aa9c', 1)], W, H),
     repeating('roseGrainL', 90, [('transparent', 0), ('transparent', 2 * PX), ('#00000030', 2 * PX), ('#00000030', 3 * PX), ('transparent', 3 * PX), ('transparent', 7 * PX)], END, H, 0, 0),
     linear('roseL', 90, [('#3e1d12', 0), ('#5e2c1a', .5), ('#3a1a10', 1)], END, H, 0, 0),
     repeating('roseGrainR', 90, [('transparent', 0), ('transparent', 2 * PX), ('#00000030', 2 * PX), ('#00000030', 3 * PX), ('transparent', 3 * PX), ('transparent', 7 * PX)], END, H, W - END, 0),
     linear('roseR', 90, [('#3e1d12', 0), ('#5e2c1a', .5), ('#3a1a10', 1)], END, H, W - END, 0)],
    f'''
  <g clip-path="url(#plinth)">
{layers(['metal', 'brush', 'light'])}
    <rect width="{W}" height="2" fill="#ffffff" fill-opacity=".63"/>
    <!-- the rosewood ends -->
    <rect width="{END}" height="{H}" fill="url(#roseL)"/>
    <rect width="{END}" height="{H}" fill="url(#roseGrainL)"/>
    <rect x="{W - END}" width="{END}" height="{H}" fill="url(#roseR)"/>
    <rect x="{W - END}" width="{END}" height="{H}" fill="url(#roseGrainR)"/>
    <rect width="{END}" height="2" fill="#ffffff" fill-opacity=".15"/>
    <rect x="{W - END}" width="{END}" height="2" fill="#ffffff" fill-opacity=".15"/>
  </g>
''')

svg('linear-rails.svg', 'Linear Tracking\'s twin rails, cantilevered over the record from the housing on the right (the line on '
    'the housing that takes the album colour is drawn on top). Deck units.',
    f'0 0 {W} {H}',
    ['<linearGradient id="rail" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#f4f1ea"/><stop offset=".5" stop-color="#b9b2a5"/><stop offset="1" stop-color="#7d7669"/></linearGradient>',
     '<linearGradient id="housing" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#e3ddd2"/><stop offset="1" stop-color="#a9a194"/></linearGradient>'],
    '''
  <rect x="770" y="462" width="430" height="12" rx="6" fill="url(#rail)"/>
  <rect x="770" y="494" width="430" height="12" rx="6" fill="url(#rail)"/>
  <rect x="1180" y="420" width="104" height="170" rx="14" fill="url(#housing)" stroke="#8a8377" stroke-width="2"/>
  <rect x="1196" y="436" width="72" height="4" rx="2" fill="#000000" fill-opacity=".12"/>
''')

svg('linear-arm.svg', 'Linear Tracking\'s pickup carriage. Arm units: the stylus at (0, 0), where the carriage sits on the record.', ARM,
    ['<linearGradient id="carriage" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#f8f5ef"/><stop offset="1" stop-color="#a39b8e"/></linearGradient>'],
    '''
  <rect x="-38" y="-92" width="76" height="72" rx="10" fill="url(#carriage)" stroke="#8a8377" stroke-width="2"/>
  <rect x="-38" y="-62" width="76" height="4" fill="#000000" fill-opacity=".1"/>
  <rect x="-16" y="-26" width="32" height="46" rx="5" fill="#1d1c1a"/>
  <rect x="-16" y="-14" width="32" height="5" fill="#c9a15b"/>
''')

# ---------------------------------------------------------------- 6 · Smoked Acrylic
svg('acrylic-deck.svg',
    f'Smoked Acrylic ({STUDY}/6-smoked-acrylic.html): the acrylic\'s edges and inner glow line, the clear arm board and '
    'rest. Deck units. The smoke tint and what shows through it, the edge light and the name are drawn around it.',
    f'0 0 {W} {H}',
    [f'<clipPath id="plinth"><rect width="{W}" height="{H}" rx="26"/></clipPath>'],
    f'''
  <g clip-path="url(#plinth)">
    <rect x="12" y="12" width="{W - 24}" height="{H - 24}" rx="14" fill="none" stroke="#ffffff" stroke-opacity=".03" stroke-width="24"/>
    <rect x="1" y="1" width="{W - 2}" height="{H - 2}" rx="25" fill="none" stroke="#ffffff" stroke-opacity=".18" stroke-width="2"/>
    <rect width="{W}" height="2" fill="#ffffff" fill-opacity=".3"/>
  </g>
  <!-- clear arm board and rest -->
  <circle cx="1180" cy="200" r="92" fill="#ffffff" fill-opacity=".05" stroke="#ffffff" stroke-opacity=".22" stroke-width="2"/>
  <circle cx="1180" cy="200" r="80" fill="none" stroke="#ffffff" stroke-opacity=".08" stroke-width="2"/>
  <rect x="1186" y="636" width="34" height="24" rx="12" fill="#ffffff" fill-opacity=".12" stroke="#ffffff" stroke-opacity=".3" stroke-width="2"/>
''')

svg('acrylic-arm.svg', f'Smoked Acrylic\'s needle: a slim matte-white tube and headshell, the clear acrylic counterweight. {ARM_NOTE}', ARM,
    ['<linearGradient id="whiteCyl" x1="0" x2="1"><stop offset="0" stop-color="#b9bcc2"/><stop offset=".45" stop-color="#ffffff"/><stop offset="1" stop-color="#c4c7cc"/></linearGradient>'],
    '''
  <rect x="-40" y="-168" width="80" height="78" rx="18" fill="#ffffff" fill-opacity=".15" stroke="#ffffff" stroke-opacity=".5" stroke-width="2"/>
  <rect x="-12" y="-150" width="24" height="42" rx="4" fill="url(#whiteCyl)"/>
  <rect x="-5" y="-92" width="10" height="94" fill="url(#whiteCyl)"/>
  <path d="M0,0 L34,657" stroke="#e9ebee" stroke-width="11" stroke-linecap="round"/>
  <path d="M-2,10 L32,650" stroke="#ffffff" stroke-width="3" stroke-linecap="round"/>
  <circle r="40" fill="url(#whiteCyl)"/>
  <circle r="14" fill="#d5d8dc" stroke="#ffffff" stroke-opacity=".67" stroke-width="2"/>
  <g transform="rotate(22 0 740)">
    <rect x="-22" y="648" width="44" height="110" rx="14" fill="url(#whiteCyl)"/>
    <rect x="-15" y="684" width="30" height="62" rx="6" fill="#1b1d22"/>
    <path d="M22,668 C52,664 70,652 80,632" fill="none" stroke="#e9ebee" stroke-width="6" stroke-linecap="round"/>
  </g>
''')
print('ok')
