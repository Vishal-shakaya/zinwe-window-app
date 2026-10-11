namespace Zinwe.Agent.Core.Features.DataTransfer;

/// <summary>
/// Publishes the owner's email-attachment size limit to where the classic-Outlook SendGuard add-in reads it
/// (a machine registry value), so that add-in blocks oversized sends at the dashboard's limit with no reinstall.
/// The service implements it (writes HKLM when LocalSystem + enforcement on; logs otherwise); tests use the no-op.
/// </summary>
public interface IEmailLimitPublisher
{
    /// <summary>Make the add-in's limit match <paramref name="maxAttachmentBytes"/> (0 = the add-in won't block).</summary>
    void Publish(long maxAttachmentBytes);
}

/// <summary>Default when nothing can write the machine registry (tests, dev as a plain user): does nothing.</summary>
public sealed class NullEmailLimitPublisher : IEmailLimitPublisher
{
    public void Publish(long maxAttachmentBytes)
    {
    }
}
