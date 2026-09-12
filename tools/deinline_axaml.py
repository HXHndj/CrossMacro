"""One-off AXAML de-inlining for the UI unification refactor.

Rules (tag-scoped, attribute-exact):
- TextBox: drop inline attrs now provided by the global style
  (Background SurfaceHoverBrush / Foreground TextPrimaryBrush / BorderThickness 0 /
   CornerRadius 4 / Padding 8,6).
- ComboBox: drop Background SurfaceHoverBrush|SurfaceBrush and Foreground TextPrimaryBrush.
- TextBlock with TextSecondary + FontSize 12|11|10: replace both with
  Classes="field-label" (12) or "field-hint" (11/10); drop TextWrapping="Wrap" for hints.
Run: python deinline_axaml.py FILE [FILE...]
"""
import re
import sys

TAG_RE = re.compile(r"<(TextBox|ComboBox|TextBlock)\b[^>]*?/?>", re.DOTALL)

ATTRS_TEXTBOX = [
    r'\s*Background="\{DynamicResource SurfaceHoverBrush\}"',
    r'\s*Foreground="\{DynamicResource TextPrimaryBrush\}"',
    r'\s*BorderThickness="0"',
    r'\s*CornerRadius="4"',
    r'\s*Padding="8,6"',
]
ATTRS_COMBOBOX = [
    r'\s*Background="\{DynamicResource SurfaceHoverBrush\}"',
    r'\s*Background="\{DynamicResource SurfaceBrush\}"',
    r'\s*Foreground="\{DynamicResource TextPrimaryBrush\}"',
]
SECONDARY = '{DynamicResource TextSecondaryBrush}'


def convert_textblock(tag: str) -> str:
    if 'TextSecondaryBrush' not in tag or 'Classes=' in tag:
        return tag
    for size, cls in (('12', 'field-label'), ('11', 'field-hint'), ('10', 'field-hint')):
        if f'FontSize="{size}"' not in tag:
            continue
        out = tag.replace(f' Foreground="{SECONDARY}"', '')
        out = out.replace(f'Foreground="{SECONDARY}" ', '')
        out = out.replace(f'FontSize="{size}" ', '').replace(f' FontSize="{size}"', '')
        if cls == 'field-hint':
            out = out.replace('TextWrapping="Wrap" ', '').replace(' TextWrapping="Wrap"', '')
        out = out.replace('<TextBlock', f'<TextBlock Classes="{cls}"', 1)
        return out
    return tag


def process_tag(m: re.Match) -> str:
    tag, name = m.group(0), m.group(1)
    if name == 'TextBlock':
        return convert_textblock(tag)
    attrs = ATTRS_TEXTBOX if name == 'TextBox' else ATTRS_COMBOBOX
    out = tag
    for pattern in attrs:
        out = re.sub(pattern, '', out)
    return out


def main(paths: list[str]) -> None:
    for path in paths:
        with open(path, encoding='utf-8') as f:
            text = f.read()
        new = TAG_RE.sub(process_tag, text)
        with open(path, 'w', encoding='utf-8', newline='') as f:
            f.write(new)
        print(f'{path}: {sum(1 for _ in TAG_RE.finditer(text))} tags scanned')


if __name__ == '__main__':
    main(sys.argv[1:])
