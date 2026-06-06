# Copilot Instructions

## Required Workflow for All Code Changes
**MANDATORY: Follow this workflow for every implementation request. Never skip these steps.**

### 1. Investigation Phase (Always Do First)
- **Search and analyze** existing code related to the request using `code_search`, `find_symbol`, and `get_file`
- **Understand** the current architecture, data flow, and how related features work
- **Identify** all affected files, methods, and components
- **Report findings** to the user with a summary of what you discovered

### 2. Planning Phase (Explain Before Coding)
- **Describe** your implementation approach in detail
- **List** specific files and methods you'll modify
- **Explain WHY** each change is needed and how it fits the architecture
- **Highlight** any risks, dependencies, or architectural concerns
- **Present the plan** to the user and wait for confirmation before proceeding

### 3. Implementation Phase (Code with Verification)
- Make the planned changes using file tools
- Run `get_errors` or `run_build` to check for compilation errors
- **Review** the changes for logical correctness
- **Verify** the implementation matches the plan

### 4. Completion Phase (Only When Truly Ready)
- Confirm zero compilation errors in affected files
- Summarize all changes made
- **Only then** declare the work ready for testing
- Never say "it's done" without completing verification

**Example of proper workflow:**
```
User: "Add feature X"

AI: "Let me investigate how [related feature] currently works..."
    [searches code]
    "I found that [explanation]. To implement your request, I plan to:
    1. Modify [FileA.cs] method [MethodB] to [reason]
    2. Add [new setting C] because [reason]
    3. Update [UI control D] to [reason]

    This will work because [architectural explanation].
    Should I proceed with this plan?"

User: "Yes" or provides feedback

AI: [makes changes]
    [verifies build]
    "Implementation complete. Changed:
    - [FileA]: [what changed]
    - [FileB]: [what changed]
    No compilation errors. Ready for testing."
```

## Project Guidelines
- When the user asks to commit changes in this repository, do not bump or modify the version number unless they explicitly ask for a version change.
- Never run git commit without the user explicitly asking to commit. Always wait for the user to say "commit" before committing.

## UI Guidelines
- Always simulate the net UI/layout effect (pixel-level if needed) before applying changes, so proposed edits produce a measurable movement on first try and avoid repeated user retesting.
- Before applying UI layout changes, compute expected net pixel effect first and avoid offsetting one change with an equal opposite change.
- For the HolyLogger Spot dialog, My Callsign should be read-only; Spotted Callsign must stay editable.
- The Frequency label should be short.
- The Send button should be fully visible.
- Do not change the Spot button size or location unless explicitly requested.
- When adjusting HolyLogger cluster window layout, change only the explicitly requested element(s) and avoid moving unrelated controls.
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

## General Guidelines
- **Never use the terminal in this workspace.** The terminal frequently times out and wastes time. Always use file tools (`get_file`, `replace_string_in_file`, `code_search`, `find_symbol`, `get_files_in_project`, etc.) instead. Git operations are the only allowed exception and must be kept minimal.
- Avoid using the terminal in the HolySuite workspace when possible because it frequently times out; prefer direct file/code tools instead.
- In this workspace, avoid using terminal commands because they time out quickly; use file/code tools instead.