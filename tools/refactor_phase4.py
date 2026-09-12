"""Phase 4 page refactor: task-row triplet pages + Files + TextExpansion."""
import re
import sys

def resub_assert(s, pattern, repl, count, label, flags=0):
    n = len(re.findall(pattern, s, flags))
    assert n == count, f'{label}: expected {count}, found {n} for: {pattern!r}'
    return re.sub(pattern, repl, s, flags=flags)

def sub_assert(s, old, new, count=1, label=''):
    n = s.count(old)
    assert n == count, f'{label}: expected {count}, found {n} for: {old[:90]!r}'
    return s.replace(old, new)

def transform_open_tags(s, tagname, fn):
    pat = re.compile(rf'<{tagname}\b[^>]*?>')
    return pat.sub(lambda m: fn(m.group(0)), s)

def strip_attr(tag, attr):
    return re.sub(rf'\s*\n?[ \t]*{attr}=[^\n>]*?(?=\n|/?>)', '', tag, count=1)

def cards(s):
    """SurfaceColor card Borders -> page-card / empty-state (keeps x:Name/IsVisible etc.)."""
    counter = {'n': 0}
    def conv(tag):
        if 'SurfaceColor' in tag and 'Classes=' not in tag:
            cls = 'empty-state' if 'Padding="32"' in tag else 'page-card'
            out = re.sub(r'[ \t]*Background="\{DynamicResource SurfaceColor\}"\s*\n?', '', tag)
            out = re.sub(r'[ \t]*CornerRadius="12"\s*\n?', '', out)
            out = re.sub(r'[ \t]*Padding="16"(\s*\n)?', '\n', out)
            out = re.sub(r'[ \t]*Padding="32"(\s*\n)?', '\n', out)
            out = out.replace('<Border', f'<Border Classes="{cls}"', 1)
            out = re.sub(r'\n[ \t]*\n', '\n', out)
            counter['n'] += 1
            return out
        return tag
    s = transform_open_tags(s, 'Border', conv)
    return s, counter['n']

