# Copilot Instructions

## Project Guidelines
- When the user asks to commit changes in this repository, do not bump or modify the version number unless they explicitly ask for a version change.

## UI Guidelines
- For the HolyLogger Spot dialog, My Callsign should be read-only; Spotted Callsign must stay editable.
- The Frequency label should be short.
- The Send button should be fully visible.
- Do not change the Spot button size or location unless explicitly requested.
- For the Spot feature, send directly to a DX cluster Telnet server; on success show a simple success message, and if the cluster does not confirm, keep the dialog open with a closable error/message.
- When adjusting the Spot button arcs, keep left and right arcs independently controllable; do not move the right arc unless explicitly asked, and keep vertical symmetry by slightly lifting the arcs when needed.

## Stability Review Findings
### High Risk
- Optimize `GetWorkedCountriesFromLog()` to use the `clusterWorkedCountries` cache instead of creating a new `EntityResolver` on every cluster spot call.
- Refactor `IsNeededCountry()` to use a single shared/static `EntityResolver` instead of creating a new one per call.
- Change `StartUDPClient` from `async void` to properly handle exceptions by ensuring `await GetQrzForCall()` is within a try/catch block inside `Dispatcher.Invoke`.
- Implement a mechanism to trim `clusterSpotKeys` to prevent memory leaks, as it currently grows indefinitely while `clusterAllSpots` is capped at 1500.

### Medium Risk
- Ensure `UdpClient` (Client) and `N1MMClient` are disposed of on window close by calling `Close()` on them.
- Unsubscribe from `NetworkChange.NetworkAvailabilityChanged` event in the closing handler to prevent memory leaks.
- Stop and dispose of `NewDXCCTimer` in the closing handler to avoid resource leaks.
- Replace `lock(this)` in `StartUDPClient` with a private object `_udpLock` to follow best practices.

### Low Risk
- Add error handling for `Properties.Settings.Default.Save()` calls to prevent unhandled exceptions.
- Change inline creation of `Regex` objects in `StartN1MMUDPClient` to use static readonly compiled regexes for performance improvement.