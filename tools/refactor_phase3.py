"""Phase 3 page refactor: Settings / Recording / Playback -> shared classes.
Usage: python refactor_phase3.py [settings|recording|playback ...]  (default: all pending)
"""
import re
import sys

def sub_assert(s, old, new, count=1, label=''):
    n = s.count(old)
    assert n == count, f'{label}: expected {count}, found {n} for: {old[:80]!r}'
    return s.replace(old, new)

def resub_assert(s, pattern, repl, count, label, flags=0):
    n = len(re.findall(pattern, s, flags))
    assert n == count, f'{label}: expected {count}, found {n} for: {pattern!r}'
    return re.sub(pattern, repl, s, flags=flags)

def transform_tags(s, tagname, fn):
    pat = re.compile(rf'<{tagname}\b[^>]*?(/?)>', re.DOTALL)
    return pat.sub(lambda m: fn(m.group(0)), s)

def textblock_field_labels(s):
    """TextBlock + TextSecondary + FontSize 12|10 + Wrap -> field-hint (descriptions)."""
    def conv(tag):
        if 'TextSecondaryBrush' not in tag or 'Classes=' in tag or 'FontSize' not in tag:
            return tag
        for size in ('12', '10'):
            if f'FontSize="{size}"' in tag and 'TextWrapping' in tag:
                out = tag.replace(f' Foreground="{{DynamicResource TextSecondaryBrush}}"', '')
                out = out.replace(f'Foreground="{{DynamicResource TextSecondaryBrush}}" ', '')
                out = out.replace(f'FontSize="{size}" ', '').replace(f' FontSize="{size}"', '')
                out = out.replace('TextWrapping="Wrap" ', '').replace(' TextWrapping="Wrap"', '')
                return out.replace('<TextBlock', '<TextBlock Classes="field-hint"', 1)
        return tag
    return transform_tags(s, 'TextBlock', conv)

def settings(s):
    s = sub_assert(s, '<Border Background="{DynamicResource SurfaceColor}" CornerRadius="8" Padding="12">',
                   '<Border Classes="page-card">', 4, 'settings cards')
    s = sub_assert(s, '<TextBlock Text="{localization:Loc Settings_GlobalHotkeys}" FontWeight="Bold" Foreground="{DynamicResource TextPrimaryBrush}"/>',
                   '<TextBlock Classes="card-title" Text="{localization:Loc Settings_GlobalHotkeys}"/>', 1, 'hotkeys title')
    s = sub_assert(s, '<TextBlock Text="{localization:Loc Settings_Profiles}" FontWeight="Bold" Foreground="{DynamicResource TextPrimaryBrush}"/>',
                   '<TextBlock Classes="card-title" Text="{localization:Loc Settings_Profiles}"/>', 1, 'profiles title')
    s = resub_assert(s, r'<TextBlock Text="\{localization:Loc Settings_SupportTitle\}"\s*\n\s*FontWeight="Bold"\s*\n\s*Foreground="\{DynamicResource TextPrimaryBrush\}"/>',
                     '<TextBlock Classes="card-title" Text="{localization:Loc Settings_SupportTitle}"/>', 1, 'support title')
    s = resub_assert(s, r'<Grid ColumnDefinitions="\*,Auto" ColumnSpacing="12"',
                     '<Grid Classes="setting-row" ColumnDefinitions="*,Auto" ColumnSpacing="12"', 7, 'setting rows')
    s = resub_assert(s, r'FontWeight="Medium"\s*\n\s*Foreground="\{DynamicResource TextPrimaryBrush\}"\s*\n\s*TextWrapping="Wrap"',
                     'Classes="row-title"\n                                       TextWrapping="Wrap"', 7, 'row titles')
    s = sub_assert(s, '''<Button Grid.Column="1" 
                                Command="{Binding OpenGitHub}"
                                Classes="primary"
                                Background="{DynamicResource PrimaryBrush}"
                                VerticalAlignment="Center">''',
                   '''<Button Grid.Column="1" 
                                Command="{Binding OpenGitHub}"
                                Classes="primary"
                                VerticalAlignment="Center">''', 1, 'support button')
    s = sub_assert(s, '''<PathIcon Data="{icons:AppIcon GitHub}"
                                          Foreground="{DynamicResource TextOnPrimaryBrush}"
                                          Width="16" Height="16"/>''',
                   '''<PathIcon Data="{icons:AppIcon GitHub}" Width="16" Height="16"/>''', 1, 'github icon')
    s = textblock_field_labels(s)
    return s

