# HolyLogger Work Log

## 2026-05-19

### Current state
- `HolyLogger` and `vfoKnob` are separate projects.
- No binding from `HolySuite` to `vfoKnob` was found.
- Radio CAT voice-message command profiles currently live inside `HolyLogger`, not in the Python project.

### HolyLogger radio models currently present
Defined in `HolyLogger/MainWindow.xaml.cs` in the `VoiceCommandProfiles` table:
- IC-7300
- IC-7300MK2
- IC-7610
- K3
- FTDX10
- FTDX101D
- FTDX3000
- FT-891

### Notes for next session
- If asked about added radio CAT commands, inspect `HolyLogger/MainWindow.xaml.cs` first.
- Do not assume anything from `vfoKnob` applies to `HolyLogger`.
- If radio model lists drift between projects, compare manually instead of sharing code across them.
