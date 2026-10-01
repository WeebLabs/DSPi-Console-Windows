# DSPiConsole-Windows: macOS Parity Plan

_Written 2026-09-29 from a survey of the three repositories:_
- _DSPi-Console-Mac on `release/v1.1.6` (9dbb07a, v1.1.6-beta4)_
- _DSPi-Firmware at `origin/release/v1.1.6` (557bce7)_
- _this repo on `graph-editing` (c85fef8)_

_The Mac README is a complete user guide. Treat it as the behavioural spec, and the Mac code as the tie-breaker where the two differ. For example, the README still says the tube output stage is off by default; the code says it is on._

## Status

| Phase | State |
|---|---|
| 0 Groundwork | Done, committed 44cb85e on `graph-editing` |
| 1 On-graph editing | Done, committed 44cb85e and f052be0; tried on hardware |
| 2 Protocol V32 and versioning | Done 2026-10-02, committed 869778f on `firmware-v32-catchup` |
| 3.1 ParameterRow, 3.2 Limiter | Done 2026-10-03, committed on `tool-controls`; hardware test pending |
| 3.3, 3.4, 4-8 | Not started |

Notes from doing Phases 0 and 1:
- **Delay limits:** the firmware's real limits are 42 ms (RP2350) and 21 ms (RP2040). The delay line is 2048 or 1024 samples at 48 kHz, since firmware 9ec0ca1. The macOS Console's 85 ms / 42 ms is stale too.
- **Device-switch prompt:** the unsaved-preset prompt already existed. Only the pending-Settings prompt was missing.
- **Editor structure:** the editor's behaviour is a pure class, `DSPiConsole.Core/GraphEditing/PeqGraphEditor.cs`, tested by `DSPiConsole.Core.Tests`.
- **Debug harness:** Debug builds open an editor harness with sample bands, so the editor can be tried without a device. Set `DSPI_EDITOR_HARNESS=1` before launching.
- **Echoed writes:** the firmware applies an EQ write in its main loop and reports it tagged UNKNOWN, not HOST_SET, so the app's own writes came back as foreign changes. `DspDevice` now drops echoes of its own filter writes.

Notes from doing Phase 2:
- **State without controls:** subharm, tube and limiter state, fetch, bulk seeding, notifications, presets and dirty tracking are in place (`MainViewModel.Subharm/Tube/Limiter.cs`). Phase 3 adds the windows and the channel-page limiter.
- **Limiter is IO-block state:** it follows `output_config_mode` like the pins (output_limiter_spec.md §5.1), so it sits in the snapshot's IO block, is saved by Save Output Config in independent mode, and Discard restores it through the gang-safe apply. Limiter writes go through one ordered queue.
- **Delays:** on an output, the channel delay (0x48) and the output delay (0x78) are one value in the firmware: the output-delay SET writes both and the DSP reads the channel delay. Only the inputs have a delay of their own, which Windows never read. It is now read, tracked and carried by preset files (`delayMs`, with outputs also written as `outputDelayMs` like the Mac); it still has no control.
- **Versioning:** the csproj carries `<Version>1.1.6-beta4</Version>`; `AppInfo.ExpectedFirmware` derives from it. The banner's Update action explains and links to the release until the Phase 6 installer exists.
- **Aux-output nouns** (caps v17/v18) are named but hidden from the Control Surfaces picker until Phase 7.

## How far behind Windows is

| | Windows (now) | macOS 1.1.6-beta4 | Firmware 1.1.6-beta4 |
|---|---|---|---|
| Release tag | v1.1.6-beta2 | v1.1.6-beta4 | v1.1.6-beta4 |
| Bulk wire format | **V28**, parses 5944 B | **V32**, 6136 B | V32 |
| Control-surface caps | **v13** (nouns to 56) | v19 (nouns to 73) | **v20** (limiter nouns, not on the Mac yet) |
| Platform/version reply | 4 bytes, BCD nibbles | 7 bytes, full-width minor and patch plus beta ordinal | 7 bytes |
| Subharmonic synth / Tube / Output limiter | **Missing** | Yes | Yes (V29-V32) |
| Spectrum analyser (RTA/FFT) | **Missing** | Yes (window, graph overlay, bar strip, settings) | Yes (0x08-0x0F) |
| On-graph editing | First pass, different design | Full editor (chip, shape page, multi-select, menus, keys) | n/a |
| Firmware install | Reboots to BOOTSEL, then asks the user to drag a UF2 | Bundled UF2, automatic copy and verify, update window | n/a |
| Onboarding | None | Getting Started wizard, tour, hints, What's New, Help menu | n/a |

In short, Windows is four firmware features, one wire-format step (V28 to V32) and three app subsystems (firmware installer, onboarding, spectrum analyser) behind. On top of that, every existing tool window has a list of smaller gaps.

Windows does not break against beta4 firmware. Its bulk parser accepts V32 images and ignores the tail, so the gap is missing features rather than broken sync.

### Windows is ahead here. Do not regress these.

