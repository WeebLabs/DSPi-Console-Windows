# Changelog

## 2026-10-02

### Firmware 1.1.6 beta 4 groundwork
- Reads the subharmonic synthesizer, tube modeller and output limiter settings from the device, keeps them in step with changes made elsewhere, and tracks them for unsaved changes; their controls come in a later release
- A banner appears when the device's firmware differs from the version this Console expects, with a way to the matching firmware or Console release; it can be hidden for the session
- Preset files carry the subharmonic synthesizer, the tube modeller, each output's limiter (applied with the hardware option, as the firmware keeps it with the wiring) and the input channels' delays, and write an output's delay the way the macOS Console does, so files from the macOS Console keep them through a Windows save
- Unsaved-changes tracking now covers psychoacoustic bass, the volume leveller's main settings, the preamps of inputs 3 to 8 and the input channels' delays
- Control Surfaces names the new parameters for the subharmonic synthesizer, the tube modeller and the output limiter
- Development builds carry the app version (1.1.6-beta4)

## 2026-10-01

### On-Graph Filter Editing (macOS design)
- PEQ bands of the selected channel appear on the response graph as dots in a per-band colour, each with a soft fill showing its own contribution; hovering or selecting brightens a band and draws its outline, and a bypassed band stays as a grey ghost
- Double-click empty graph to add a bell, Ctrl-click to choose a shape (and its slope) from a card, or pull a new band out of the curve itself
- Click, Ctrl-click, Shift-click and box selection; Ctrl+A and Tab; band numbers in the list select too, and rows light up with the graph
- Drag moves a band (Q for 12 dB cuts), several selected bands move together, Shift is fine, Alt locks an axis, Ctrl from the start drags Q, Ctrl pressed mid-drag scales the selection's gains in proportion; Alt-click bypasses
- Wheel over a dot or fill changes Q, Ctrl-wheel gain; a scroll stays with its band and a selection owns the wheel
- A band chip beside the dot: drag, scroll or type values (note names such as A4 accepted), a two-step shape and slope page, and a bypass button
- Right-click menus for a band (slope, bypass, invert gain, delete) and for empty graph; Delete, Escape, arrow and Alt-arrow keys
- The device follows a drag live, and the model is written once, on release; editing pauses on the XO tab
- With "Gain affects displayed level" on, the dots and fills move with the channel's gain or preamp along with the curve, and a band placed at a point lands there
- A gear on the graph opens Graph Setup and Pop Out Graph; new Grid Opacity, Frequency Readout and Gain Readout settings; glow on and 15 Hz by default
- Identical curves draw as one line with a colour gradient; the graph can be made as short as 200 px; a button stands in for the graph while it is popped out

### Fixes
- Preset files write their pin lists as number arrays, so they open in the macOS Console (and Mac files open here); older files still read
- Output delay is limited to what the firmware's delay line holds (42 ms on RP2350, 21 ms on RP2040), and typed delays apply on Enter rather than per keystroke
- Clip indicators read the firmware's 32-bit clip field, so PDM and the eighth input can show a clip
- Bypassing all crossover bands asks first
- The 6 dB low and high cuts are offered only on firmware that has them
- Narrow, very low bells draw at their true height
- Tool-window shortcuts follow the macOS Console; the volume leveller has a menu item (Ctrl+Shift+V)
- Switching devices with unsaved Settings changes asks first
- Channel names are kept to the 31 bytes the device stores
- The device's report of a filter change the app itself made is no longer taken for another host's change; each one rebuilt the band list and the graph, which made graph drags crawl

## 2026-03-22

### Sidebar Redesign
- Replaced "GLOBAL" header text with a row of quick-access shortcut icons: Matrix Mixer, Settings, Loudness Compensation, Crossfeed, Stats for Nerbs, and Bypass Master EQ
- Icons illuminate when their feature is active (window open or feature enabled)
- Bypass icon turns red when engaged
- Hover over an icon produces a subtle brightness increase with ease-in/out animation
- Matrix Mixer and Stats icons now toggle their windows open/closed on click
- Loudness Compensation and Crossfeed: left-click toggles on/off, right-click opens settings window
- Removed the standalone "Bypass Master EQ" toggle button

### Multi-Device Support
- App now discovers and tracks all connected DSPi devices by serial number
- Device selector in the sidebar footer shows the active device name with a dropdown chevron
- When multiple devices are connected, clicking the selector opens a flyout to switch between them
- Current device indicated with a checkmark in the flyout
- Switching devices with unsaved preset changes prompts a Save/Discard/Cancel dialog
- Auto-reconnects to the previously selected device if it is unplugged and re-plugged
- Auto-selects the first device if none is currently selected

### Connection Status
- Moved connection status to the sidebar footer alongside the CPU meter
- Connection indicator dot and device name shown inline
- Right-click the connection area to trigger a reconnect
- Removed the standalone reconnect button
- "Connected" text replaced with the active device display name (e.g. "DSPi (A1B2C3D4)")

### Title Bar
- Moved the menu button to the left side of the title bar
- Changed the menu icon from a gear to a hamburger menu icon

### Preset Selector
- Removed "(empty)" suffix from unoccupied preset slots
- Preset ComboBox is now transparent by default, border appears on hover
- Preset text color matched to channel name color for consistency

### Theming
- CPU meter bar, connection indicator dot, preamp slider track, and slider thumb now use the Windows system accent color
- CPU meter still turns red when load exceeds 90%; connection dot turns red when disconnected

### Layout Polish
- CPU meter and connection status consolidated into a single compact footer row
- Increased bottom row element sizes slightly for better readability (font 11, meter bar 44x6)
- Reduced gap between shortcut icon row and system status section
- Device selector box shows a subtle rounded highlight on hover
