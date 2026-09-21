#!/usr/bin/env python3
"""
Convert the Claude Design handoff artboards (.dc.html) into a real static site.

The artboards only render inside Claude Design's canvas: they are wrapped in
<x-dc>, carry a <helmet> instead of a <head>, and use React-style bindings
({{ handler }}, ref=, onClick=, camelCase boolean attributes) that are resolved
by a React runtime (support.js) which is not shipped with the bundle.

This strips all of that and emits plain HTML that stands on its own, moving the
interactivity to assets/js/site.js.
"""

import html
import re
import pathlib
import sys

SRC = pathlib.Path(sys.argv[1])
OUT = pathlib.Path(sys.argv[2])

# artboard -> (output filename, <title>, meta description)
PAGES = {
    "Marvin-Home": (
        "index.html",
        "Marvin — An open source tracked robot",
        "A small tracked robot with an expressive two-axis head. Every file "
        "needed to build one is published: firmware, electronics, mechanics.",
    ),
    "Marvin-Build": (
        "build.html",
        "Build — Marvin",
        "Print the parts, wire the electronics, assemble the chassis and flash "
        "the firmware.",
    ),
    "Marvin-Drive": (
        "drive.html",
        "Drive — Marvin",
        "Connect over Bluetooth and drive Marvin from a browser. The command "
        "language, the controller app, and troubleshooting.",
    ),
    "Marvin-Contribute": (
        "contribute.html",
        "Contribute — Marvin",
        "Four ways to contribute to Marvin: firmware, software, mechanics and "
        "electronics.",
    ),
    "Marvin-Reference": (
        "reference.html",
        "Reference — Marvin",
        "Command protocol, Bluetooth service, pin map, printed parts and "
        "licensing.",
    ),
}

LINK_MAP = {f"{stem}.dc.html": name for stem, (name, _, _) in PAGES.items()}

# Runs before first paint so an explicit scheme choice never flashes the other
# one. With nothing stored, the stylesheet's prefers-color-scheme rules stay in
# charge. Kept inline and tiny on purpose — a deferred script is too late.
THEME_BOOTSTRAP = (
    "<script>(function(){try{var t=localStorage.getItem('marvin-theme');"
    "if(t==='light'||t==='dark')"
    "document.documentElement.setAttribute('data-theme',t);}catch(e){}})();</script>"
)

ATTR_RE = re.compile(r'([a-zA-Z-]+)="([^"]*)"')


def slot_template() -> str:
    """The MarvinSlot component body, as a format template."""
    src = (SRC / "MarvinSlot.dc.html").read_text()
    fig = re.search(r"(<figure class=\"blueprint\".*?</figure>)", src, re.S)
    if not fig:
        raise SystemExit("could not locate the MarvinSlot <figure>")
    return fig.group(1)


SLOT = slot_template()


def inline_slots(markup: str) -> str:
    """Replace <dc-import name="MarvinSlot" ...> with the component markup."""

    def repl(m: re.Match) -> str:
        attrs = dict(ATTR_RE.findall(m.group(0)))
        out = SLOT
        for key in ("fig", "kind", "title", "shows", "ratio", "status"):
            out = out.replace("{{ %s }}" % key, attrs.get(key, ""))
        # The slot is placed into a sized grid cell by its parent.
        return out

    return re.sub(
        r'<dc-import\s+name="MarvinSlot".*?</dc-import>', repl, markup, flags=re.S
    )


def de_react(markup: str) -> str:
    """Turn React-flavoured attributes into real HTML."""
    # Boolean media attributes: muted="{{ true }}" -> muted
    for attr, real in (
        ("muted", "muted"),
        ("loop", "loop"),
        ("playsInline", "playsinline"),
        ("autoPlay", "autoplay"),
        ("controls", "controls"),
    ):
        markup = markup.replace(f'{attr}="{{{{ true }}}}"', real)

    # Element refs become data hooks that site.js queries.
    markup = re.sub(r'ref="\{\{\s*(\w+)\s*\}\}"', r'data-ref="\1"', markup)

    # Event handlers become declarative actions.
    markup = re.sub(
        r'on(?:Click)="\{\{\s*(\w+)\s*\}\}"', r'data-action="\1"', markup
    )

    # The skip link's show/hide handlers are replaced by a CSS :focus rule.
    markup = re.sub(r'\s*on(?:Focus|Blur)="\{\{\s*\w+\s*\}\}"', "", markup)
    markup = markup.replace(
        '<a href="#content" style="position:absolute;left:-9999px',
        '<a href="#content" class="skip-link" style="position:absolute;left:-9999px',
    )

    # Live text bindings get a sensible server-rendered initial value; site.js
    # keeps them in step from there.
    for token, initial in (
        ("themeLabel", "Dark"),
        ("playLabel", "Pause"),
        ("demoLabel", "Pause"),
    ):
        markup = markup.replace("{{ %s }}" % token, initial)

    return markup


def rewrite_paths(markup: str) -> str:
    for old, new in LINK_MAP.items():
        markup = markup.replace(f'href="{old}', f'href="{new}')
    markup = markup.replace('src="uploads/', 'src="assets/video/')
    markup = re.sub(r'poster="([^"/]+\.png)"', r'poster="assets/img/\1"', markup)
    return markup


def convert(stem: str) -> str:
    name, title, description = PAGES[stem]
    raw = (SRC / f"{stem}.dc.html").read_text()

    body = re.search(r"<x-dc>(.*)</x-dc>", raw, re.S)
    if not body:
        raise SystemExit(f"{stem}: no <x-dc> wrapper found")
    markup = body.group(1)

    # Drop the canvas <helmet>; its contents are rebuilt as a real <head>.
    markup = re.sub(r"<helmet>.*?</helmet>", "", markup, flags=re.S)

    markup = inline_slots(markup)
    markup = de_react(markup)
    markup = rewrite_paths(markup)
    markup = markup.strip()

    if "{{" in markup or "dc-import" in markup or "<x-dc" in markup:
        leftover = re.findall(r"\{\{[^}]*\}\}", markup)[:5]
        raise SystemExit(f"{stem}: unconverted canvas syntax remains: {leftover}")

    return f"""<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{html.escape(title)}</title>
<meta name="description" content="{html.escape(description)}">
<meta name="color-scheme" content="light dark">
<link rel="icon" href="assets/img/favicon.svg" type="image/svg+xml">
<meta property="og:title" content="{html.escape(title)}">
<meta property="og:description" content="{html.escape(description)}">
<meta property="og:type" content="website">
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<!-- Same URL the design system's @import uses, so this is one request, not two.
     Requesting it here starts the download immediately instead of waiting for
     industry.css to parse first. -->
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Barlow:wght@400;500;700&family=Barlow+Condensed:wght@400;600&display=swap">
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=IBM+Plex+Mono:wght@400;500&display=swap">
<link rel="stylesheet" href="assets/css/industry.css">
<link rel="stylesheet" href="assets/css/site.css">
{THEME_BOOTSTRAP}
<script src="assets/js/site.js" defer></script>
</head>
<body>
{markup}
</body>
</html>
"""


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for stem in PAGES:
        name = PAGES[stem][0]
        (OUT / name).write_text(convert(stem))
        print(f"  {stem}.dc.html -> {name}")


if __name__ == "__main__":
    main()