- **Pin Overview:** clicking a GPIO jumps to the control that owns it, scrolls to it and flashes it (`Settings/PinNavigation.cs`). The Resolve-all pin conflict prompt offers free GPIOs (`PinConflictPrompt.cs`).
- **Settings:** pending dots per page, dependency-ordered apply (`PendingChangeTracker.cs`), and a Save/Discard/Cancel prompt on close. Settings opens on Overview; this is deliberate.
- **Presets:** "Save to…" (save-as), Reload, and a name prompt when saving into an empty slot. Ctrl+S saves.
- **Dashboard:** a card for every active input pair. Output headers show gain, delay and MUTED.
- **Output page:** gain and delay lock icons, and the ms/cm readout toggle.
- **AutoEQ:** apply opens a channel picker.
- **ADAT free-running warning:** the fix-it button picks a free TX pin.
- **Control Surfaces:** macro total delay, and a group live preview with "x of N" counters.
- **Bulk Endpoint Monitor:** auto-scroll, IDLE filter, raw hex view and crossover decode.
- **Windows-only preferences:** dotted inactive channels, gain affects displayed level, dB axis units, and a Save button. The About page shows platform and firmware.

---

## Decisions needed before the phases they block

| # | Decision | Recommendation | Blocks |
|---|---|---|---|
| D1 | **Modifier mapping** for graph editing. The Mac uses Cmd, Option and Control separately; Windows has only Ctrl and Alt free, since Win is reserved and Ctrl+Alt is AltGr. | Cmd → **Ctrl** and Option → **Alt**. The Mac's Control key (scale the selection's gains in proportion) becomes **Ctrl pressed or released after the drag has started**. Ctrl held at press already means a Q-drag, and the Mac ignores Control in Q mode, so the two never collide. | Phase 1 |
| D2 | **Win2D.** Rendering today is XAML `Shape`s rebuilt on every tick. The Mac uses Metal for the editor overlay and the RTA. | Add `Microsoft.Graphics.Win2D` in Phase 1 for the editor layer. It is required anyway for the RTA, which runs 60 fps with 37 bands × 9 channels. | Phases 1 and 4 |
| D3 | **Windows-only graph extras.** Crossover diamonds are editable on the graph, there is a `GraphEditingEnabled` setting, and there is a "Double-click to add a band" hint. The Mac edits PEQ only and disables graph editing on the XO tab. | Match the Mac: remove the crossover editing and the hint. Keep the setting only if you want an off switch; the Mac has none. | Phase 1 |
| D4 | **Keyboard shortcuts.** Windows uses Ctrl+Shift+C for Crossfeed (Mac X) and Ctrl+Shift+I/E for preset import/export (on the Mac, Cmd+Shift+I is the Interrupt Monitor). Ctrl+Shift+B is bound twice. | Adopt the Mac letters for tool windows, moving preset import/export to Ctrl+O and Ctrl+Shift+S or similar. Fix the B clash either way. | Phase 0 and Phase 5 |
| D5 | **Terminology.** Windows says "Preset File…" / "Save Preset"; the Mac says "Device Configuration…" / "Commit Parameters…". | Adopt the Mac names. They also match the README. | Phase 8 |

---

## Phase 0: Groundwork and correctness fixes (S, about 1-2 days)

Do this on its own branch off `master`, so the graph-editing branch stays focused.

1. **Update the firmware reference checkout.** `DSPi-Firmware` is 30 commits behind `origin/release/v1.1.6`. Fast-forward it: `git -C ../DSPi-Firmware merge --ff-only origin/release/v1.1.6`. The V29-V32 specs are then local: `subharmonic_synth_spec.md`, `tube_preamp_spec.md`, `output_limiter_spec.md`, `spectrum_analyser_spec.md`, `control_surfaces_aux_spec.md`, `firmware_versioning_spec.md`.
2. **Add a test project.** Create `DSPiConsole.Core.Tests` (xUnit, x64). The Mac's pure-model tests (`PeqGraphEditorTests`, `*WireTests`, `PresetDocumentTests`) port nearly one to one, and Phases 1-3 lean on them.
3. **Preset interop bug.** `PresetIoBlock.OutputPins`, `OutputSlotTypes`, `SpdifRxPins` and `I2sRxPins` are `byte[]` (`Models/PresetDocument.cs:216-237`). System.Text.Json writes those as **base64 strings**, while the Mac writes number arrays. As a result:
   - A Windows file loses its pins on the Mac.
   - A Mac file almost certainly fails to import on Windows.

   The fix:
   - Switch these fields to `int[]` or add a number-array converter.
   - Match the Mac shape: `spdifRxPins` has 3 entries and `spdifRxPin4` is a separate key.
   - Add a round-trip test against a Mac-written fixture.