def triplet(s, keys):
    s, n_cards = cards(s)
    assert n_cards == keys['cards'], f'cards: {n_cards}'

    # Task row -> shared class + selection binding
    s = resub_assert(s, r'<Border Padding="10,8"\s*\n\s*CornerRadius="8"\s*\n\s*Margin="0,2"\s*\n\s*Classes="row-item no-hover"\s*\n\s*Background="\{DynamicResource BackgroundBrush\}">',
                     '<Border Classes="task-row"\n                                        Classes.selected="{Binding IsSelected}">', 1, 'task row')

    # Section titles (12px SemiBold secondary) -> card-title
    def conv_title(tag):
        if ('TextSecondaryBrush' in tag and 'FontSize="12"' in tag
                and 'FontWeight="SemiBold"' in tag and 'Classes=' not in tag):
            out = re.sub(r'[ \t]*FontWeight="SemiBold"\s*\n?', '', tag)
            out = re.sub(r'[ \t]*Foreground="\{DynamicResource TextSecondaryBrush\}"\s*\n?', '', out)
            out = re.sub(r'[ \t]*FontSize="12"\s*\n?', '', out)
            out = re.sub(r'\n[ \t]*\n', '\n', out)
            return out.replace('<TextBlock', '<TextBlock Classes="card-title"', 1)
        return tag
    s = transform_open_tags(s, 'TextBlock', conv_title)
    n_titles = s.count('Classes="card-title"')
    assert n_titles == keys['titles'], f'titles: {n_titles}'

    # Task count meta -> caption
    def conv_count(tag):
        if 'TaskCountText' in tag and 'TextSecondaryBrush' in tag and 'Classes=' not in tag:
            out = re.sub(r'[ \t]*Foreground="\{DynamicResource TextSecondaryBrush\}"\s*\n?', '', tag)
            out = re.sub(r'[ \t]*FontSize="12"\s*\n?', '', out)
            out = re.sub(r'\n[ \t]*\n', '\n', out)
            return out.replace('<TextBlock', '<TextBlock Classes="caption"', 1)
        return tag
    s = transform_open_tags(s, 'TextBlock', conv_count)

    # Hotkey/field chip (Shortcut/Trigger only; Schedule has none)
    chip_pat = r'<Border Background="\{DynamicResource SurfaceHoverBrush\}"\s*\n\s*CornerRadius="4"\s*\n\s*Padding="6,2"(\s*\n\s*IsVisible="[^"]*")?>'
    n_chips = len(re.findall(chip_pat, s))
    assert n_chips in (0, 1), f'chips: {n_chips}'
    if n_chips:
        s = resub_assert(s, chip_pat,
                         lambda m: '<Border Classes="chip accent"' + (m.group(1) or '') + '>', 1, 'chip border')
        s = resub_assert(s, r'<TextBlock (Text="[^"]*")\s*\n\s*FontSize="11"\s*\n\s*FontFamily="Consolas, monospace"\s*\n\s*Foreground="\{DynamicResource PrimaryBrush\}"/>',
                         r'<TextBlock Classes="mono" \1/>', 1, 'chip text')

    # Empty state contents
    s = resub_assert(s, r'<PathIcon Data="\{icons:AppIcon (\w+)\}" Width="48" Height="48" Opacity="0.8" HorizontalAlignment="Center"/>',
                     r'<PathIcon Classes="empty-state-icon" Data="{icons:AppIcon \1}" HorizontalAlignment="Center"/>', 1, 'empty icon')
    s = resub_assert(s, r'<TextBlock Text="\{localization:Loc (\w+)\}"\s*\n\s*FontSize="16"\s*\n\s*FontWeight="Medium"\s*\n\s*HorizontalAlignment="Center"/>',
                     r'<TextBlock Classes="empty-title" Text="{localization:Loc \1}"/>', 1, 'empty title')

    # Field labels: caption-class tags whose key is not a tip/hint -> field-label
    def conv_caption(tag):
        m = re.search(r'localization:Loc (\w+)', tag)
        if m and 'Classes="caption"' in tag and not ('Tip' in m.group(1) or 'Hint' in m.group(1) or 'Tooltip' in m.group(1).lower()):
            return tag.replace('Classes="caption"', 'Classes="field-label"', 1)
        return tag
    s = transform_open_tags(s, 'TextBlock', conv_caption)
    return s

def shortcut(s):
    s = triplet(s, {'cards': 4, 'titles': 3})
    # Macro file display box -> nested panel
    counter = {'n': 0}
    def conv_nested(tag):
        if 'BackgroundBrush' in tag and 'CornerRadius="6"' in tag and 'Padding="12,8"' in tag and 'Classes=' not in tag:
            out = re.sub(r'[ \t]*Background="\{DynamicResource BackgroundBrush\}"\s*\n?', '', tag)
            out = re.sub(r'[ \t]*CornerRadius="6"\s*\n?', '', out)
            out = re.sub(r'[ \t]*Padding="12,8"\s*\n?', '', out)
            out = out.replace('<Border', '<Border Classes="nested-panel"', 1)
            counter['n'] += 1
            return out
        return tag
    s = transform_open_tags(s, 'Border', conv_nested)
    assert counter['n'] == 1, f"nested: {counter['n']}"
    return s

def trigger(s):
    return triplet(s, {'cards': 4, 'titles': 3})