def recording(s):
    s = sub_assert(s, '''<Border Grid.Row="1" Grid.Column="0"
                       Background="{DynamicResource SurfaceColor}"
                       CornerRadius="8"
                       Padding="12,8"
                       ClipToBounds="True"
                       VerticalAlignment="Stretch">''',
                   '''<Border Grid.Row="1" Grid.Column="0"
                       Classes="mini-card"
                       ClipToBounds="True"
                       VerticalAlignment="Stretch">''', 1, 'stat status tile')
    s = sub_assert(s, '''<Border Grid.Row="1" Grid.Column="1"
                       Background="{DynamicResource SurfaceColor}"
                       CornerRadius="8"
                       Padding="8,8"
                       Margin="8,0,0,0"
                       ClipToBounds="True"
                       VerticalAlignment="Stretch">''',
                   '''<Border Grid.Row="1" Grid.Column="1"
                       Classes="mini-card"
                       Margin="8,0,0,0"
                       ClipToBounds="True"
                       VerticalAlignment="Stretch">''', 1, 'stat events tile')
    s = sub_assert(s, '''<Border Grid.Row="1" Grid.Column="2"
                       Background="{DynamicResource SurfaceColor}"
                       CornerRadius="8"
                       Padding="12,8"
                       Margin="8,0,0,0"
                       VerticalAlignment="Stretch">''',
                   '''<Border Grid.Row="1" Grid.Column="2"
                       Classes="mini-card"
                       Margin="8,0,0,0"
                       VerticalAlignment="Stretch">''', 1, 'stat state tile')
    s = sub_assert(s, '<Border Background="{DynamicResource SurfaceColor}" CornerRadius="12" Padding="16">',
                   '<Border Classes="page-card">', 1, 'capture card')
    s = sub_assert(s, '''<TextBlock Text="{localization:Loc Recording_CaptureSettings}" 
                              FontWeight="SemiBold" 
                              Foreground="{DynamicResource TextSecondaryBrush}" 
                              FontSize="12"/>''',
                   '<TextBlock Classes="card-title" Text="{localization:Loc Recording_CaptureSettings}"/>', 1, 'capture title')

    # Nested option rows: opening Border tags with BackgroundBrush + r6 + padding 12,8
    counter = {'n': 0}
    def conv_border(tag):
        if 'BackgroundBrush' in tag and 'CornerRadius="6"' in tag and 'Padding="12,8"' in tag and 'Classes=' not in tag:
            out = re.sub(r'\s*\n\s*Background="\{DynamicResource BackgroundBrush\}"', '', tag)
            out = re.sub(r'\s*\n\s*CornerRadius="6"', '', out)
            out = re.sub(r'\s*\n\s*Padding="12,8"', '', out)
            out = out.replace('<Border', '<Border Classes="nested-panel"', 1)
            counter['n'] += 1
            return out
        return tag
    s = transform_tags(s, 'Border', conv_border)
    assert counter['n'] == 5, f'recording nested rows: {counter["n"]}'

    # Help badges (2x): opening Border tag
    counter2 = {'n': 0}
    def conv_badge(tag):
        if 'CornerRadius="10"' in tag and 'Width="20" Height="20"' in tag and 'Classes=' not in tag:
            out = re.sub(r'\s*\n\s*Background="\{DynamicResource SurfaceHoverBrush\}"', '', tag)
            out = re.sub(r'\s*\n\s*CornerRadius="10"', '', out)
            out = re.sub(r'\s*\n\s*VerticalAlignment="Center"', '', out)
            out = out.replace('<Border', '<Border Classes="help-badge"', 1)
            counter2['n'] += 1
            return out
        return tag
    s = transform_tags(s, 'Border', conv_badge)
    assert counter2['n'] == 2, f'help badges: {counter2["n"]}'
    # Badge "?" glyph: styling from Layout.axaml
    s = resub_assert(s, r'<TextBlock Text="\?"\s*\n\s*HorizontalAlignment="Center"\s*\n\s*VerticalAlignment="Center"\s*\n\s*FontSize="12"\s*\n\s*FontWeight="Bold"\s*\n\s*Foreground="\{DynamicResource TextSecondaryBrush\}"/>',
                     '<TextBlock Text="?"/>', 2, 'help badge text')
    # 10px descriptions -> field-hint
    s = resub_assert(s, r'FontSize="10"\s*\n\s*Foreground="\{DynamicResource TextSecondaryBrush\}"/>',
                     'Classes="field-hint"/>', 3, 'rec hints')
    s = sub_assert(s, '''<StackPanel Orientation="Horizontal" HorizontalAlignment="Center" Spacing="4">
                <PathIcon Data="{icons:AppIcon Tip}" Width="14" Height="14" Opacity="0.9"/>
                <TextBlock Text="{localization:Loc Recording_Tip}" Classes="caption"/>
            </StackPanel>''',
                   '''<StackPanel Classes="footer-tip">
                <PathIcon Data="{icons:AppIcon Tip}" Classes="caption-icon"/>
                <TextBlock Text="{localization:Loc Recording_Tip}" Classes="caption"/>
            </StackPanel>''', 1, 'rec footer tip')
    return s