4. **RP2040 output delay cap.** The limit is 170 ms and should be **42 ms** (RP2350 is 85). See `MainWindow.xaml.cs:1677` and `:1694`. Also clamp the typed value and stop committing on every keystroke.
5. **Clip flags are 16-bit.** `SystemStatus.ClipFlags` is a `ushort` (`DSPiConsole.Core/Models/SystemStatus.cs:28`). Make it u32, so that RP2350 PDM (bit 16) and Input 8 can show a clip.
6. **Tweeter safety.** Add the confirmation the Mac shows before XO "Bypass All".
7. **Firmware gates.** Gate the 6 dB/oct low and high cuts behind wire **≥ 28**. The Mac gates notch, all-pass and first-order types too (`DSPViewModel.swift:1943-2047`).
8. **Response magnitude maths.** Port the Mac's RBJ φ = sin²(ω/2) evaluation (`DSPMath.swift:516-526`) into `DspMath.cs:214-233`. The current complex form reads 0 dB at the peak of low, narrow bells: a 10 Hz bell with Q 20 is flat. This matters for Phase 1, because a dot sits on the curve.
9. **Shortcut clash.** Ctrl+Shift+B is bound to both AutoEQ Browse and the Bulk Monitor (`MainWindow.xaml:57`, `:102`).
10. **Unguarded device switch.** Switching device should prompt for pending Settings and unsaved preset changes, as it does on quit (`SwitchToDeviceCommand`).
11. **Channel names.** Enforce the 31-char limit in the rename box. Today the VM keeps a name the device truncated.

---

## Phase 1: On-graph editing, rebuilt to the macOS design (L-XL, on `graph-editing`)

The current commit c85fef8 is a single-selection editor with a Flyout. The Mac design is a different interaction model: a multi-select gesture state machine, a band chip, a two-step shape page, menus and keys.

Keep the parts of c85fef8 that match:
- the live-send plumbing in the VM (`SetFilterLive` / `QueueLiveSend`, mirrored to linked pairs)
- Linkwitz Transform refusal
- deselect on empty click

Replace the rest.

**Mac sources:**

| File | Contents |
|---|---|
| `PeqGraphModel.swift` | pure model |
| `PeqGraphSupport.swift` | shared selection and host protocol |
| `PeqGraphEditor.swift` | gestures |
| `PeqBandHUD.swift` | chip, shape chooser, Add Band card |
| `PeqGraphRenderer.swift` | drawing |
| `GraphView.swift` / `SpectrumAnalyserView.swift` | chrome |
| `DSPi ConsoleTests/PeqGraphEditorTests.swift` | 43 behaviour tests |

### 1.1 Pure model: `GraphEditModel.cs` (Core)

Port `PeqGraphModel.swift` one to one:
- **Geometry.**
- **Shapes and two-letter codes:** PK, LS, LC, HS, HC, NT, AP, and LT (read-only).
- **Node roles.** These decide where a dot sits:
  - bell: at its gain
  - shelf: at measured gain × scale
  - 12 dB cut: at 20·log10(Q)
  - 1st-order cut: at its corner level
  - notch and all-pass: at 0 dB
  - LT: locked
- **Limits:**
  - gain ±30 dB
  - Q 0.1-20
  - frequency 10 Hz-0.45·fs when typed or created; also clamped to the visible range when dragged, scrolled or arrowed
- **Creation rules:**
  - A double-click makes a bell with Q 1.
  - Dragging from the curve starts at 0 dB and makes a low shelf in the left 12 %, a high shelf in the right 12 %, and a bell elsewhere.
  - A new band takes the lowest Off slot. Beep when every slot is in use.
- **10-hue band palette.**
- **Value text parser:** `440`, `2k`, `2.5 kHz`, note names such as `A4` / `C#2` / `Bb3` / `C#2+13`, `+3 dB`, `q1.4`. Values are truncated to 2 decimals for display.

Port the model tests with it.

### 1.2 Shared selection state (VM) and the band list

Add `GraphBandSelection` on `MainViewModel`, mirroring the Mac's `PeqGraphSelection`:
- `Selected` (a set)
- `GraphHovered`, `ListHovered`
- `RevealRow` event
- reset on channel change

The main graph, the pop-out and the band list all bind to it, so the three views stay in sync.

Band list work (`CreateFilterEditorRow` in `MainWindow.xaml.cs`):
- palette colour on the band number and the bypass dot
- number-click selects, Ctrl-click toggles, and Shift-click takes a range in row order
- row hover lights the band on the graph
- a highlight bar follows the selection and hover
- the list scrolls to a row when the graph hovers a dot for 0.25 s or makes a selection

### 1.3 Gesture controller (`GraphEditorController`, plain class, testable)

Port the Mac state machine: Idle, Press, Background, Drag, Marquee.

**Presses**
- A modified press waits for mouse-up (a click) or 2 px of movement (a drag).
- Only the **dot** takes clicks. A fill takes the scroll wheel and hover, and behaves as empty graph for everything else.

**Selection**
- Click selects one band.
- Ctrl-click toggles a band.
- Shift-click selects a range in frequency order.
- A marquee (dashed box) selects; Shift adds to the selection.
- Ctrl+A selects every band.
- Tab / Shift+Tab step by frequency and wrap round.

**Dragging**
- Several selected bands drag together: frequency by ratio, gain by dB. Proportional gain scaling uses the D1 mapping, with a 0.25 dB threshold.
- A 12 dB cut dragged vertically changes Q.
- Shift is fine adjustment (×0.12) and can be toggled mid-drag.
- Alt locks the axis. Pressed mid-drag, it takes whichever axis dominated so far, with a 4 px pending threshold and no jump.
- Ctrl-drag from the start drags Q: ×2 per 60 px, applied to the selection, and a band outside the selection joins it.
- Alt-click toggles bypass on the whole selection.
- Double-clicking a dot opens the chip with Freq ready to type.
- The wheel works during a drag. Patch the start snapshot so a Q change made mid-drag survives later movement.

