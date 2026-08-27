# Changelog

## 2026-08-27

### On-Graph Filter Editing
- PEQ and crossover bands of the selected channel now appear as draggable handles directly on the response graph (numbered circles for PEQ, diamonds for crossovers), in both the main graph and the pop-out
- Drag a handle to set frequency and gain (frequency only for cuts/notch/all-pass and crossovers); the curve and the device track the drag live
- Shift+drag for fine adjustment, Ctrl+drag to lock to the dominant axis
- Mouse wheel over a handle adjusts Q (Shift = fine); on a crossover handle it steps the slope through the family's orders
- Alt+click toggles band bypass; bypassed bands render as hollow handles
- Hovering or selecting a band shows its isolated response curve with a soft fill, plus a readout chip (frequency · gain · Q)
- Double-click empty graph space to add a Peaking band at the pointer; double-click or right-click a handle for a compact editor flyout (type/family, values, bypass, remove)
- Delete removes the selected band, Esc deselects
- Selecting a crossover handle automatically flips the band list to the XO page (and vice versa)
- Toggleable via Settings → Graphing → Style → "Edit filters on the graph" (on by default)

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