def playback(s):
    s = sub_assert(s, '''<!-- Speed Control -->
                <StackPanel Spacing="8">''',
                   '''<!-- Speed Control -->
                <Border Classes="page-card">
                    <StackPanel Spacing="12">''', 1, 'speed card open')
    s = sub_assert(s, '''                </StackPanel>

                <!-- Motion fidelity -->
                <StackPanel Spacing="10">
                    <TextBlock Text="{localization:Loc Playback_MotionMode}"
                               FontWeight="SemiBold"
                               Foreground="{DynamicResource TextPrimaryBrush}"/>
''',
                   '''                    </StackPanel>
                </Border>

                <!-- Motion fidelity -->
                <Border Classes="page-card">
                    <StackPanel Spacing="12">
                        <TextBlock Classes="card-title" Text="{localization:Loc Playback_MotionMode}"/>
''', 1, 'motion card open')
    s = sub_assert(s, '''                </StackPanel>

                <!-- Loop Controls -->
                <StackPanel Spacing="12">''',
                   '''                    </StackPanel>
                </Border>

                <!-- Loop Controls -->
                <Border Classes="page-card">
                    <StackPanel Spacing="12">''', 1, 'loop card open')
    s = sub_assert(s, '''                </StackPanel>

                <!-- Countdown -->
                <StackPanel Spacing="8">
                    <TextBlock Classes="label" Text="{localization:Loc Playback_CountdownSec}"/>''',
                   '''                    </StackPanel>
                </Border>

                <!-- Countdown -->
                <Border Classes="page-card">
                    <StackPanel Spacing="8">
                        <TextBlock Classes="field-label" Text="{localization:Loc Playback_CountdownSec}"/>''', 1, 'countdown card open')
    s = sub_assert(s, '''                </StackPanel>

                <!-- Play Controls -->''',
                   '''                    </StackPanel>
                </Border>

                <!-- Play Controls -->''', 1, 'countdown card close')
    s = sub_assert(s, '''<TextBlock Grid.Column="1" Text="{Binding PlaybackSpeed, StringFormat='{}{0:0.0}x'}"
                                  FontWeight="Medium"
                                  FontSize="16"
                                  Foreground="{DynamicResource PrimaryBrush}"/>''',
                   '''<TextBlock Grid.Column="1" Classes="value-text" Text="{Binding PlaybackSpeed, StringFormat='{}{0:0.0}x'}"/>''', 1, 'speed value')
    s = resub_assert(s, r'<TextBlock Classes="label"', '<TextBlock Classes="field-label"', 6, 'playback field labels')
    s = sub_assert(s, '''<StackPanel Orientation="Horizontal" HorizontalAlignment="Center" Spacing="4">
                    <PathIcon Data="{icons:AppIcon Tip}" Classes="caption-icon"/>
                    <TextBlock Text="{localization:Loc Playback_LoopTip}" Classes="caption"/>
                </StackPanel>

                <!-- NEW: Bottom Spacer -->
                <Rectangle Height="40" Fill="Transparent"/>''',
                   '''<StackPanel Classes="footer-tip">
                    <PathIcon Data="{icons:AppIcon Tip}" Classes="caption-icon"/>
                    <TextBlock Text="{localization:Loc Playback_LoopTip}" Classes="caption"/>
                </StackPanel>''', 1, 'playback footer tip')
    return s

FUNCS = {'settings': settings, 'recording': recording, 'playback': playback}

def main():
    names = sys.argv[1:] or list(FUNCS)
    for name in names:
        path = f'src/CrossMacro.UI/Views/Tabs/{name.capitalize().replace("settings", "Settings").replace("recording", "Recording").replace("playback", "Playback")}'
        path = f'src/CrossMacro.UI/Views/Tabs/{ {"settings": "Settings", "recording": "Recording", "playback": "Playback"}[name] }TabView.axaml'
        s = open(path, encoding='utf-8').read()
        s = FUNCS[name](s)
        open(path, 'w', encoding='utf-8', newline='').write(s)
        print(f'{name}: ok')

if __name__ == '__main__':
    main()