**Wheel**
- Over a dot or fill: the wheel changes Q; Ctrl-wheel changes gain; Shift is fine.
- The selection owns the wheel.
- A scroll gesture sticks to its band for 0.5 s.
- Over the dB labels (a 40 px zone) the wheel zooms the range, unless a band is under the pointer.

**Keys**
- Delete and Backspace delete the selection.
- Escape closes in layers: the card, then the shape page, then the selection.
- The arrows nudge by 1/12 octave, or 1/96 with Shift.
- Up and Down change gain by ±0.5 dB, or 0.1 dB with Shift. For shapes with no gain they change Q.
- Alt+Up and Alt+Down change Q.

**Menus** (MenuFlyout)
- Band menu:
  - title "Band N" or "N Bands"
  - Slope submenu (Order for an all-pass)
  - Bypass / Enable
  - Invert Gain
  - Delete Band(s)
- Graph menu: Select All, Deselect All, Delete Selected.
- While the Add Band card is open, a right-click only closes it.

**Commit and send**
- Send live at about 30 Hz, reusing `QueueLiveSend`.
- Commit once, on release.
- For the wheel and the keys, commit 450 ms after the last event.

**Availability**
- Editing needs a channel page, a connected device and a visible curve.
- It is disabled on the XO tab, and in the pop-out when Follow is off.
- Graph editing is PEQ only (see D3).

**Cursors**
- open hand over a dot
- closed hand while dragging
- up/down arrows during a Ctrl Q-drag

**Tests**
- Port `PeqGraphEditorTests` in this order: 175, 371-410, 611, 642, 768, 842, 953-1145.

### 1.4 Renderer (Win2D layer over `BodePlotControl`)

- The edited channel's combined curve and glow. The other channels stay on the existing path.
- Fills:
  - every audible band has a faint lobe fill at 0.22 alpha, rising to 0.42 on hover or selection
  - its extent comes from `lobeReach`
  - the outline shows only on hover or selection
- Dots:
  - flat discs, radius 5, growing to 6.5 on hover plus 0.5 when selected
  - a selected dot's centre is drawn in the graph background colour
- Bypassed bands are grey-mixed ghosts. The dot is at 0.55 opacity and the fill and line are kept.
- Master bypass dims the editing layer to 0.5.
- Emphasis eases with a 70 ms time constant. External band changes animate for 0.22 s, in log frequency.
- Also draw the ghost dot, the marquee, and the frequency and gain readout labels.
- When every band is in use, show "All N bands in use".
- Draw on demand. Run a `CompositionTarget.Rendering` loop only while an animation is unsettled. Stop rebuilding `Children` on every tick.

### 1.5 Band chip, shape page, Add Band card (UserControls on a Canvas, not Flyouts)

**Chip**
- Frosted dark card, 110 × 78.
- Placement: on the side of the dot away from 0 dB, then right, then left, clamped inside the graph. Port `panelFrame`.
- Header: a shape button (glyph plus code) and a power button (shown when the firmware has per-band bypass). No delete button.
- Rows, only those the shape uses: Freq, Gain, Width, and read-only Slope / Order / f0 / fp.
- Editing a value:
  - drag vertically, with Shift for fine steps
  - scroll
  - double-click to type
  - Return applies; Tab and Shift+Tab apply and move to the next field; Escape cancels
  - beep when the text can't be parsed
- Ctrl-scroll anywhere on the chip changes gain.

**Scope**
- Values apply only to the chip's own band.
- The shape and power buttons act on the whole selection.
- The chip is pinned to the selection. It hides 0.35 s after hover leaves.

**Shape page**
- Two steps in the same card, at the same size, and the card holds still: pick a shape, then a slope (6/12 dB, or 180°/360° for an all-pass).
- The current shape is marked in the band's colour.
- The header names the shape under the pointer. The back arrow steps back one page.
- On a shape change:
  - keep the frequency
  - set gain to 0 if the new shape has none
  - seed Q (1 for bell and notch, 0.707 otherwise)

**Add Band card**
- Opened by a Ctrl-click on empty graph. It reuses the shape chooser.
- A ghost dot marks the target point.
- It turns into the new band's chip once the band is added.

### 1.6 Graph chrome (README "The Response Graph")

- **Gear popover** on hover, with a main page and a Graph Setup page:
  - FFT Graph and RTA Bars stay hidden until Phase 4.
  - It also offers Pop Out and Follow Channel Selection in the pop-out.
- **New settings:**
  - Grid Opacity, 0-200 %, default 50 %, scaling each line's alpha
  - Frequency Readout and Gain Readout toggles
  - Scale Reset
- **Defaults:** 15 Hz start and Glow on.
- **Resize strip:** 200-350.
- **Pop-out:** when the graph is popped out, the main window shows a collapse button.
- **Identical curves:** merge any identical curves into one gradient line, not just linked pairs.

