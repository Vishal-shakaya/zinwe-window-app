using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Zinwe.Agent.Core.Features.DataTransfer;

namespace Zinwe.Agent.Service.Features.DataTransfer;

/// <summary>
/// Best-effort visibility into attachments leaving via CLASSIC desktop Outlook, using late-bound COM
/// (<see cref="Type.GetTypeFromProgID(string)"/>) so no Outlook interop package is referenced. On each poll it
/// attaches ONLY if outlook.exe is already running (it never launches Outlook) and reads the Sent Items folder for
/// mail sent since the last poll, reporting attachment COUNT and total SIZE — never the files themselves.
///
/// NOT covered (documented limits): the new "Outlook for Windows" (New Outlook) and all webmail (OWA, Gmail, …),
/// which are browsers/store apps with no automation surface; and sends completed between two polls on a machine
/// where Outlook then closes before the next poll.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class OutlookAttachmentWatcher(
    DataTransferOptions options, ILogger<OutlookAttachmentWatcher> logger) : IOutlookAttachmentWatcher
{
    private const int OlFolderSentMail = 5;

    private readonly CancellationTokenSource _cts = new();
    private Thread? _thread;
    // Only mail sent after the watcher started is reported; we never surface the historical Sent Items on startup.
    private DateTime _since = DateTime.Now;

    public event EventHandler<EmailSendNotice>? AttachmentsSent;

    public void Start()
    {
        _thread = new Thread(() => Loop(_cts.Token))
        {
            IsBackground = true,
            Name = "zinwe-outlook-watch",
        };
        _thread.SetApartmentState(ApartmentState.STA); // COM automation wants an STA caller
        _thread.Start();
    }

    private void Loop(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.EmailPollSeconds));
        LogStarted();
        while (!ct.IsCancellationRequested)
        {
            if (ct.WaitHandle.WaitOne(interval))
            {
                break; // cancelled
            }
            try
            {
                if (OutlookIsRunning())
                {
                    PollOnce();
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogPollFailed(e.Message); // never let a COM hiccup kill the loop
            }
        }
    }

    private static bool OutlookIsRunning()
    {
        try
        {
            var procs = Process.GetProcessesByName("outlook");
            foreach (var p in procs)
            {
                p.Dispose();
            }
            return procs.Length > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void PollOnce()
    {
        var outlookType = Type.GetTypeFromProgID("Outlook.Application");
        if (outlookType is null)
        {
            return; // classic Outlook not installed
        }

        object? app = null, ns = null, sent = null, items = null;
        try
        {
            app = Activator.CreateInstance(outlookType);
            if (app is null)
            {
                return;
            }
            ns = Call(app, "GetNamespace", "MAPI");
            sent = Call(ns!, "GetDefaultFolder", OlFolderSentMail);
            items = Get(sent!, "Items");
            Call(items!, "Sort", "[SentOn]", true); // newest first

            var count = Convert.ToInt32(Get(items!, "Count"), CultureInfo.InvariantCulture);
            var newest = _since;
            for (var i = 1; i <= count; i++)
            {
                object? item = null;
                try
                {
                    item = Call(items!, "Item", i);
                    var sentOn = ToDate(Get(item!, "SentOn"));
                    if (sentOn is null || sentOn <= _since)
                    {
                        break; // sorted newest-first: nothing older can be new
                    }
                    if (sentOn > newest)
                    {
                        newest = sentOn.Value;
                    }
                    ReportItemAttachments(item!);
                }
                catch (Exception e) when (e is COMException or InvalidCastException or MissingMemberException)
                {
                    // non-mail item (report/meeting) or transient COM error: skip it
                }
                finally
                {
                    Release(item);
                }
            }
            _since = newest;
        }
        finally
        {
            Release(items);
            Release(sent);
            Release(ns);
            Release(app);
        }
    }

    private void ReportItemAttachments(object item)
    {
        var attachments = Get(item, "Attachments");
        if (attachments is null)
        {
            return;
        }
        try
        {
            var count = Convert.ToInt32(Get(attachments, "Count"), CultureInfo.InvariantCulture);
            if (count <= 0)
            {
                return;
            }
            long totalBytes = 0;
            for (var j = 1; j <= count; j++)
            {
                object? att = null;
                try
                {
                    att = Call(attachments, "Item", j);
                    totalBytes += Convert.ToInt64(Get(att!, "Size"), CultureInfo.InvariantCulture);
                }
                catch (Exception e) when (e is COMException or InvalidCastException or MissingMemberException or FormatException)
                {
                    // attachment size unreadable: count it, skip its bytes
                }
                finally
                {
                    Release(att);
                }
            }
            var subject = options.IncludeDetails ? Get(item, "Subject") as string : null;
            AttachmentsSent?.Invoke(this, new EmailSendNotice(count, totalBytes, subject));
        }
        finally
        {
            Release(attachments);
        }
    }

    private static DateTime? ToDate(object? value) => value switch
    {
        DateTime dt => dt,
        _ => null,
    };

    private static object? Call(object target, string name, params object?[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);

    private static object? Get(object target, string name) =>
        target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture);

    private static void Release(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
        {
            Marshal.FinalReleaseComObject(comObject);
        }
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _thread?.Join(TimeSpan.FromSeconds(5));
        }
        catch (Exception e) when (e is AggregateException or ThreadStateException)
        {
            // best-effort shutdown
        }
        finally
        {
            _cts.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Outlook attachment watch active (classic desktop Outlook only)")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Outlook attachment poll skipped: {Error}")]
    private partial void LogPollFailed(string error);
}
