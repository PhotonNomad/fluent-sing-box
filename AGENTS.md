# Windows development and testing

When a command is not found, distinguish Codex sandbox execution from the user's normal PowerShell before recommending installation. Check command resolution and PATH, then the verified installed executable path. Distinguish missing paths from access-denied execution. If allowed, compare a narrowly scoped command outside the sandbox and tell the user which environment produced each result. A machine-scope install does not by itself prove sandbox access. Prefer scoped approved execution over reinstalling or weakening sandbox boundaries.

Use isolated FLUENTSINGBOX_DATA_DIR fixtures for UI tests. Do not change the host's existing proxy or TUN configuration to run tests. Prefer UI Automation patterns without focus/click requests; keep visible windows only when needed. Stop only test processes created by the runner. Configuration checks and local proxy fixtures do not prove real external-node or TUN routing.
