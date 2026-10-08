using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Zinwe.Agent.Core.Policy;

namespace Zinwe.Agent.Core.Usb;

/// <summary>Turns a <see cref="UsbMode"/> into the PC's actual setting.</summary>
public interface IUsbEnforcer
{
    /// <summary>True when nothing on the PC is changed (test mode); events carry this as "dry_run".</summary>
    bool IsDryRun { get; }

    /// <summary>Idempotent. Throws on registry/permission failures; <see cref="UsbProtection"/> catches and logs.</summary>
    void Apply(UsbMode mode);
}

/// <summary>Writes DWORDs / deletes values under an HKLM subkey. Abstracted so tests never touch the real registry.</summary>
public interface IRegistryWriter
{
    void SetDword(string keyPath, string valueName, int value);

    /// <summary>No-op when the value (or key) does not exist.</summary>
    void DeleteValue(string keyPath, string valueName);
}

/// <summary>One registry edit: set <see cref="ValueName"/> to <see cref="Dword"/>, or delete it when that is null.</summary>
public sealed record RegistryChange(string ValueName, int? Dword)
{
    public override string ToString() => Dword is { } v ? $"set {ValueName}={v}" : $"delete {ValueName}";
}

/// <summary>
/// Windows' documented "Removable Storage Access" policy (the same values Group Policy writes for
/// "Removable Disks: Deny read/write access"). No driver needed; Windows enforces it in the storage stack.
/// </summary>
public static class RemovableStoragePolicy
{
    /// <summary>Under HKLM. The GUID is the Removable Disks device class used by the Group Policy template.</summary>
    public const string KeyPath =
        @"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices\{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}";

    public const string DenyRead = "Deny_Read";
    public const string DenyWrite = "Deny_Write";

    /// <summary>
    /// Ordered so a transition never passes through a looser state than either end
    /// (e.g. block → read_only keeps Deny_Write set before Deny_Read goes).
    /// </summary>
    public static IReadOnlyList<RegistryChange> ChangesFor(UsbMode mode) => mode switch
    {
        UsbMode.Allow => [new(DenyRead, null), new(DenyWrite, null)],
        UsbMode.ReadOnly => [new(DenyWrite, 1), new(DenyRead, null)],
        UsbMode.Block => [new(DenyRead, 1), new(DenyWrite, 1)],
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown USB mode."),
    };
}

/// <summary>The real enforcer. Only ever built by <see cref="UsbEnforcerSelector"/> when running as LocalSystem.</summary>
public sealed class RegistryUsbEnforcer(IRegistryWriter registry, ILogger<RegistryUsbEnforcer> logger) : IUsbEnforcer
{
    public bool IsDryRun => false;

    public void Apply(UsbMode mode)
    {
        foreach (var change in RemovableStoragePolicy.ChangesFor(mode))
        {
            if (change.Dword is { } value)
            {
                registry.SetDword(RemovableStoragePolicy.KeyPath, change.ValueName, value);
            }
            else
            {
                registry.DeleteValue(RemovableStoragePolicy.KeyPath, change.ValueName);
            }
        }
        logger.LogInformation("USB storage set to {Mode}", mode.ToWire());
    }
}

/// <summary>Test mode: logs exactly what the registry enforcer would do and changes nothing.</summary>
public sealed class DryRunUsbEnforcer(ILogger<DryRunUsbEnforcer> logger) : IUsbEnforcer
{
    public bool IsDryRun => true;

    public void Apply(UsbMode mode)
    {
        var changes = string.Join(", ", RemovableStoragePolicy.ChangesFor(mode));
        logger.LogInformation("[dry run] USB {Mode}: would {Changes} under HKLM\\{KeyPath}",
            mode.ToWire(), changes, RemovableStoragePolicy.KeyPath);
    }
}

/// <summary>
/// SAFETY RULE: the registry enforcer is used only when the process is LocalSystem (i.e. the installed service)
/// AND Agent:Enforcement:DryRun is false. A developer's `dotnet run`, a test, or a misconfigured box gets the
/// dry-run enforcer, so nothing short of the real installed service can lock a PC's USB ports.
/// </summary>
public static class UsbEnforcerSelector
{
    public static IUsbEnforcer Select(
        bool runningAsSystem,
        bool dryRunConfigured,
        Func<IUsbEnforcer> createRegistryEnforcer,
        ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger(typeof(UsbEnforcerSelector));
        if (dryRunConfigured)
        {
            logger.LogWarning("Agent:Enforcement:DryRun is on: USB rules will be logged, not applied.");
            return new DryRunUsbEnforcer(loggers.CreateLogger<DryRunUsbEnforcer>());
        }
        if (!runningAsSystem)
        {
            logger.LogWarning("Not running as LocalSystem: USB rules will be logged, not applied (test mode).");
            return new DryRunUsbEnforcer(loggers.CreateLogger<DryRunUsbEnforcer>());
        }
        return createRegistryEnforcer();
    }

    public static bool IsRunningAsSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem;
    }
}
