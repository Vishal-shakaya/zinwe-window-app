# Windows agent architecture

## Shape

```
 Employee session (per user)            Session 0 (LocalSystem)                    Cloud
┌──────────────────────────┐   pipe   ┌──────────────────────────────┐  HTTPS   ┌──────────────────┐
│ Zinwe.Agent.Tray (WPF)   │◄────────►│ Zinwe.Agent.Service          │◄────────►│ Django z_devices │
│ tray icon, status card,  │ "Zinwe.  │  AgentWorker: enroll→beat    │ Device   │ enroll/heartbeat │
│ (later) consent + alerts │  Agent"  │  PipeServer                  │ token    │                  │
└──────────────────────────┘          │  (later) enforcement modules │          └──────────────────┘
                                      └──────────────────────────────┘
```

**Why two processes.** Windows services run in Session 0 and cannot show UI, read the foreground window, or raise toasts. All enforcement (it needs SYSTEM rights) lives in the service. Everything the employee sees lives in the tray, and the tray holds no secrets and makes no network calls.

**Why no kernel driver.** USB, install, network and remote-access control are all reachable through policy (registry/GPO, WDAC/AppLocker, Windows Filtering Platform in user mode). A driver would need an EV certificate plus Microsoft attestation signing, which means months of lead time and a crash risk on customer PCs.

## Enrollment & identity

1. The owner copies their enrollment key (`ZNW-XXXX-XXXX-XXXX`, reusable, rotatable) from the dashboard.
2. The admin installs `msiexec … ENROLLMENT_KEY=…`. The MSI writes it to `HKLM\SOFTWARE\Zinwe\Agent`.
3. The service posts `machine_id` (Windows `MachineGuid`) and the key, and gets a **device token**. The backend stores only its SHA-256.
4. The token is saved DPAPI-encrypted (`device.dat`, LocalSystem scope) in a ProgramData folder locked to SYSTEM and Administrators. The registry key is deleted.
5. A reinstall on the same PC re-uses the same `Device` row (unique on owner + machine_id) and rotates the token.

The device token works **only** on device endpoints (`DeviceTokenAuthentication` is not in DRF's default auth classes). A token stolen from a PC can't read the owner's account.

## State machine (`AgentWorker`)

| State | Meaning | Leaves when |
|---|---|---|
| NotEnrolled | no identity, no usable key | a key appears (checked every 30s) |
| Enrolling | POST /enroll in flight | 2xx → Connected; 400 on key → Rejected; network → NotEnrolled |
| Connected | last heartbeat ok | network error → Offline; 401 → Rejected |
| Offline | backend unreachable; exponential backoff ≤ 5 min | next success |
| Rejected | key refused or device removed; identity wiped | an admin supplies a *new* key |

Rule: **losing the backend never turns protection off.** Modules will run on the last cached policy.

Rule: **a used or refused key is never reused.** On successful enroll, on refusal, and whenever the agent starts already enrolled, any key lying around is consumed: deleted from the registry and remembered in memory. Without this, a PC the owner removed would silently re-join on its next 401. A live test caught exactly that bug.

## Gotchas already hit

- `JsonContent` streams with chunked encoding (no `Content-Length`). Django's WSGI server reads that as an **empty body**. `BackendClient` buffers bodies; a test pins it.
- The host's content root defaults to the working directory, and the SCM starts services in `System32`. `Program.cs` pins it to `AppContext.BaseDirectory`.
- WinForms (for `NotifyIcon`) and WPF both define `Application`, `MessageBox`, etc. The tray csproj removes the WinForms/Drawing implicit usings and aliases them.

## Not done yet (in rough order)

1. Policy fetch + local policy cache (`policy_version` is already in the heartbeat).
2. First enforcement module: USB storage block/read-only + event upload.
3. Event queue in SQLite for offline upload.
4. Tray: verify the pipe server's process is the real service (anti pipe-squatting), consent screen, alert toasts.
5. Self-update (signed MSI download), tamper protection (service ACL), uninstall password.
6. Dashboard UI for devices + enrollment key (backend API exists).
7. Code signing + Defender submission (launch blocker).
