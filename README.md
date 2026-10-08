# Zinwe Windows agent

The program that runs on every employee PC. It enrolls the PC into the business owner's Zinwe account, checks in with the backend, and will host the protection modules (USB, installs, remote access, network…). The owner never uses this UI: they use the web dashboard. Employees see only a tray icon and a status card.

**Stack:** C# / .NET 10 (LTS) · Windows Service + WPF tray · WiX v6 MSI · self-contained x64 (no .NET install needed on the PC).

```
window-app/
├─ src/Zinwe.Agent.Core/      shared: backend client + contracts, device identity (DPAPI), pipe protocol
├─ src/Zinwe.Agent.Service/   Windows service "ZinweAgent" (LocalSystem): enroll → heartbeat loop, pipe server
├─ src/Zinwe.Agent.Tray/      per-user tray icon + status card (talks only to the local service)
├─ tests/Zinwe.Agent.Core.Tests/
├─ installer/                 WiX MSI (service, tray autostart, registry, locked ProgramData folder)
├─ build.ps1                  test → publish → MSI
└─ docs/architecture.md       why it's shaped like this
```

## Prerequisites

- .NET SDK 10 (`dotnet --list-sdks`). Installed per-user at `%LOCALAPPDATA%\Microsoft\dotnet` on the dev box.
- The Django backend running locally (`backend/zinwe-backend`, `manage.py serve` on 127.0.0.1:8000, or `.cloude/start-local-env.sh --backend-only`).
- No separate WiX install needed: the WiX SDK comes from NuGet.

## Run it locally (no install, no admin)

1. Get an enrollment key for a local owner account:

   ```powershell
   cd ..\backend\zinwe-backend
   .venv\Scripts\python.exe -c "import os,django; os.environ.setdefault('DJANGO_SETTINGS_MODULE','config.settings'); django.setup(); from z_auth.models import User; from z_devices.services import get_active_key; u,_=User.objects.get_or_create(email='agent-dev@zinwe.local'); print(get_active_key(u).key)"
   ```

2. Run the service as a console app (dev settings → `http://127.0.0.1:8000`, state in `%LOCALAPPDATA%\Zinwe\Agent-dev`):

   ```powershell
   $env:DOTNET_ENVIRONMENT = "Development"
   $env:ZINWE_Agent__EnrollmentKey = "ZNW-XXXX-XXXX-XXXX"
   dotnet run --project src\Zinwe.Agent.Service
   ```

3. Run the tray (`--show` opens the status card straight away):

   ```powershell
   dotnet run --project src\Zinwe.Agent.Tray -- --show
   ```

To see the "removed" path, revoke the device in Django admin (**Devices → Revoke**). The next heartbeat gets a 401 and the agent wipes its identity and shows "Not connected".

## Test & build

```powershell
dotnet test Zinwe.Agent.slnx     # unit tests
.\build.ps1                      # tests + publish + artifacts\installer\ZinweAgent.msi
.\build.ps1 -Version 0.2.0
```

Warnings are errors (`Directory.Build.props`).

## Install (test VM first; needs admin)

```powershell
msiexec /i artifacts\installer\ZinweAgent.msi ENROLLMENT_KEY=ZNW-XXXX-XXXX-XXXX /qn /l*v install.log
# optional: BACKEND_URL=https://staging.example   (production default is in appsettings.json)
sc query ZinweAgent
msiexec /x artifacts\installer\ZinweAgent.msi /qn
```

## Configuration

| Setting | Where | Notes |
|---|---|---|
| `Agent:BackendBaseUrl` | `appsettings.json` → registry `BackendBaseUrl` → env `ZINWE_Agent__BackendBaseUrl` | Must be `https`, except loopback |
| Enrollment key | registry `HKLM\SOFTWARE\Zinwe\Agent\EnrollmentKey` (MSI), or `Agent:EnrollmentKey` (dev) | Deleted by the service after use |
| `Agent:HeartbeatSeconds` | config | Initial cadence only; the backend's `next_heartbeat_seconds` wins |
| `Agent:DataDirectory` | config | Default `%ProgramData%\Zinwe\Agent` (`device.dat`, DPAPI-encrypted) |

## Backend contract (`backend/zinwe-backend/z_devices`)

| Call | Auth | Result |
|---|---|---|
| `POST /api/devices/enroll/` `{enrollment_key, machine_id, hostname, os_version, agent_version, platform}` | none (throttled 60/h/IP) | 201 new / 200 re-enrolled → `{device_id, device_token, re_enrolled}`; 400 `enrollment_key` = refused |
| `POST /api/devices/heartbeat/` | `Authorization: Device <token>` | `{device_id, server_time, next_heartbeat_seconds, policy_version}`; 401 = removed |
| `GET /api/devices/`, `GET /api/devices/enrollment-key/`, `POST …/rotate/` | owner `Token` | dashboard |

## Code signing (launch blocker)

The MSI and both exes are **unsigned**. Before any customer install you need an OV code-signing certificate (or Azure Trusted Signing, if eligible in India) and a Microsoft Defender false-positive submission. An unsigned "monitoring agent" will be flagged by SmartScreen/Defender.