def schedule(s):
    s = triplet(s, {'cards': 7, 'titles': 6})
    # Speed value readout
    s = resub_assert(s, r'<TextBlock Grid\.Column="1"\s*\n\s*(Text="\{Binding[^"]*")\s*\n\s*FontWeight="Medium"\s*\n\s*FontSize="16"\s*\n\s*Foreground="\{DynamicResource PrimaryBrush\}"/>',
                     '<TextBlock Grid.Column="1"\n                                      \\1\n                                      Classes="value-text"/>', 1, 'schedule speed value')
    # Status labels -> caption (parity with Shortcut/Trigger)
    for key in ('Schedule_LastRun', 'Schedule_NextRun', 'Schedule_StatusLabel'):
        pat = rf'<TextBlock (Grid\.Row="\d" Grid\.Column="0") Text="\{{localization:Loc {key}\}}" FontSize="12" Foreground="\{{DynamicResource TextSecondaryBrush\}}"/>'
        repl = lambda m, k=key: f'<TextBlock {m.group(1)} Classes="caption" Text="{{localization:Loc {k}}}"/>'
        s = resub_assert(s, pat, repl, 1, key)
    # Status values: drop redundant size (defaults to body size), keep binding
    s = resub_assert(s, r'(<TextBlock Grid\.Row="\d" Grid\.Column="1"\s*\n\s*Text="\{Binding [^"]+\}"\s*)\n\s*FontSize="12"/>',
                     r'\1/>', 3, 'schedule status values')
    # Tips -> field-hint
    s = resub_assert(s, r'<PathIcon Data="\{icons:AppIcon Tip\}" Width="11" Height="11" Opacity="0.85"/>\s*\n\s*<TextBlock Text="\{localization:Loc (\w+)\}" FontSize="11" Foreground="\{DynamicResource TextSecondaryBrush\}"/>',
                     r'<PathIcon Data="{icons:AppIcon Tip}" Classes="caption-icon"/>\n                        <TextBlock Classes="field-hint" Text="{localization:Loc \1}"/>', 2, 'schedule tips')
    # Empty state hint -> caption
    s = resub_assert(s, r'<TextBlock Text="\{localization:Loc Schedule_SelectTaskHint\}"\s*\n\s*FontSize="12"\s*\n\s*Foreground="\{DynamicResource TextSecondaryBrush\}"\s*\n\s*HorizontalAlignment="Center"/>',
                     '<TextBlock Classes="caption"\n                              HorizontalAlignment="Center"\n                              Text="{localization:Loc Schedule_SelectTaskHint}"/>', 1, 'schedule empty hint')
    return s

def files(s):
    # List card: Color-vs-Brush border cleanup -> page-card compact
    s = sub_assert(s, '''<Border Background="{DynamicResource SurfaceColor}"
                            BorderBrush="{DynamicResource SurfaceHoverColor}"
                            BorderThickness="1"
                            CornerRadius="12"
                            Padding="8">''',
                   '<Border Classes="page-card compact">', 1, 'files list card')
    # Inner field labels
    s = sub_assert(s, '<TextBlock Classes="label" Text="{localization:Loc Files_MacroName}"/>',
                   '<TextBlock Classes="field-label" Text="{localization:Loc Files_MacroName}"/>', 1, 'macro name label')
    s = sub_assert(s, '<TextBlock Classes="label" Text="{localization:Loc Files_SequenceRepeat}"/>',
                   '<TextBlock Classes="field-label" Text="{localization:Loc Files_SequenceRepeat}"/>', 1, 'seq repeat label')
    return s

