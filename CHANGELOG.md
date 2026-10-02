# Changelog

## 2026-10-03

### Menu
- The main menu gathers Loudness Compensation, Crossfeed, Psychoacoustic Bass, Subharmonic Synthesizer, Tube Modeller, Stereo Upmixer and Volume Leveller into an Effects submenu; their shortcuts are unchanged

### Channel pages
- In the band list, gain now comes before Q, as on the macOS Console
- The filter type menu groups the shelves, cuts and all-pass into submenus by slope (6 or 12 dB/oct; all-pass 180 or 360 degrees)
- Enable All and Bypass All dim when they would change nothing, and are hidden on firmware without per-band bypass
- Output pages have Clear All, which resets every band on the page after asking
- Changing a crossover's family keeps the nearest slope it offers (Butterworth 3rd order to Linkwitz-Riley gives LR2)
- Input preamp moves in 0.1 dB steps, can be typed, and right-click resets it to 0 dB; right-click on an output's gain or delay resets it to 0
- "Clear PEQ" (or "Clear 1/2 PEQ" on a linked pair) clears an input's bands without asking
- On an output page, clicking an input's name connects or disconnects it, its level can be set before connecting (right-click for 0 dB), and an active INV is orange
- A muted output's icon is red
- The Linkwitz Transform editor shows the DC boost in orange past +15 dB, whether the values are applied, and has Revert
- Ctrl+C and Ctrl+V copy and paste a channel page's parameters; pasting onto a linked input updates its partner too, and bands past the copied ones are cleared
- Linking an input pair also compares (and copies) the preamp
- Input pair links are remembered per device

### Main window and Settings polish
- Dashboard filter rows use the macOS Console's codes: HC and LC for the cuts, with a 1 for the first-order (6 dB/oct) variant, and show Q only for peaking bands
- Hovering the connection dot says whether the device is connected, the last connection error, or how to retry
- Alt-click a channel in the sidebar to rename it
- The sidebar's Settings gear closes Settings when it is open, and lights while it is; Ctrl+, opens Settings
- About lists links (YouTube, GitHub, Discord, Patreon, Ko-fi) with a few words about the project
- Settings > Advanced > Debug resets every channel name to its factory default

### Getting started and release notes
- A Getting Started wizard on first launch takes a new user from a blank Pico to verified DSPi firmware: Welcome, Board (find the board in bootloader mode, or update a connected one, and install the firmware this Console carries) and Done. It replaces the console until it is finished or skipped, and Help > Getting Started runs it again. Existing users are never shown it
- What's New shows the release notes once after an update, and from Help whenever asked
- The menu has a Help submenu: Getting Started, What's New and links to the Console and firmware on GitHub

### Dashboard layout
- The dashboard lays its cards out in rows, using the width of a wide window: Auto fits as many cards per row as the window allows, or choose 1, 2 or 3 from the gear that appears in a card's corner on hover; the choice is remembered

### Control Surfaces
- Auxiliary outputs: a page of their own under Control for GPIOs the DSPi switches or dims for something it knows nothing about (an amplifier trigger, a speaker relay, a panel lamp, a fan). Each output has a live switch (and, dimmable, a level) that acts at once without touching flash, its pin and sense, a level limit and linear response for dimmable ones, turn-on and turn-off delays, its power-on state (fixed, or as last saved), and a list of the controls, remote keys and macros that drive it
- Controls, remote keys, macro steps and display pages can drive an aux output through Aux Switch and Aux Level, picking the output by name
- The function menu is grouped into families (Volume & Mute, Loudness, Crossfeed, and so on), with short names inside each
- A card's type badge is a menu that changes the component type
- Turn-on and turn-off delays are entered in minutes and seconds, with a note when they are set
- Card summaries read as sentences ("Press to toggle Mute.", "Lights to indicate Mute. Delayed 2 s on, 10 min off."), and component types use the macOS Console's names
- A group card says how many controls use it

### Firmware update
- File > Update Firmware installs the firmware that ships with this Console: click Update Firmware and the device restarts into its bootloader, the firmware for its chip is written, and the window waits for the device to come back and confirms the version it reports; a board already in bootloader mode (BOOTSEL held while plugging in) is found on its own
- The window shows this Console's version beside the connected device's, warns when the update would be a downgrade, offers Export Configuration first, and steps through Prepare, Write, Verify and Done, with Try Again and Update Another Board
- It refuses to choose between two boards in bootloader mode, and refuses to install firmware that does not match this Console
- "Enter bootloader mode without installing" remains, for flashing a build of your own
- The firmware mismatch banner's Update and Details buttons open the window

