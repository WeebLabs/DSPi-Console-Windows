# DSPi Console GUI Specifications

This document outlines all design choices and provides a comprehensive reference for UI styling parameters.

---

## Design Choices Summary

### Window & Backdrop
- **Backdrop Type**: `DesktopAcrylicBackdrop` (frosted glass effect showing desktop)
- **Translucency when unfocused**: Always visible (`IsInputActive = true`)
- **Title bar**: Extended into content area (`ExtendsContentIntoTitleBar = true`)

### Color Philosophy
- **Text colors**: Softer than WinUI defaults to reduce harshness
- **Primary text**: #E0E0E0 (soft white, not pure white)
- **Channel colors**: Distinct colors per channel for visual identification
- **Backgrounds**: Transparent sidebar (shows acrylic), solid main content area

### Layout
- **Sidebar**: Translucent with acrylic effect, fixed width with min/max constraints
- **Main content**: Solid background for readability
- **Channel badges**: Modern pill-shaped design with glowing indicator dot

---

## File Locations Quick Reference

| File | Purpose |
|------|---------|
| `DSPiConsole/App.xaml` | Global theme colors and brushes |
| `DSPiConsole/MainWindow.xaml` | Main UI layout and XAML-defined elements |
| `DSPiConsole/MainWindow.xaml.cs` | Programmatically created UI elements |
| `DSPiConsole/Controls/CpuMeter.cs` | CPU meter control styling |
| `DSPiConsole/Controls/HorizontalMeterBar.cs` | Audio level meter styling |
| `DSPiConsole.Core/Models/Channel.cs` | Channel color definitions |

---

## Global Theme Colors

**File**: `DSPiConsole/App.xaml`

### Text Colors (Lines 14-24)

| Resource Key | Color | Usage |
|--------------|-------|-------|
| `TextFillColorPrimary` | #E0E0E0 | Default text, CPU % values |
| `TextFillColorSecondary` | #CCCCCC | Channel names, preamp value, section labels |
| `TextFillColorTertiary` | #888888 | Filter value units (Hz, dB, Q) |
| `TextFillColorGhostly` | #666666 | "INPUTS" and "OUTPUTS" headers |
| `TextFillColorDisabled` | #4D4D4D | Disabled elements |

### Channel Colors (Lines 27-37)

| Resource Key | Color | Channel |
|--------------|-------|---------|
| `MasterLeftColor` | #FF4A8FE3 | Master L (Blue) |
| `MasterRightColor` | #FFF57373 | Master R (Red) |
| `OutLeftColor` | #FF45C2A3 | Out L (Teal) |
| `OutRightColor` | #FFF0C459 | Out R (Yellow) |
| `SubColor` | #FFBA87F3 | Sub (Purple) |

**Note**: These are also defined in `DSPiConsole.Core/Models/Channel.cs` lines 42-60 for programmatic access.

---

## Acrylic Backdrop Settings

**File**: `DSPiConsole/MainWindow.xaml.cs`
**Method**: `SetupAcrylicBackdrop()` (Lines ~106-132)

| Property | Value | Effect |
|----------|-------|--------|
| `TintColor` | RGB(32, 32, 32) | Dark gray tint over blur |
| `TintOpacity` | 0.5f | Lower = more translucent |
| `LuminosityOpacity` | 0.8f | Lower = more see-through |
| `IsInputActive` | true | Keep effect when window unfocused |

---

## Layout & Sizing

### Sidebar

**File**: `DSPiConsole/MainWindow.xaml`

| Property | Line | Value | Description |
|----------|------|-------|-------------|
| Width | 34 | 280 | Default sidebar width (pixels) |
| MinWidth | 34 | 200 | Minimum width |
| MaxWidth | 34 | 300 | Maximum width |
| Background | 38 | Transparent | Allows acrylic to show through |

### Sidebar Section Padding

| Section | Line | Padding Value |
|---------|------|---------------|
| Channels section | 48 | `Padding="12"` |
| Global section | 73 | `Padding="12"` |
| System Status section | 97 | `Padding="12"` |
| Connection status | 174 | `Padding="12,8"` |

### Main Content Area

| Property | Line | Value |
|----------|------|-------|
| Padding | 195 | 20 |
| Background | 195 | `SolidBackgroundFillColorBaseBrush` |
| Bode Plot Height | 198 | 250 |

### Title Bar