**Exit criteria:** every row of the README's "Graph Editing Quick Reference" works with the D1 mapping, and the ported tests pass.

---

## Phase 2: Firmware protocol catch-up, V28 → V32 plus versioning (M, about 1 week)

The prerequisite for Phase 3. Specs are in `DSPi-Firmware/Documentation/Features/`; the Mac reference is `Constants.swift:631-760` and `Commands.swift`.

1. **Bulk parser:**
   - subharm at 5944 (16 B in V29, 36 B in V30)
   - tube at 5980 (48 B, V31)
   - limiter at 6028 (9 × 12 B, V32)
   - `PacketSizeV32 = 6136`, plus `Has*` flags
   - notify PARAM_CHANGED offset ranges for all three sections; mind the ordering note on the open-ended psybass range
2. **Opcodes in `DspDevice`:**

   | Feature | Opcodes |
   |---|---|
   | Subharm | 0x10-0x1F, 0x2C-0x2F, 0xA9-0xAE (incl. HEADROOM and METER) |
   | Tube | 0x3E / 0x3F, param index 0-13 |
   | Limiter | 0x81 (`(out<<8)\|idx`, 0xFF = all; 0x80 meter, 0x81 status probe) |
   | Core1 mode / conflict | 0x7A / 0x7B |
   | LG Sound Sync status | 0xE8 |
   | Build info | 0x80 |
3. **Versioning:**
   - read the 7-byte `REQ_GET_PLATFORM` (fall back to the 6- and 4-byte replies; handle the early-beta rule)
   - read the Mac's expected-firmware-version rule, where the app version equals the expected firmware version
   - add a `<Version>` to the csproj so dev builds carry one
   - add the **firmware mismatch banner** (older: Update…; newer: Details…; Hide for this session)
4. **Capability gates** for the whole table in `DSPViewModel.swift:1943-2047`:
   - subharm ≥ 29, extended subharm ≥ 30, tube ≥ 31, limiter ≥ 32
   - the filter-type gates
   - upmix needs ≥ 26 and RP2350
5. **PresetSnapshot and dirty tracking.** Add subharm, tube and limiter. Also close the existing holes:
   - **psybass** (not tracked at all)
   - **leveller core params** (only the masks are tracked)
   - **input preamps 3-8**
6. **Preset document.** Add these so Mac files survive a Windows re-save:
   - the `subharm`, `tube` and `channels[].limiter` blocks
   - `channels[].outputDelayMs`, `eqChannel`, `inputIndex`, `outputIndex`
   - `meta.masterVolumeMode` and `meta.outputConfigMode`
   - `io.spdifRxPin4`

   Import the limiter with the Mac's gang-safe order: unlink, write, then relink in ascending order. Add tests from `PresetDocumentTests.swift`.
7. **Control-surface caps names:**
   - v14-v16 subharm nouns 57-67
   - v19 tube nouns 70-73
   - v20 limiter nouns: firmware has them and the Mac does not yet
   - the INVALID_AUX error text

   Aux outputs themselves come in Phase 7.

---

## Phase 3: Shared tool controls, then the three new DSP features (L-XL, about 2-3 weeks)

### 3.1 `ParameterRow` control and slider delivery (M). Do this first.

Port `CustomSlider.swift`'s `ParameterRow` / `SliderValueDelivery`:
- **Drag:** live sends are coalesced to ≤ 30 Hz with one pending value. They go straight to the device without touching the model, and model echoes are ignored while the drag runs. The model commits once, on release.
- **Value field:** commits on Return or blur (not on every keystroke), clamps and reformats, and steps with Ctrl+wheel.
- **Row:** a caption and a tooltip.
- **No device:** the row is disabled.

Today every `ValueChanged` runs an uncoalesced `Task.Run` USB write (`MainViewModel.cs:2396`, `:2437`, `:2672-2725`), so writes can land out of order.

Retrofit the channel-page gain, preamp and delay rows as the first consumers.

### 3.2 Output Limiter (M-L)

Mac `OutputLimiterView.swift`; commits a426166, 0eb4972, 7d86acd, 9624658.

- **Gauge icon** under each output's mute button: grey when off, the accent colour when on, orange while it is reducing gain.
- **Click** toggles the limiter.
- **Right-click** opens a Flyout with:
  - the switch
  - Threshold (-30..0 dB, default -1)
  - Release (10-1000 ms, default 100)
  - Link segments (Off, 1-4)
  - Copy to all outputs
  - an All outputs menu: Link all stereo pairs, Unlink all, Switch every limiter off
- Settings grey out while the limiter is off.
- **Gang rules:**
  - A write to one member writes its whole group.
  - A band that joins a group adopts the settings of the lowest-numbered member.
  - A group number above 4 falls back to unlinked.
- **Meter:** polled on the 60 ms timer, only while the icon is visible and some limiter is on.
- **Persistence** follows `output_config_mode`.

### 3.3 Subharmonic Synthesizer (L)

Mac `SubharmonicSynthView.swift`. A two-column window.

**BANDS graph**
- 16-250 Hz by -42..+18 dB.
- Source columns, the synthesized half-frequency blocks, and "÷2" arrows.
- The 70 Hz bell and the ceiling line.