def textexpansion(s):
    # Root rhythm: 24 -> 16 like other pages
    s = sub_assert(s, '<ScrollViewer Padding="16">\n            <StackPanel Spacing="24">',
                   '<ScrollViewer Padding="16">\n            <StackPanel Spacing="16">', 1, 'root spacing')
    s, n_cards = cards(s)
    assert n_cards == 2, f'cards: {n_cards}'
    # Card titles (12 Bold secondary)
    s = resub_assert(s, r'<TextBlock Text="\{localization:Loc TextExpansion_AddNewExpansion\}"\s*\n\s*FontSize="12"\s*\n\s*FontWeight="Bold"\s*\n\s*Foreground="\{DynamicResource TextSecondaryBrush\}"/>',
                     '<TextBlock Classes="card-title" Text="{localization:Loc TextExpansion_AddNewExpansion}"/>', 1, 'add title')
    s = resub_assert(s, r'<TextBlock Text="\{localization:Loc TextExpansion_SavedExpansions\}"\s*\n\s*VerticalAlignment="Center"\s*\n\s*FontSize="12"\s*\n\s*FontWeight="Bold"\s*\n\s*Foreground="\{DynamicResource TextSecondaryBrush\}"/>',
                     '<TextBlock Classes="card-title"\n                                       VerticalAlignment="Center"\n                                       Text="{localization:Loc TextExpansion_SavedExpansions}"/>', 1, 'saved title')
    # Count meta -> caption
    s = resub_assert(s, r'<TextBlock Text="\{Binding ExpansionCountText\}"\s*\n\s*VerticalAlignment="Center"\s*\n\s*Foreground="\{DynamicResource TextSecondaryBrush\}"\s*\n\s*FontSize="12"/>',
                     '<TextBlock Classes="caption" Text="{Binding ExpansionCountText}"\n                                           VerticalAlignment="Center"/>', 1, 'count meta')
    # Field labels (12 secondary, no wrap)
    s = resub_assert(s, r'<TextBlock Text="\{localization:Loc ([^}]+)\}" FontSize="12" Foreground="\{DynamicResource TextSecondaryBrush\}"/>',
                     r'<TextBlock Classes="field-label" Text="{localization:Loc \1}"/>', 6, 'tx field labels')
    # Inline input chrome -> global style
    s = resub_assert(s, r'\s*\n\s*CornerRadius="6"\s*\n\s*Padding="10"/>', '/>', 2, 'textboxes')
    s = resub_assert(s, r'\s*\n\s*CornerRadius="6">', '>', 3, 'combobox radius')
    # Chips
    s = resub_assert(s, r'<Border Background="\{DynamicResource SurfaceHoverBrush\}" CornerRadius="4" Padding="4,2"( HorizontalAlignment="Left")?(\s*\n?\s*IsVisible="[^"]*")?>',
                     lambda m: '<Border Classes="chip"' + (m.group(1) or '') + (m.group(2) or '') + '>', 3, 'tx chips')
    s = resub_assert(s, r'<TextBlock Text="[^"]*"\s*\n\s*FontSize="10"\s*\n\s*Foreground="\{DynamicResource TextSecondaryBrush\}"/>',
                     lambda m: m.group(0).split('\n')[0].rstrip(), 3, 'tx chip texts placeholder') if False else s
    # chip inner texts: 10px secondary -> chip style handles it
    def conv_chip_text(tag):
        if 'FontSize="10"' in tag and 'TextSecondaryBrush' in tag:
            out = re.sub(r'\s*\n\s*FontSize="10"', '', tag)
            out = re.sub(r'\s*\n\s*Foreground="\{DynamicResource TextSecondaryBrush\}"', '', out)
            return re.sub(r'\n[ \t]*\n', '\n', out)
        return tag
    s = transform_open_tags(s, 'TextBlock', conv_chip_text)
    # Row wrapper: pointless transparent border attrs
    s = sub_assert(s, '''<Border Background="Transparent"
                                            BorderBrush="{DynamicResource SurfaceHoverBrush}"
                                            BorderThickness="0,0,0,0"
                                            Padding="0,12">''',
                   '<Border Padding="0,12">', 1, 'row wrapper')
    # Footer tip + spacer removal (tip text was already converted to field-label above)
    s = sub_assert(s, '''<StackPanel Orientation="Horizontal" HorizontalAlignment="Center" Spacing="4">
                    <PathIcon Data="{icons:AppIcon Tip}" Classes="caption-icon"/>
                    <TextBlock Classes="field-label" Text="{localization:Loc TextExpansion_EnableInSettingsTip}"/>
                </StackPanel>

                <!-- NEW: Bottom Spacer -->
                <Rectangle Height="40" Fill="Transparent"/>''',
                   '''<StackPanel Classes="footer-tip">
                    <PathIcon Data="{icons:AppIcon Tip}" Classes="caption-icon"/>
                    <TextBlock Text="{localization:Loc TextExpansion_EnableInSettingsTip}" Classes="caption"/>
                </StackPanel>''', 1, 'tx footer tip')
    return s

FUNCS = {'shortcut': shortcut, 'trigger': trigger, 'schedule': schedule, 'files': files, 'textexpansion': textexpansion}

def main():
    names = sys.argv[1:] or list(FUNCS)
    mapping = {'shortcut': 'Shortcut', 'trigger': 'Trigger', 'schedule': 'Schedule',
               'files': 'Files', 'textexpansion': 'TextExpansion'}
    for name in names:
        path = f'src/CrossMacro.UI/Views/Tabs/{mapping[name]}TabView.axaml'
        s = open(path, encoding='utf-8').read()
        s = FUNCS[name](s)
        open(path, 'w', encoding='utf-8', newline='').write(s)
        print(f'{name}: ok')

if __name__ == '__main__':
    main()
