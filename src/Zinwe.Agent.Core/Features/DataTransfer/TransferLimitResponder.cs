namespace Zinwe.Agent.Core.Features.DataTransfer;

/// <summary>
/// Called when a data transfer crosses an admin limit that should trigger an ENFORCEMENT response (not just an
/// alert). The service's implementation locks USB to read-only and notifies the member; Core/tests use the no-op.
/// Must never throw.
/// </summary>
public interface ITransferLimitResponder
{
    /// <summary>A copy to <paramref name="destination"/> totalled <paramref name="totalBytes"/>, over <paramref name="limitBytes"/>.</summary>
    void OnUsbLimitExceeded(string destination, long totalBytes, long limitBytes, DateTimeOffset at);
}

/// <summary>Default when lockdown isn't wired (tests, or a build without the service responder): does nothing.</summary>
public sealed class NullTransferLimitResponder : ITransferLimitResponder
{
    public void OnUsbLimitExceeded(string destination, long totalBytes, long limitBytes, DateTimeOffset at)
    {
    }
}
