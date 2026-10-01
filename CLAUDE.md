# DSPiConsole-Windows — Working Agreement

## Git
- **Never commit without being explicitly asked to do so.** Make and build changes,
  but leave them uncommitted until the user explicitly requests a commit. "Proceed",
  "continue", or approving a change is NOT a request to commit.

## Testing
- **Don't test simple things in the running app yourself** (colours, appearance,
  simple functions): build, then hand it to the user to check. The user is often
  using the app and the DSPi at the same time, and driving it with UI automation
  interferes with that and can change the device's settings. Only drive the app
  yourself when the user says "This is an autonomous session".
- Unit tests and the build don't need the app; run those as usual.

## Build
- Build with `dotnet build -p:Platform=x64` from the repo root. Do NOT use the
  `AnyCPU` default (fails: WindowsAppSDK needs an explicit RID) and do NOT target
  ARM64 — this project is x86_64 only.
- A full Release build fails to copy output DLLs while the app is running (file
  lock, not a compile error). To verify a compile without closing the app, build a
  single project (e.g. `dotnet build DSPiConsole.Usb/DSPiConsole.Usb.csproj -p:Platform=x64`).
