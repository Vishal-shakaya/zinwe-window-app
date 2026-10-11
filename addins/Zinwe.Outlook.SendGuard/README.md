# Zinwe SendGuard — classic Outlook send blocker

A COM add-in for **classic desktop Outlook** that **cancels an outgoing email whose attachments exceed the
company's size limit** and tells the user why. This is the only way to truly *block* a send: Outlook raises
`Application.ItemSend` in-process with a `Cancel` flag, and only an in-process add-in can set it.

> **Scope:** classic desktop Outlook only. New Outlook and webmail (Gmail, Outlook on the web) have **no**
> equivalent hook — those are covered by the agent's **upload monitoring** (`net.upload`), which flags large
> uploads by domain but can't block them.

## Why this is a separate project

It targets **.NET Framework 4.8**, not the agent's .NET 10. Office hosts add-ins in-process and does not support
loading a .NET 10 / .NET Core add-in (and VSTO is .NET Framework only). So it is **deliberately excluded** from
`Zinwe.Agent.slnx` and must be built on its own. It cannot be compiled or validated from the agent's .NET 10
toolchain — build it on a Windows box as below.

## Build

Requires: Windows, the **.NET Framework 4.8 Developer Pack**, and MSBuild (Visual Studio or Build Tools).

```powershell
dotnet build Zinwe.Outlook.SendGuard.csproj -c Release
# or: msbuild Zinwe.Outlook.SendGuard.csproj /p:Configuration=Release
```

Output: `bin\Release\net48\Zinwe.Outlook.SendGuard.dll`.

## Register (per machine, run elevated)

The add-in registers itself for COM and writes the Outlook add-in key (`HKCU\...\Outlook\Addins\Zinwe.SendGuard`,
`LoadBehavior=3`) via its `[ComRegisterFunction]`:

```powershell
# 64-bit Outlook → 64-bit regasm; 32-bit Outlook → the Framework64\... path's 32-bit regasm equivalent.
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\regasm.exe /codebase "bin\Release\net48\Zinwe.Outlook.SendGuard.dll"
```

`regasm` must match Outlook's bitness. For real deployment, package the DLL + registration into the Zinwe MSI
instead of calling regasm by hand, and sign the DLL so Outlook trusts it.

Unregister:

```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\regasm.exe /unregister "bin\Release\net48\Zinwe.Outlook.SendGuard.dll"
```

## The size limit (ties into the dashboard policy)

The add-in reads the limit, in **bytes**, from the registry (first match wins, `0` disables the block):

1. `HKLM\SOFTWARE\Zinwe\SendGuard\MaxAttachmentBytes` (QWORD/DWORD) — written by the **Zinwe agent** so it tracks
   the owner's dashboard "email" data-transfer limit.
2. `HKCU\SOFTWARE\Zinwe\SendGuard\MaxAttachmentBytes`
3. Default **20 MB** if neither is set.

**Agent integration (done):** the Zinwe service mirrors `policy.data_transfer.email_warn_bytes` into
`HKLM\SOFTWARE\Zinwe\SendGuard\MaxAttachmentBytes` on each policy apply (see `RegistryEmailLimitPublisher` in the
agent — real only as LocalSystem with enforcement on, dry-run otherwise). So changing the **email** limit on the
dashboard updates this add-in automatically, no reinstall. The add-in's 20 MB default only applies if the agent
isn't running / hasn't written the value yet.

## Validate (on a box with classic Outlook)

1. Build + register as above; start classic Outlook.
2. File ▸ Options ▸ Add-ins → confirm **Zinwe SendGuard** is a *Active Application Add-in*.
3. Set a small test limit: `reg add HKCU\SOFTWARE\Zinwe\SendGuard /v MaxAttachmentBytes /t REG_QWORD /d 1048576`
   (1 MB).
4. Compose a mail, attach a file > 1 MB, press Send → expect a **"Zinwe — email not sent"** dialog and the mail
   stays in Drafts/Compose (not sent). A small attachment sends normally.
5. `reg delete HKCU\SOFTWARE\Zinwe\SendGuard /f` to clear the test limit.

## Safety

Every handler is wrapped so a fault **never cancels or corrupts** the user's mail — on any error the add-in does
nothing and the send proceeds. It reads only attachment **sizes**, never message content.