| Property | Line | Value |
|----------|------|-------|
| Height | 17 | 32 |
| Title margin | 20 | `16,0,0,0` |

---

## Sidebar Elements

### "INPUTS" / "OUTPUTS" Headers

**File**: `DSPiConsole/MainWindow.xaml` (Lines 49-50, 60-61)

| Property | Value |
|----------|-------|
| FontSize | 11 |
| FontWeight | SemiBold |
| Foreground | `TextFillColorGhostlyBrush` (#666666) |
| Margin | `0,8,0,4` (INPUTS), `0,16,0,4` (OUTPUTS) |

### Channel List Items

**File**: `DSPiConsole/MainWindow.xaml.cs`
**Method**: `CreateChannelListItem()` (Lines ~158-240)

#### Channel Name Text (Lines 172-177)
| Property | Value |
|----------|-------|
| Foreground | `TextFillColorSecondaryBrush` |
| VerticalAlignment | Center |

#### Channel Badge (Pill) (Lines 182-190)
| Property | Value |
|----------|-------|
| Background alpha | 40 |
| Border alpha | 80 |
| BorderThickness | 1 |
| CornerRadius | 10 |
| Padding | `8, 3, 10, 3` |

#### Badge Indicator Dot (Lines 199-224)
| Element | Size | Alpha |
|---------|------|-------|
| Outer glow | 8x8 | 100 |
| Inner core | 5x5 | 255 (full) |

#### Badge Text (Lines 228-236)
| Property | Value |
|----------|-------|
| FontSize | 9 |
| FontWeight | SemiBold |
| Foreground alpha | 230 |
| CharacterSpacing | 40 |

### ListView Item Padding

**File**: `DSPiConsole/MainWindow.xaml` (Lines 52-56, 63-67)

| Property | Value |
|----------|-------|
| Padding | `22,0,12,0` |
| ListView Margin | `-12,0,-12,0` (extends to edges) |

### Preamp Section

**File**: `DSPiConsole/MainWindow.xaml` (Lines 77-87)

| Element | Property | Value |
|---------|----------|-------|
| "Preamp" label | Foreground | `TextFillColorSecondaryBrush` |
| Preamp value | Foreground | `TextFillColorSecondaryBrush` |
| Preamp value | FontFamily | Cascadia Code, Consolas |
| Slider | Min/Max | -60 to 10 |

### Section Labels (GLOBAL, SYSTEM STATUS, etc.)

**File**: `DSPiConsole/MainWindow.xaml`

| Element | Line | Foreground |
|---------|------|------------|
| "GLOBAL" | 74-75 | `TextFillColorSecondaryBrush` |
| "SYSTEM STATUS" | 98-99 | `TextFillColorSecondaryBrush` |
| "USB IN" | 112-113 | `TextFillColorSecondaryBrush` |
| "SPDIF OUT" | 135-136 | `TextFillColorSecondaryBrush` |
| "PDM OUT" | 158-159 | `TextFillColorSecondaryBrush` |

### Meter Channel Labels (L, R, S)

**File**: `DSPiConsole/MainWindow.xaml` (Lines 123-168)

| Property | Value |
|----------|-------|
| FontSize | 9 |
| FontFamily | Cascadia Code |
| Foreground | `TextFillColorSecondaryBrush` |

---

## Custom Controls

### CpuMeter

**File**: `DSPiConsole/Controls/CpuMeter.cs`

| Element | Line | Property | Value |
|---------|------|----------|-------|
| Label (C0:, C1:) | 47-53 | FontSize | 10 |
| | | Foreground | `Colors.Gray` |
| Meter bar | 55-60 | Width | 40 |
| | | Height | 6 |
| Meter background | 62-66 | Background | ARGB(76, 128, 128, 128) |
| Meter foreground | 68-74 | Background | DodgerBlue (normal), Red (>90%) |
| Value text | 79-86 | FontSize | 10 |
| | | FontFamily | Cascadia Code, Consolas |
| | | Width | 28 |
| | | Foreground | Default (Primary) |

### HorizontalMeterBar

**File**: `DSPiConsole/Controls/HorizontalMeterBar.cs`

| Element | Line | Property | Value |
|---------|------|----------|-------|
| Control | 40 | Height | 8 |
| Background | 44-48 | Background | ARGB(76, 0, 0, 0) |
| | | CornerRadius | 2 |
| Foreground | 50-55 | CornerRadius | 2 |

Meter colors are set per-instance in MainWindow.xaml (Lines 126, 130, 149, 153, 168).

---

## Main Content Area

### "Filter Response" Title

**File**: `DSPiConsole/MainWindow.xaml` (Lines 204-205)

| Property | Value |
|----------|-------|
| FontSize | 14 |
| FontWeight | Medium |
| Foreground | `TextFillColorSecondaryBrush` |

### Bode Plot Container

**File**: `DSPiConsole/MainWindow.xaml` (Lines 246-250)

| Property | Value |
|----------|-------|
| CornerRadius | 8 |
| Background | `CardBackgroundFillColorDefaultBrush` |
| BorderBrush | `CardStrokeColorDefaultBrush` |
| BorderThickness | 1 |

### Legend Panel

**File**: `DSPiConsole/MainWindow.xaml` (Lines 252-253)

| Property | Value |
|----------|-------|
| Spacing | 8 |
| Margin | `0,12,0,16` |

---

## Dashboard Cards

**File**: `DSPiConsole/MainWindow.xaml.cs`

### Stereo Dashboard Card (Lines 350-395)

| Property | Line | Value |
|----------|------|-------|
| Background | 354 | ARGB(153, 45, 45, 48) |
| CornerRadius | 355 | 8 |
| BorderBrush | 356 | ARGB(51, 128, 128, 128) |
| BorderThickness | 357 | 1 |

### Channel Header (Lines 397-437)

| Property | Line | Value |
|----------|------|-------|
| Background alpha | 401 | 25 (tinted with channel color) |
| Padding | 402 | 8 |
| Indicator dot size | 410-411 | 6x6 |
| Channel name FontSize | 418 | 11 |
| Delay text Foreground | 430 | `Colors.Gray` |

### Filter Row (Lines 450+ in CreateDashboardFilterRow)

| Property | Value |
|----------|-------|
| Row height | 24 |
| Row padding | `8, 0, 8, 0` |
| Alternating row background | ARGB(8, 255, 255, 255) |
| Band number Foreground | ARGB(178, 128, 128, 128) |
| Filter values Foreground | `TextFillColorSecondaryBrush` |
| Unit labels Foreground | `TextFillColorTertiaryBrush` |

---

## Connection Status Bar

**File**: `DSPiConsole/MainWindow.xaml` (Lines 174-192)

| Element | Property | Value |
|---------|----------|-------|
| Container padding | 174 | `12,8` |
| Indicator dot | 182 | 6x6 ellipse |
| Status text Foreground | 184 | `TextFillColorSecondaryBrush` |
| Reconnect button | 187-189 | Transparent, no border |

Connection indicator colors are set in code (`UpdateConnectionStatus` method):
- Connected: `Colors.LimeGreen`
- Disconnected: `Colors.Red`

---

## Tips for Making Adjustments

### To change all text brightness at once:
Edit `TextFillColorPrimary` in `App.xaml` line 14.

### To change sidebar translucency:
Edit `TintOpacity` and `LuminosityOpacity` in `MainWindow.xaml.cs` lines 126-127.

### To change sidebar width:
Edit the first `ColumnDefinition` in `MainWindow.xaml` line 34.

### To change channel colors:
Edit both `App.xaml` (lines 27-31) AND `Channel.cs` (lines 42-60) to keep them in sync.

### To change badge/pill appearance:
Edit `CreateChannelListItem()` in `MainWindow.xaml.cs` starting around line 182.

### To change section header styling:
Most use `TextFillColorSecondaryBrush` - change the brush definition in `App.xaml` or edit individual elements.

---

## On-Graph Filter Editing (Controls/GraphEditing)

A port of the macOS Console's editor. Behaviour lives in `DSPiConsole.Core/GraphEditing/PeqGraphEditor.cs` (unit-tested in `DSPiConsole.Core.Tests`); `PeqGraphEditorView` hosts it over the plot rect of `BodePlotControl` and draws with Win2D (`PeqGraphRenderer`).

- **Band colours**: `PeqBandPalette`, ten soft hues (coral, orange, amber, sage, teal, sky, periwinkle, lavender, rose, clay), shared with the band list's bypass dot and number
- **Dots**: flat discs, radius 5, 6.5 on hover, +0.5 selected; a selected dot grows a 2.2 px centre in the graph background colour (#262629)
- **Fills**: every band's lobe at 0.22 opacity (0.42 hovered or selected), fading to nothing at 0 dB column by column; outline 1.25 px at 0.9 when hovered or selected, dissolving within 10 px of 0 dB
- **Band list rows**: a band hovered on the graph tints its row 18 % toward the band colour with a 2 px left bar at 55 % opacity; a selected band tints 24 % with the bar at full colour
- **Level offset**: with "Gain affects displayed level" on, the curve includes the channel's output gain or input preamp, and the dots, fills, fill baseline and hit areas are drawn on axes shifted by the same amount; chip and list values stay the band's own, and the pointer readout shows the axis level
- **Rendering**: all the fills are one GPU pass, a Direct2D pixel shader (`Controls/GraphEditing/Shaders/PeqLobes.hlsl`; the compiled `PeqLobes.bin` is embedded, and the .hlsl header gives the fxc command to rebuild it). Each band's response row is recomputed only when that band changes, and the view, chip, readouts and band list follow the editor once per displayed frame, however fast pointer input arrives
- **Bypassed band**: colour mixed 65 % toward grey, dot at 0.55 opacity, fill 0.06, outline 0.35
- **Edited channel's curve**: drawn by the editor (the plot skips it), line width and glow from Graphing settings; glow is the curve at 2.5x width, alpha 0.7, Gaussian blur 3.5, composited at 0.85
- **Chip and card**: 110 px wide, #EB1C1C20 with a 1 px #1AFFFFFF edge, 8 px radius; header 17 px at y 4, rule at y 24, value rows 15 px from y 28; values Cascadia Code 11, labels 9.5 at 45 % white
- **Readouts**: 16 px labels, #EB17171C, 4 px radius, Cascadia Code 10 semibold, at the bottom edge (frequency) and left edge (gain)
- **Graph options**: the gear at the graph's top right (`GraphOptionsPanel`), 280 px, a main page and a Graph Setup page

## Subharmonic Synthesizer (SubharmonicSynthWindow.cs)

- 900 x 660 window, header (accent icon, title 14 semibold, subtitle 10, SOLO pill #FF9F0A when on, enable switch), two columns of 16 px padding with a 1 px divider
- **Band graph** (`Controls/SubharmBandGraph.cs`, Win2D): 16-250 Hz log by -42..+18 dB, 38 px annotation strip and 13 px axis strip; source bands white 10 % (4 % when their band is off) with brackets; sub blocks accent, #FF9F0A and #BF5AF2 at 40 % with 90 % outlines; the 70 Hz bell #30D158 at 80 %, 1.4 px; the ceiling #FF453A dashed across 16-80 Hz; 188 px high in an 8 px rounded panel (`ToolWindowChrome.PanelColor`, #99161618, shared with the tube panels)
- Section labels 10 bold secondary; band rows show "Off" at -30 dB; sub meters 3 px under each output chip, polled at 10 Hz while the window is open and the effect is on

## Tube Modeller (TubeModellerWindow.cs)

- 900 x 700 window; header tube icon (`Controls/TubeIcon.cs`, 27 px) whose filament glows #FF9F0A while on (0.9 s in, 0.6 s out), Basic | Advanced picker
- **Transfer curve** (`Controls/TubeTransferGraph.cs`): input -1..1 by output -1.4..1.4, clipped inputs shaded #FF9F0A at 10 %, dashed linear reference, full-scale lines #FF453A dashed, curve accent 1.6 px
- **Basic showcase** (`Controls/TubeIllustration.cs`): 168 x 280 tube from the macOS Console's 120 x 200 family geometry, three Win2D layers (tube, heater, bloom); bloom opacity = sqrt(peak) of the processed outputs; a warm radial wash behind the glass while on

## Output Limiter (MainWindow.Limiter.cs, Controls/OutputLimiterSettings.cs)

- **Icon** (`Controls/LimiterIcon.cs`): 19 px gauge glyph under the output card's mute button, 1.5 px round strokes and a filled hub; secondary text colour when off, `SystemAccentColor` when on, #FF9F0A while gain reduction is 0.05 dB or more; opacity 0.5 while disconnected. Right-click opens `OutputLimiterSettings` (320 px, padding 16) in a flyout to the left
- **Settings rows** use `ParameterRow` (`Controls/ParameterRow.cs`): title 12 medium, subtitle 9, a 60 px value field with its unit, end labels 9; a drag sends live values through `SliderDrag` and commits once on release