### Upmixer, matrix mixer, signal generator, statistics and interrupt monitor
- Upmixer: a STATUS line (Active, Idle with its reason, or No device) with live centre and surround gauges shown only while it runs; the Mac's labels and sections, and a stage that is Off hides its settings
- Matrix mixer: gain and INV show only on connected crosspoints, an active INV is orange; outputs that PDM would take over carry orange rings, and the PDM prompt names the outputs and says Enable PDM or Disable PDM; crosspoints of disabled outputs are dimmed but still clickable; delays stop at the device's limit; an output header's right-click menu has Identify, Rename, Copy Parameters and Paste Parameters; with more than two inputs, each input row has a trim, and the routing bar has Direct 1:1 and Clear; the table scrolls when it outgrows the screen
- Signal generator: signals as picture tiles; output chips cycle on, inverted and off, with All and None; outputs disabled in the matrix are dimmed and the one playing during Walk is outlined; level -80 to 0 dB; the SMPTE and CCIF two-tone presets and the named intersample-peak patterns; a warning before RAW output; a Stop now button and the Space bar; a status pill reading Fading in, Running, Gap, Fading out or Idle; choosing a signal resets its settings, edits apply to a running signal after a short pause, None stops it, and it keeps playing when the window closes
- Statistics: the reconnect count; overruns in orange and underruns in red; Streaming and PDM Active indicators; a 15 s trace under each buffer with its watermark band and thresholds, sampled every 60 ms; S/PDIF DMA starvation with per-pair counts and the time since the last event; S/PDIF input, LG Sound Sync, ADAT output and I2S input sections when the device has them
- The Bulk Endpoint Monitor is now the Interrupt Monitor: every notification the firmware sends is named, with the field and value of every parameter change; a Listening, Paused or Inactive state and an event count; Pause (Ctrl+P) and Clear (Ctrl+K); it keeps the last 2000 lines and stays smooth during bursts

### Loudness, crossfeed, volume leveller and psychoacoustic bass
- The four windows follow the macOS Console's layout: a header with the switch, and (apart from the leveller) two columns, the graph beside the parameters; every value is a slider with a typed field that sends as you drag and records once on release
- Loudness: the curve is the compensation at -40 dB of volume (it was drawn 40 dB too low, at a different level), on a 20 Hz-20 kHz axis fitted to the curve, reading Disabled when off; the mask preset is "Slot 1 only (Headphones)"
- Crossfeed: the response is the firmware's actual filter; choosing Default, Chu Moy or Jan Meier no longer overwrites your Custom cutoff and feed, and editing either while a built-in preset runs switches to Custom from the values shown; legend, Disabled state and preset descriptions; "Pair 1 only (Headphones)"
- Volume leveller: the channel chips follow the active input count and name the right channels; the Lookahead note reads 5 ms; the mask presets use the Mac's names and order
- Every slider in the tool windows: a drag released where it began leaves the device on that value, not on the last value sent mid-drag; a value field that still has focus no longer undoes a change made elsewhere (a preset load) when you click away; the graphs follow a drag without snapping back; switches, masks and presets reach the device in the order they were made
- Psychoacoustic bass: a SPECTRUM diagram of the original band and the harmonics from fc to 4fc; the Apply preset values match the macOS Console's; Warm / Aggressive ends on Character

### Spectrum analyser
- The response graph shows a live spectrum behind its curves, in each channel's own colour, on its own dBFS scale: the bass bands where the transform is coarse, the finer FFT detail above them when one channel is selected, with a peak-hold contour
- The graph's gear has a SPECTRUM section: an Inputs | Outputs switch, the channels as chips, Clear, and FFT Graph and RTA Bars switches. The dashboard remembers its channels; a channel page starts on its own channel
- A Spectrum Analyser window (Ctrl+Shift+A) mirrors the open page's spectrum as curves, bars or both, with channels hidden in the window only, and a status line with the refresh rate, frames per second and the analyser's main-loop and bass load
- The analyser runs only while something shows it, and stops on the device when nothing does
- RTA Bars: a strip of third-octave bars under the response graph, one cell per channel in 1 to 4 columns, its height dragged from the bottom edge; its gear chooses the columns and opens the analyser window
- Settings › Graphing › Spectrum Analyser: spectrum strength, peak hold, smoothing, the floor and ceiling, and the device's transform size, averaging and peak decay, which the Console remembers and sends whenever the analyser starts

### Graphing
- Inputs beyond the active source's channel count (inputs 3 to 8 on a stereo source) no longer draw on the response graph, where they showed as one multicoloured line with no pill to hide it
- The graph options open centred below the gear, clear of the graph where the screen allows, on a translucent background; the gear stays shown while they are open
- The response graph's background is a neutral grey

### Subharmonic synthesizer and tube modeller
- A Subharmonic Synthesizer window (Ctrl+Shift+S): the band graph with the synthesized subs, the 70 Hz boost and the ceiling, the band levels, the headroom cost, selectivity, sub ceiling, LF boost, per-output selection with sub meters, pair linking, solo (cleared when the window closes) and four starting points
- A Tube Modeller window (Ctrl+Shift+D): Basic mode shows the selected tube, glowing while on and brightening with the signal, with a shelf of sixteen tubes, drive and mix; Advanced mode shows the transfer curve with its second and third harmonics, the tube picker, trim, the character controls, the rectifier and the output stage; five starting points; the mode is remembered
- Both windows follow a drag on their graphs as it happens

### Output limiter
- Each output's page has a limiter icon under the mute button: grey when off, the accent colour when on, orange while it is reducing gain; a click switches the limiter, a right-click opens its settings
- The settings hold the threshold, release and link group, with Copy to all outputs and an All outputs menu (link every stereo pair, unlink all, switch every limiter off); linked outputs share their settings, as the firmware gangs them

### Sliders
- Dragging the output gain, output delay and input preamp sliders sends values to the device as the slider moves, at most 30 times a second, and records the change once on release; device writes now always arrive in the order they were made
- Typed output gain applies on Enter or on leaving the field, not per keystroke

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