**Controls**
- Three band sliders (-30 = Off, up to +12).
- SELECTIVITY (All / Percussive / Sustained, with Depth and Hold).
- SUB CEILING and LF BOOST.
- A HEADROOM COST readout.

**Presets:** the four "Apply preset" entries.

**OUTPUTS**
- The chips, each with a thin sub meter at 10 Hz. Reuse `HorizontalMeterBar`.
- Link output pairs, on by default.
- Mask presets.

**Header**
- An enable switch.
- A SOLO button. Solo is runtime-only and clears when the window closes.

**V29 firmware:** hide the V30-only controls.

### 3.4 Tube Modeller (L; XL with the artwork)

Mac `TubeModellerView.swift` and `TubeIllustration.swift`.

**Order**
1. **Advanced mode first:**
   - TRANSFER CURVE: a port of `TubeShaper`, the pure memoryless curve plus the 2nd/3rd-harmonic DFT readout.
   - STAGE, CHARACTER and OUTPUT STAGE sections.
   - Five presets. Each resets Mix to 100 % and Trim to 0 dB.
   - Picking a type copies the `TUBE_TYPE_ROWS` defaults into the knobs locally.
2. **Then Basic mode:**
   - The tube illustration for 10 families, drawn as XAML `Path`s or with Win2D, rasterised once per family and size.
   - Heater fade: 0.9 s in, 0.6 s out.
   - Bloom opacity = sqrt(peak) of the masked outputs, driven through a Composition `Visual.Opacity` so it stays off the layout path.

**Also**
- The Basic | Advanced choice is persisted.
- The header tube icon glows while the modeller is on.

**Defaults:** follow the code, not the README. Output stage on, 95 Hz resonance, damping 2, drive -12 dB, drive floor -30 dB.

Each feature also needs:
- a menu item and its shortcut (Mac S / D, per D4)
- its Phase 2 snapshot and preset-document fields
- an onboarding hint key, which Phase 7 fills in

---

## Phase 4: Spectrum analyser / RTA (XL, about 2-3 weeks)

The Mac files, their responsibilities, and the planned Windows equivalents:

| Mac file | Contents | Windows plan |
|---|---|---|
| `SpectrumAnalyser.swift` | wire structs, `RtaEngine` | port to Core/Usb |
| `GraphSpectrumOverlay.swift` | graph overlay, `RtaCurveBuilder`, bin smoothing | Win2D layer under the plot |
| `SpectrumAnalyserView.swift` | scale, bar smoother, gear, bar strip, window | WinUI and Win2D |
| `RtaRenderCache.swift` | cache of layout geometry | port |
| `RtaMetal*.swift` | Metal renderers | Win2D batches |

1. **Engine (Core/Usb).**
   - **Caps probe:** 0x0A, then the band-centre table in chunks. A STALL means unsupported, and there is no wire-version gate.
   - **Subscriptions:** each consumer subscribes with a tap, a mask and whether it wants bins. The subscriptions fold into one 12-byte `RtaConfig`, and the last release sends STOP.
   - **Poll:** runs from the existing 60 ms poll timer, so vendor traffic stays serialised.
     - Push the config with SET_CONFIG and verify it with GET_CONFIG. Give up after 3 rejections.
     - Read every band with `GET_BANDS_ALL` 0x0F (82-byte frames, 0.5 dB steps, 243 = 0 dBFS).
     - Read `GET_BINS` 0x0C only when one channel is shown. Resume a short read by offset, and re-read on a seq mismatch.
     - Read status every 0.5 s.
   - **Host processing:**
     - average the bins in the power domain
     - smooth by 1/6 octave
     - silence stale frames (`ageMs` growing)
     - hide FFT bands that have no bin
   - **Publishing:** through a volatile snapshot and a version counter. Never pass frames through per-frame `INotifyPropertyChanged`.
2. **Analyser window:**
   - Curves | Bars | Both
   - a per-window channel hide
   - a status bar: state, refresh, fps, busy load, and a larger-FFT hint
3. **Graph overlay:** a Win2D layer under the plot. Translucent channel-coloured fill, on its own dBFS floor/ceiling scale.
4. **RTA bar strip** under the graph:
   - 1-4 columns
   - drag to resize
   - a gear with Open in Window
5. **Gear popover SPECTRUM section:**
   - Inputs | Outputs switch
   - channel chips, Clear, and the summary
   - FFT Graph and RTA Bars toggles (their Phase 1 placeholders go live here)
   - a per-page selection, remembered for each side
6. **Settings › Spectrum Analyser:**
   - Strength, Peak Hold, Smoothing, Floor, Ceiling
   - Transform Size, Averaging, Peak Decay
   - these are clamped to the device's caps
7. **Bar smoothing:**
   - `RtaBarSmoother` is a one-pole filter at 60 fps.
   - Fall tau = clamp(refresh × amount, 35 ms, 400 ms).
   - Peak caps rise instantly.
   - An identity change snaps rather than slides.
   - The loop stops when the view is hidden or the window is minimised.

---

## Phase 5: Existing tool-window parity (M-L, about 1-2 weeks)

