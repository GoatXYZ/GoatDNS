# AGENTS.md

GoatDNS is a personal-use Windows encrypted DNS client built with .NET 10,
WinUI 3, a Windows Service, and WinDivert. The UI communicates with the service
over a named pipe; the service owns DNS interception, policy, and upstream
resolution.

## Non-Negotiable Rules

- Do not install or uninstall the service, alter machine DNS settings, load the
  WinDivert driver, or capture live traffic unless Goat explicitly requests it.
- Treat resolver configuration, query logs, packet captures, credentials, and
  exported runtime data as sensitive. Never print, expose, or commit them.
- Do not create or switch branches or worktrees, commit, push, or open or merge
  pull requests unless explicitly requested.
- Preserve fail-open behavior: if interception is unavailable or the service
  stops, normal Windows DNS must continue working.
- Only an explicit instruction from Goat can override these rules.

## Questions Are Read-Only

A question is a request for an answer, not a change. Inspect the repository and
answer it without editing files. Even when the change is trivial, offer it and
wait for approval.

## Before Coding

- Resolve discoverable facts from the repository before asking questions.
- Surface material ambiguity instead of choosing silently.
- Trace the real UI-to-IPC-to-service-to-engine path before editing.
- Reuse existing code and platform features before adding abstractions or
  dependencies.
- Make the smallest complete change and preserve unrelated working-tree edits.

## Core Priorities

1. Prevent plaintext DNS leaks while interception is active, including failure,
   retry, cancellation, and shutdown paths.
2. Keep service state, configuration writes, and IPC operations safe across
   concurrent clients and restarts.
3. Keep packet handling bounded and responsive; network work must not block the
   capture loop or WinUI thread.
4. Maintain the privilege boundary: the app runs unelevated and privileged
   operations remain in the service.
5. Preserve protocol correctness for UDP/TCP DNS, DoH, DoT, DoQ, DNSCrypt,
   DNSSEC policy, IPv4, and IPv6 wherever the affected path applies.

## Architecture Map

- `GoatDNS.Core/` owns DNS codecs, upstream protocols, rules, hosts, pools,
  configuration, the proxy engine, and IPC contracts.
- `GoatDNS.Service/` owns the Windows Service lifecycle, privileged runtime,
  named-pipe server, and configuration reload.
- `GoatDNS.WinDivert/` owns native WinDivert interop and packet capture/injection.
- `GoatDNS.App/` owns the WinUI 3 interface, tray behavior, and service client.
- `GoatDNS.Tests/` contains the xUnit v3 test executable.
- `scripts/` owns dependency installation, builds, publishing, setup, removal,
  and WinDivert acquisition.
- `README.md`, `BUILDING.md`, and `PLAN.md` describe the product, supported build
  path, and remaining scope.

## Check Every Affected Surface

- **Engine or protocols:** Check cancellation, timeouts, malformed packets,
  retries, concurrent queries, DNSSEC policy, and plaintext-leak behavior.
- **Capture:** Check packet direction, endpoint swapping, checksums, recursion
  avoidance, IPv4/IPv6 behavior, driver absence, and fail-open shutdown.
- **IPC or service:** Check pipe authorization, framing, cancellation,
  disconnects, config consistency, service restart, and app/service version
  mismatch.
- **WinUI:** Check UI-thread affinity, async error reporting, startup without the
  service, tray lifecycle, keyboard access, and high-contrast/readability.
- **Configuration:** Preserve backward compatibility unless a migration is
  explicitly requested; write atomically and validate at the trust boundary.
- **Documentation:** Update `README.md` or `BUILDING.md` when public behavior,
  requirements, installation, or operator steps change.

## Runtime and Repository Safety

- Do not download WinDivert binaries or other runtime dependencies unless the
  task requires it.
- Do not use active `%ProgramData%\GoatDNS` state for mutating checks; use
  disposable configuration and ports.
- Never kill processes by broad name or path match. Stop only a positively
  identified process.
- Treat DNS replies, hosts files, imported stamps, pipe messages, logs, and
  packet data as untrusted input.
- Do not edit generated `bin/`, `obj/`, `publish/`, or downloaded runtime files.

## Verification

Use the smallest applicable proof and report exactly what ran.

- Inspect the scoped diff and working-tree status.
- Run `git diff --check`.
- Build only the affected project when that proves the change.
- Run the existing xUnit v3 executable for affected Core logic when practical.
- Use `scripts/build.ps1` only when the full Windows solution needs validation.
- Do not claim Windows, WinUI, service, driver, or live interception behavior was
  verified when the current environment could not exercise it.

## Git and Instruction Files

- Work on the current branch and stage only files in scope.
- A request to commit authorizes a local commit only; pushing requires an
  explicit push request.
- Use Conventional Commit messages without agent attribution.
- Keep `AGENTS.md` authoritative. `CLAUDE.md` must remain the minimal
  `@AGENTS.md` wrapper.
