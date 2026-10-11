using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Outlook = Microsoft.Office.Interop.Outlook;

namespace Zinwe.Outlook.SendGuard
{
    /// <summary>
    /// The standard COM add-in "Extensibility" interface. Declared here (canonical GUID) so the project needs no
    /// reference to the Add-In Designer / Extensibility type library.
    /// </summary>
    [ComImport]
    [Guid("B65AD801-ABAF-11D0-BB8B-00A0C90F2744")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IDTExtensibility2
    {
        void OnConnection(object application, int connectMode, object addInInst, ref Array custom);
        void OnDisconnection(int removeMode, ref Array custom);
        void OnAddInsUpdate(ref Array custom);
        void OnStartupComplete(ref Array custom);
        void OnBeginShutdown(ref Array custom);
    }

    /// <summary>
    /// Zinwe SendGuard: a classic desktop Outlook COM add-in that CANCELS an outgoing email whose attachments
    /// exceed the company's size limit, and tells the user why. The limit is read from the registry (written by
    /// the Zinwe agent / MSI), so it tracks the owner's dashboard policy; a sensible default applies if absent.
    ///
    /// This is the only way to truly block a send (Outlook fires ItemSend in-process with a Cancel flag). It covers
    /// CLASSIC Outlook only — New Outlook and webmail have no equivalent hook (those are watched by the agent's
    /// upload monitoring instead). Everything is wrapped so a fault never blocks or corrupts the user's mail.
    /// </summary>
    [ComVisible(true)]
    [Guid(ClassId)]
    [ProgId(ProgId)]
    [ClassInterface(ClassInterfaceType.None)]
    [ComDefaultInterface(typeof(IDTExtensibility2))]
    public sealed class Connect : IDTExtensibility2
    {
        public const string ProgId = "Zinwe.SendGuard";
        public const string ClassId = "7B2C9E14-1D3A-4C8E-9F6B-0A1B2C3D4E5F";

        // Registry the agent/MSI writes the current email limit to (HKLM preferred, HKCU fallback), in bytes.
        private const string SettingsKey = @"SOFTWARE\Zinwe\SendGuard";
        private const string LimitValue = "MaxAttachmentBytes";
        private const long DefaultLimitBytes = 20L * 1024 * 1024; // 20 MB if nothing is configured

        private Outlook.Application _app;

        public void OnConnection(object application, int connectMode, object addInInst, ref Array custom)
        {
            try
            {
                _app = application as Outlook.Application;
                if (_app != null)
                {
                    _app.ItemSend += OnItemSend;
                }
            }
            catch
            {
                // Never let a wiring fault stop Outlook from loading.
            }
        }

        public void OnDisconnection(int removeMode, ref Array custom)
        {
            try
            {
                if (_app != null)
                {
                    _app.ItemSend -= OnItemSend;
                }
            }
            catch
            {
            }
            finally
            {
                _app = null;
            }
        }

        public void OnAddInsUpdate(ref Array custom) { }
        public void OnStartupComplete(ref Array custom) { }
        public void OnBeginShutdown(ref Array custom) { }

        /// <summary>Fired before any item is sent. Setting <paramref name="cancel"/> true stops the send.</summary>
        private void OnItemSend(object item, ref bool cancel)
        {
            try
            {
                var mail = item as Outlook.MailItem;
                if (mail == null)
                {
                    return; // only guard real mail (not meeting requests, etc.)
                }

                long total = 0;
                var attachments = mail.Attachments;
                if (attachments != null)
                {
                    for (int i = 1; i <= attachments.Count; i++) // Outlook collections are 1-based
                    {
                        try { total += attachments[i].Size; } catch { /* skip an unreadable attachment */ }
                    }
                }

                long limit = ReadLimitBytes();
                if (limit > 0 && total > limit)
                {
                    cancel = true;
                    ShowBlockedMessage(total, limit);
                }
            }
            catch
            {
                // On any error, do NOT cancel — never silently eat a user's mail because of a bug here.
            }
        }

        private static void ShowBlockedMessage(long total, long limit)
        {
            try
            {
                System.Windows.Forms.MessageBox.Show(
                    "This email's attachments (" + Human(total) + ") are over your company's " + Human(limit) +
                    " limit, so it was not sent.\n\nReduce the attachment size or share a link instead.",
                    "Zinwe — email not sent",
                    System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
            }
            catch
            {
            }
        }

        private static long ReadLimitBytes()
        {
            foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                try
                {
                    using (var key = root.OpenSubKey(SettingsKey))
                    {
                        var raw = key?.GetValue(LimitValue);
                        if (raw != null)
                        {
                            var value = Convert.ToInt64(raw, CultureInfo.InvariantCulture);
                            if (value >= 0)
                            {
                                return value; // 0 disables the block
                            }
                        }
                    }
                }
                catch
                {
                    // try the next root, then the default
                }
            }
            return DefaultLimitBytes;
        }

        private static string Human(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return (unit == 0 ? value.ToString(CultureInfo.InvariantCulture)
                              : Math.Round(value, 1).ToString(CultureInfo.InvariantCulture)) + " " + units[unit];
        }

        // --- COM registration: add/remove the Outlook add-in registry entry on regasm ---------------------------

        private static string AddinKey => @"Software\Microsoft\Office\Outlook\Addins\" + ProgId;

        [ComRegisterFunction]
        public static void RegisterFunction(Type type)
        {
            // Per-user load. LoadBehavior 3 = loaded + load at startup.
            using (var key = Registry.CurrentUser.CreateSubKey(AddinKey))
            {
                key.SetValue("FriendlyName", "Zinwe SendGuard");
                key.SetValue("Description", "Blocks outgoing email whose attachments exceed the company limit.");
                key.SetValue("LoadBehavior", 3, RegistryValueKind.DWord);
            }
        }

        [ComUnregisterFunction]
        public static void UnregisterFunction(Type type)
        {
            try { Registry.CurrentUser.DeleteSubKey(AddinKey, throwOnMissingSubKey: false); } catch { }
        }
    }
}