Build every item on the Phase 3.1 `ParameterRow`. The Mac moved to two-column layouts on 09-15; follow suit.

### Matrix Mixer
- Show the gain field and INV only on connected crosspoints. Active INV is orange.
- Delay cap: 42 or 85 ms.
- PDM prompt: say "Enable PDM" / "Disable PDM" and name the worker range. Add orange conflict rings.
- Header right-click menu: Identify, Rename, Copy/Paste.
- Multichannel: per-row input trim, **Direct 1:1**, and a Clear that keeps gain and polarity.
- Scroll when there are 8 inputs.
- Crosspoints of disabled outputs are dimmed but still clickable.

### Loudness
- Draw the preview at -40 dB with its badge. Today it is drawn at refSPL-20 (`LoudnessWindow.xaml.cs:220`).
- Axis: 20 Hz-20 kHz, auto-scaled.
- Show a "Disabled" state.
- Sliders step by 0.1.
- Rename the mask preset to "Slot 1 only (Headphones)".

### Crossfeed
- Cutoff and Feed are dimmed rather than disabled, and editing either switches to Custom.
- Add the legend and the Disabled state.
- Add preset descriptions.
- Rename the pair preset to "Pair 1 only (Headphones)".

### Volume Leveller
- Add a menu item and its shortcut.
- Fix the chip tooltips: they use `Channel.Inputs[i].Name`, which has only 2 entries (`VolumeLevellerWindow.xaml.cs:94`).
- The Lookahead caption should read 5 ms.
- Fix the preset names and order.
- Take the chip count from the effective live input count.

### Psychoacoustic Bass
- SPECTRUM diagram: Original band, Harmonics band from fc to 4fc, markers, legend.
- Fix the preset values. Windows' Bookshelf, Small Bluetooth, Laptop and Headphone all differ from the Mac's.
- Rename "Starting points" to "Apply preset".
- Add the Warm/Aggressive end labels.

### Upmixer
- A STATUS line: Active, Idle with its reason, or No device.
- Hide the meters while idle.
- Hide the section of a stage that is Off.
- Use the Mac's labels and section headers.

### Signal Generator
- Keep playing when the window closes.
- Choosing a signal resets the parameters.
- Chips cycle on → ø → off, with All and None as buttons.
- Dim outputs that are disabled in the matrix. Outline the chip that is playing during Walk.
- Level range -80..0 dB.
- Add the SMPTE/CCIF 2-tone presets and the named ISP patterns.
- Add the RAW tweeter warning.
- Transport:
  - an immediate-stop button and the Space bar
  - a Fading in / Fading out pill

### System Statistics (L)
- Reconnect count.
- Colour the overrun and underrun counts.
- Show the Streaming and PDM Active indicators.
- **BUFFER FILL:** a 15 s trace with a min-max band and threshold bars, polled every 60 ms.
- **S/PDIF DMA starvation:** the total, per-pair counts and the timers.
- Conditional sections: S/PDIF INPUT, LG SOUND SYNC (0xE8), ADAT OUTPUT, I2S INPUT.

### Interrupt Monitor (rename from Bulk Endpoint Monitor)
- **Decode coverage:**
  - v1 master volume
  - 0x07 siggen, 0x08 ADAT, 0x09 I2S slave, 0x0B ADAT in, 0x0C CS aux
  - the full V32 offset-name map (17 delays, 8×9 crosspoints, every section)
- **Header:** Listening / Paused / Inactive state and an event count.
- **Shortcuts:** Pause (Ctrl+P) and Clear (Ctrl+K).
- **Buffer:** keep 2000 lines and coalesce redraws to 25 Hz.
- Keep the Windows extras: auto-scroll, IDLE filter, raw hex and crossover decode.

---

## Phase 6: Firmware installer and update window (L, about 1-1.5 weeks)

Mac sources:
- `FirmwareInstaller.swift`
- `BootloaderLocator.swift`
- `FirmwareUpdateView.swift`
- `FirmwareInstallUI.swift`

Work items:

