using System.Runtime.InteropServices;

namespace BetterScanner;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Single instance: a second launch just brings the running window forward.
        using var mutex = new Mutex(true, "BetterScanner.SingleInstance" + AppSettings.ProfileSuffix, out bool first);
        if (!first)
        {
            try
            {
                using var ev = EventWaitHandle.OpenExisting("BetterScanner.Show" + AppSettings.ProfileSuffix);
                ev.Set();
            }
            catch
            {
                // the other instance is shutting down
            }
            return;
        }

        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "BetterScanner.Show" + AppSettings.ProfileSuffix);
#if NETFRAMEWORK
        Native.SetProcessDPIAware(); // sharp text on high-DPI screens (Windows 7)
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
#else
        ApplicationConfiguration.Initialize();
#endif
        var form = new MainForm();

        var listener = new Thread(() =>
        {
            while (showEvent.WaitOne())
            {
                if (form.IsDisposed) return;
                try { form.BeginInvoke((Action)form.ShowFromTray); } catch { return; }
            }
        }) { IsBackground = true };
        listener.Start();

        Application.Run(form);
    }
}

internal static class Native
{
    private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    /// <summary>Keeps the PC from sleeping while hands-free scanning is waiting (call on the UI thread).</summary>
    public static void KeepAwake(bool on) =>
        SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDPIAware();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    /// <summary>Grey hint text in an empty box (TextBox.PlaceholderText only exists on .NET 10).</summary>
    public static void SetPlaceholder(TextBox tb, string text)
    {
#if NETFRAMEWORK
        const int EM_SETCUEBANNER = 0x1501;
        void Apply() => SendMessage(tb.Handle, EM_SETCUEBANNER, (IntPtr)1, text);
        if (tb.IsHandleCreated) Apply();
        tb.HandleCreated += (_, _) => Apply();
#else
        tb.PlaceholderText = text;
#endif
    }
}