1. **Bundle the firmware.** Ship `DSPi-RP2040-v<ver>.uf2` and `DSPi-RP2350-v<ver>.uf2` in `app\Firmware\` under the launcher layout (see memory: release-folder-layout). Reject stale images.
2. **BOOTSEL detection.**
   - The USB device is VID 0x2E8A with PID 0x0003 or 0x000F, found through SetupAPI or WMI.
   - The drive is found by volume label (`RPI-RP2` / `RP2350`) through `DriveInfo`.
   - Refuse to proceed when two boards are present.
3. **Copy.** Write the UF2 in chunks. The drive vanishing counts as success.
4. **Verify.** Wait for the device to reconnect and check its version with the 7-byte reply from Phase 2.
5. **Tools › Firmware Update window:**
   - THIS CONSOLE vs CONNECTED DEVICE
   - a downgrade warning
   - an Export Configuration backup
   - Prepare / Write / Verify / Done strip
   - Update Another Board / Done / Try Again
   - the full firmware message table
   - keep "enter bootloader without installing" as a secondary action
6. **Wire it up.** The Phase 2 mismatch banner's Update… button opens this window.
7. **README.** Update the Windows README, which today tells users to drag a UF2 by hand.

---

## Phase 7: Control Surfaces aux outputs, and onboarding (L-XL, about 2 weeks)

### 7.1 Control Surfaces catch-up (L)

**Aux outputs (caps v18):**
- component types AUX_OUT = 9 and AUX_PWM = 10
- nouns 68-69
- opcodes 0x04-0x07 for live state and level
- power-on state and "Driven By"
- the notify 0x0C decode

**Other work:**
- **Function menu:** group it by family. `CsNounInfo.Group` already exists but is unused.
- **Group cards:** show the "used by N controls" count.
- **Header badge:** it should change the component type.
- **LED delays:** entered in minutes and seconds.
- **Card summaries:** full sentences.

### 7.2 Onboarding (XL, about 2,100 lines of Swift; see the Mac's `onboarding_plan.md`)

- **`OnboardingCoordinator`:**
  - a set of completed step IDs
  - gating by `introducedIn`, so existing users are seeded as complete and only see new content
- **Getting Started wizard:** Welcome, Board, Done.
  - BOOTSEL detection and Install / Update / Downgrade, using the Phase 6 installer.
  - Menus are disabled while it is shown, and it crossfades into the console.
- **Basics tour:**
  - 10 coach marks with a spotlight hole: a custom overlay plus `TeachingTip`-style panels.
  - 2 steps run inside the Matrix Mixer.
  - Some steps auto-select an input first.
- **Just-in-time hints**, shown once on first open of each feature. Copy the Mac catalogue text verbatim:
  - matrix, surfaces, interfaces, macros, aux, groups
  - ADAT, I2S input
  - upmixer, crossfeed, loudness, leveller, psybass
  - subharm, tube, limiter
  - AutoEQ, test signals, stats
- **What's New:** a bundled JSON, shown once after an update.
- **Help menu:**
  - Getting Started…
  - Replay the Basics Tour
  - What's New
  - GitHub links for Console and Firmware
- **Advanced:** an onboarding developer section.

---

## Phase 8: Main window, channel page, preset and settings polish (M, about 1 week; S items)

**Main window**
- CPU meters: show C0 and C1.
- Dashboard card layout gear: Auto / 1 / 2 / 3.
- Dashboard type codes: HC/LC with a first-order suffix, and Q shown only for PK.
- Put the connection error in a tooltip.
- Alt-click renames a channel.

**Channel pages**
- Band list: a header row, and columns in the Mac's order.
- Enable All | Bypass All: dim the half that would change nothing, and hide it without bypass firmware.
- Add Clear All with a confirmation on output pages.
- Preamp: 0.1 dB steps, typeable, and right-click resets to 0 dB.
- Output gain: right-click resets to 0 dB.
- Clear PEQ: no confirmation.
- Output routing preview: click the name; a dB preset works while unrouted; active INV is orange.
- Muted icon: red.
- Filter type menu: slope submenus and all-pass shown as 180°/360°. This reverses the deliberate cec6ea9 flattening, so confirm it first.
- Crossover family change: keep the nearest valid slope.
- LT editor: Revert, an "Applied" status, and an orange DC-boost warning above +15 dB.
- Paste onto a linked input also updates its partner.
- Ctrl+C / Ctrl+V on a channel page.
- Input Link compare: include the preamp.
- Link memory: store it per device serial.

**Presets**
- Copy to… asks about unsaved changes.
- D5 naming.
- Filter import report: list skipped types, band overflow and crossovers on old firmware. Show the preamp in the dialog.

**AutoEQ**
- Rebuild from GitHub, with a progress window.
- Show the database date and entry count.
- Show Reset only after an update.

**Settings**
- Ctrl+, opens Settings.
- A second gear click closes Settings.
- Back and Forward history.
- About links and the open-source blurb.
- Advanced › Reset Channel Names.
- External mute: allow a 0 ms release.
- Labels: "Independent" / "With Preset".
- Return saves.

---

## Suggested order and branching

| Order | Phase | Branch | Why here |
|---|---|---|---|
| 1 | Phase 0 | `parity-groundwork` off `master` | Fixes that ship on their own. Phase 1 needs the magnitude fix and the test project. |
| 2 | Phase 1 | `graph-editing` (rebase onto Phase 0) | The current priority. It doesn't depend on V32. |
| 3 | Phase 2 | `firmware-v32-catchup` | Unblocks everything else. It could run in parallel with Phase 1, since the two touch disjoint files except `MainViewModel`. |
| 4 | Phase 3 | per feature | The limiter first, since it lives on the channel page and is in presets. |
| 5 | Phase 6 | | |
| 6 | Phase 4 | | Self-contained. The Win2D groundwork already exists from Phase 1. |
| 7 | Phase 5 | | |
| 8 | Phase 7 | | |
| 9 | Phase 8 | | |

**Rough total:** 10-14 weeks of focused work. Phases 1, 3 and 4 make up about 60 % of it.

**Release checkpoints**
- After Phases 0-2: a Windows 1.1.6 with Mac-style graph editing and presets that are safe across platforms.
- After Phases 3 and 6: feature parity with firmware beta4, apart from the analyser.
- After Phases 4, 5 and 7: full parity.
