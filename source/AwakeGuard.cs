using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Xml;
using Microsoft.Win32;

[assembly: AssemblyTitle("AwakeGuard")]
[assembly: AssemblyDescription("A portable Windows idle sleep and restart guard")]
[assembly: AssemblyCompany("AwakeGuard")]
[assembly: AssemblyProduct("AwakeGuard")]
[assembly: AssemblyVersion("1.0.0.0")]

namespace AwakeGuard
{
    internal static class Program
    {
        internal const string ShowMessageName = "AwakeGuard.ShowWindow.8C915833";
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool created;
            using (Mutex instance = new Mutex(true, "Local\\AwakeGuard.8C915833", out created))
            {
                if (!created)
                {
                    if (Array.IndexOf(args, "--tray") < 0)
                        NativeMethods.PostMessage(new IntPtr(0xffff), NativeMethods.RegisterWindowMessage(ShowMessageName), IntPtr.Zero, IntPtr.Zero);
                    return 0;
                }
                try
                {
                    Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                    using (MainForm form = new MainForm(Array.IndexOf(args, "--tray") >= 0))
                        Application.Run(form);
                    return 0;
                }
                catch (Exception ex)
                {
                    MessageBox.Show("AwakeGuard stopped unexpectedly. Its power requests are released when the process exits.\n\n" + ex.Message,
                        "AwakeGuard", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                finally { instance.ReleaseMutex(); }
            }
        }
    }

    internal interface IWindowsGuard : IDisposable
    {
        int SetPower(bool awake, bool display);
        int SetRestartBlock(bool enabled);
        int SetCountdownPrivilege(bool enabled);
        int AbortCountdown();
    }

    internal sealed class GuardController : IDisposable
    {
        private readonly IWindowsGuard windows;
        private readonly Func<TimeSpan> elapsed;
        private TimeSpan started;
        private TimeSpan lastRefresh;
        private bool disposed;
        internal bool Active { get; private set; }
        internal bool KeepAwake = true;
        internal bool KeepDisplay;
        internal bool GuardRestarts = true;
        internal bool PowerApplied { get; private set; }
        internal bool RestartApplied { get; private set; }
        internal bool CountdownAvailable { get; private set; }
        internal int DurationMinutes;
        internal int Intercepted { get; private set; }
        internal string LastEvent = "Ready to protect your session.";
        internal string PowerError = "";
        internal string RestartError = "";
        internal string CountdownError = "";
        internal event Action Changed;
        internal event Action<string> Notification;

        internal GuardController(IWindowsGuard windows, Func<TimeSpan> elapsed)
        { this.windows = windows; this.elapsed = elapsed; }

        internal TimeSpan SessionElapsed { get { return Active ? elapsed() - started : TimeSpan.Zero; } }
        internal TimeSpan Remaining { get { return TimeSpan.FromMinutes(DurationMinutes) - SessionElapsed; } }

        internal void Start()
        {
            if (disposed || Active || (!KeepAwake && !GuardRestarts)) return;
            Active = true;
            started = elapsed();
            Apply();
            LastEvent = "Protection started at " + DateTime.Now.ToString("HH:mm") + ".";
            Signal();
        }

        internal void Apply()
        {
            if (disposed) return;
            int powerError = windows.SetPower(Active && KeepAwake, Active && KeepAwake && KeepDisplay);
            PowerApplied = Active && KeepAwake && powerError == 0;
            PowerError = powerError == 0 ? "" : "Windows rejected the power request (" + powerError + ").";
            int blockError = windows.SetRestartBlock(Active && GuardRestarts);
            RestartApplied = Active && GuardRestarts && blockError == 0;
            RestartError = blockError == 0 ? "" : "Restart registration failed (" + blockError + ").";
            int privilegeError = windows.SetCountdownPrivilege(Active && GuardRestarts);
            CountdownAvailable = Active && GuardRestarts && privilegeError == 0;
            CountdownError = privilegeError == 0 ? "" : "Countdown cancellation is unavailable (" + privilegeError + ").";
            lastRefresh = elapsed();
            Signal();
        }

        internal void ChangeDuration(int minutes)
        {
            DurationMinutes = Math.Max(0, minutes);
            if (Active) { started = elapsed(); LastEvent = "Protection timer updated at " + DateTime.Now.ToString("HH:mm") + "."; }
            Signal();
        }

        internal void Stop(string reason)
        {
            if (disposed) return;
            Active = false;
            Apply();
            LastEvent = reason;
            Signal();
        }

        internal void Tick()
        {
            if (!Active || disposed) return;
            if (DurationMinutes > 0 && Remaining <= TimeSpan.Zero)
            {
                Stop("Timer finished. Normal sleep and restart behavior restored.");
                Notify("Your protection timer finished. Normal sleep and restart behavior is restored.");
                return;
            }
            if (elapsed() - lastRefresh >= TimeSpan.FromSeconds(15)) Apply();
            if (GuardRestarts && CountdownAvailable)
            {
                int error = windows.AbortCountdown();
                if (error == 0) RecordInterception("A pending shutdown or restart countdown was cancelled.");
                else if (error != 1116) // ERROR_NO_SHUTDOWN_IN_PROGRESS is the normal idle result.
                {
                    CountdownError = "Countdown cancellation failed (" + error + ").";
                    CountdownAvailable = false; // Re-evaluate at the next refresh, without busy retries.
                    Signal();
                }
            }
        }

        internal static bool ShouldVeto(bool active, bool restartApplied, long sessionFlags)
        {
            const long Logoff = 0x80000000L;
            const long Critical = 0x40000000L;
            const long CloseApp = 0x1L;
            return active && restartApplied && (sessionFlags & (Logoff | Critical | CloseApp)) == 0;
        }

        internal bool QueryEndSession(long flags)
        {
            bool veto = ShouldVeto(Active, RestartApplied, flags);
            if (veto) RecordInterception("A shutdown or restart request was intercepted. Windows may ask you to cancel it.");
            return !veto;
        }

        private void RecordInterception(string message)
        { Intercepted++; LastEvent = message; Signal(); Notify(message); }
        private void Notify(string message) { if (Notification != null) Notification(message); }
        private void Signal() { if (Changed != null) Changed(); }

        public void Dispose()
        {
            if (disposed) return;
            Stop("Protection stopped.");
            disposed = true;
            windows.Dispose();
        }
    }

    internal sealed class WindowsGuard : IWindowsGuard
    {
        private readonly IntPtr window;
        private bool registered;
        private IntPtr token;
        private NativeMethods.TokenPrivileges previous;
        private bool privilegeEnabled;

        internal WindowsGuard(IntPtr window) { this.window = window; }

        public int SetPower(bool awake, bool display)
        {
            uint flags = 0x80000000u;
            if (awake) flags |= 1u;
            if (display && awake) flags |= 2u;
            return NativeMethods.SetThreadExecutionState(flags) == 0 ? 31 : 0;
        }

        public int SetRestartBlock(bool enabled)
        {
            if (registered == enabled) return 0;
            bool ok = enabled
                ? NativeMethods.ShutdownBlockReasonCreate(window, "AwakeGuard protection is active. Pause or exit AwakeGuard to restart.")
                : NativeMethods.ShutdownBlockReasonDestroy(window);
            if (!ok) return Marshal.GetLastWin32Error();
            registered = enabled;
            return 0;
        }

        public int SetCountdownPrivilege(bool enabled)
        {
            if (enabled == privilegeEnabled) return 0;
            if (!enabled)
            {
                if (token != IntPtr.Zero)
                {
                    bool ok = NativeMethods.RestoreTokenPrivileges(token, false, ref previous, 0, IntPtr.Zero, IntPtr.Zero);
                    int error = Marshal.GetLastWin32Error();
                    NativeMethods.CloseHandle(token); token = IntPtr.Zero; privilegeEnabled = false;
                    return ok ? error : (error == 0 ? 31 : error);
                }
                return 0;
            }
            if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), 0x20u | 0x8u, out token))
                return Marshal.GetLastWin32Error();
            NativeMethods.Luid luid;
            if (!NativeMethods.LookupPrivilegeValue(null, "SeShutdownPrivilege", out luid))
                return CloseTokenWithError();
            NativeMethods.TokenPrivileges desired = new NativeMethods.TokenPrivileges();
            desired.Count = 1; desired.Luid = luid; desired.Attributes = 2;
            uint returned;
            bool adjusted = NativeMethods.AdjustTokenPrivileges(token, false, ref desired,
                (uint)Marshal.SizeOf(typeof(NativeMethods.TokenPrivileges)), out previous, out returned);
            int result = Marshal.GetLastWin32Error();
            if (!adjusted || result != 0)
            {
                NativeMethods.CloseHandle(token); token = IntPtr.Zero;
                return result == 0 ? 31 : result;
            }
            privilegeEnabled = true;
            return 0;
        }

        private int CloseTokenWithError()
        { int error = Marshal.GetLastWin32Error(); NativeMethods.CloseHandle(token); token = IntPtr.Zero; return error; }
        public int AbortCountdown()
        { return NativeMethods.AbortSystemShutdown(null) ? 0 : Marshal.GetLastWin32Error(); }
        public void Dispose()
        { SetPower(false, false); SetRestartBlock(false); SetCountdownPrivilege(false); }
    }

    internal static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)] internal struct Luid { internal uint Low; internal int High; }
        [StructLayout(LayoutKind.Sequential)] internal struct TokenPrivileges
        { internal uint Count; internal Luid Luid; internal uint Attributes; }
        [DllImport("kernel32.dll")] internal static extern uint SetThreadExecutionState(uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShutdownBlockReasonCreate(IntPtr window, string reason);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShutdownBlockReasonDestroy(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShutdownBlockReasonQuery(IntPtr window, StringBuilder reason, ref uint length);
        [DllImport("advapi32.dll", EntryPoint = "AbortSystemShutdownW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AbortSystemShutdown(string machine);
        [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool LookupPrivilegeValue(string machine, string name, out Luid luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
            ref TokenPrivileges desired, uint size, out TokenPrivileges previous, out uint returned);
        [DllImport("advapi32.dll", EntryPoint = "AdjustTokenPrivileges", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RestoreTokenPrivileges(IntPtr token, bool disableAll,
            ref TokenPrivileges desired, uint size, IntPtr previous, IntPtr returned);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterWindowMessage(string name);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyIcon(IntPtr icon);
    }

    internal sealed class Preferences
    {
        internal bool Awake = true;
        internal bool Display;
        internal bool Restarts = true;
        internal int Minutes;
        internal static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AwakeGuard");
        private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.xml");

        internal static Preferences Load()
        {
            Preferences settings = new Preferences();
            try
            {
                if (!File.Exists(FilePath)) return settings;
                XmlDocument document = new XmlDocument(); document.XmlResolver = null;
                XmlReaderSettings safe = new XmlReaderSettings(); safe.DtdProcessing = DtdProcessing.Prohibit; safe.XmlResolver = null;
                using (XmlReader reader = XmlReader.Create(FilePath, safe)) document.Load(reader);
                XmlElement root = document.DocumentElement;
                bool parsed; int minutes;
                if (Boolean.TryParse(root.GetAttribute("awake"), out parsed)) settings.Awake = parsed;
                if (Boolean.TryParse(root.GetAttribute("display"), out parsed)) settings.Display = parsed;
                if (Boolean.TryParse(root.GetAttribute("restarts"), out parsed)) settings.Restarts = parsed;
                if (Int32.TryParse(root.GetAttribute("minutes"), out minutes) && Array.IndexOf(MainForm.DurationChoices, minutes) >= 0) settings.Minutes = minutes;
            }
            catch (Exception ex) { Trace.WriteLine("Could not load preferences: " + ex.Message); }
            return settings;
        }

        internal string Save()
        {
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                XmlDocument document = new XmlDocument();
                XmlElement root = document.CreateElement("AwakeGuard"); document.AppendChild(root);
                root.SetAttribute("awake", Awake.ToString()); root.SetAttribute("display", Display.ToString());
                root.SetAttribute("restarts", Restarts.ToString()); root.SetAttribute("minutes", Minutes.ToString());
                string temporary = FilePath + ".tmp";
                document.Save(temporary);
                if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null); else File.Move(temporary, FilePath);
                return "";
            }
            catch (Exception ex) { return "Preferences could not be saved: " + ex.Message; }
        }
    }

    internal static class StartupSetting
    {
        private const string KeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        private static string Command { get { return "\"" + Application.ExecutablePath + "\" --tray"; } }
        internal static bool IsEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(KeyPath))
                return key != null && String.Equals(key.GetValue("AwakeGuard") as string, Command, StringComparison.OrdinalIgnoreCase);
        }
        internal static void Set(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath))
            {
                if (enabled) key.SetValue("AwakeGuard", Command, RegistryValueKind.String);
                else key.DeleteValue("AwakeGuard", false);
            }
        }
    }

    internal sealed class MainForm : Form
    {
        internal static readonly int[] DurationChoices = { 0, 30, 60, 120, 240, 480 };
        private readonly bool startInTray;
        private readonly Preferences settings;
        private GuardController guard;
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ToolStripMenuItem trayPause = new ToolStripMenuItem("Pause protection");
        private readonly ToolStripMenuItem trayStatus = new ToolStripMenuItem("Protection is starting...");
        private readonly HeroPanel hero = new HeroPanel();
        private readonly OptionCard awakeCard;
        private readonly OptionCard displayCard;
        private readonly OptionCard restartCard;
        private readonly ComboBox duration = new ComboBox();
        private readonly Label session = new Label();
        private readonly Label lastEvent = new Label();
        private readonly Label restartDetail = new Label();
        private readonly Button pause = new Button();
        private readonly CheckBox startup = new CheckBox();
        private readonly uint showMessage;
        private bool exitRequested;
        private bool updating;
        private bool trayTipShown;
        private string preferenceError = "";

        internal MainForm(bool startInTray) : this(startInTray, Preferences.Load()) { }

        internal MainForm(bool startInTray, Preferences preferences)
        {
            SuspendLayout();
            this.startInTray = startInTray;
            settings = preferences;
            showMessage = NativeMethods.RegisterWindowMessage(Program.ShowMessageName);
            Text = "AwakeGuard";
            Font = new Font("Segoe UI", 10f);
            BackColor = Theme.Canvas;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(600, 752);
            MinimumSize = new Size(616, 791);
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            Icon = Theme.CreateIcon();

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill; root.Padding = new Padding(24, 16, 24, 16);
            root.ColumnCount = 1; root.RowCount = 11;
            int[] heights = { 64, 122, 28, 76, 76, 76, 70, 64, 48, 40 };
            foreach (int height in heights) root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            Panel header = new Panel(); header.Dock = DockStyle.Fill;
            PictureBox mark = new PictureBox(); mark.Size = new Size(40, 40); mark.Location = new Point(0, 4);
            mark.Image = Theme.CreateMark(40); mark.SizeMode = PictureBoxSizeMode.Zoom; header.Controls.Add(mark);
            header.Controls.Add(Theme.Label("AwakeGuard", 18f, FontStyle.Bold, Theme.Ink, new Rectangle(52, 0, 340, 32)));
            header.Controls.Add(Theme.Label("Keep your PC ready.", 10f, FontStyle.Regular, Theme.Muted, new Rectangle(54, 34, 320, 22)));
            root.Controls.Add(header, 0, 0);
            hero.Dock = DockStyle.Fill; hero.Margin = new Padding(0, 0, 0, 6); root.Controls.Add(hero, 0, 1);
            Label section = Theme.Label("PROTECTION OPTIONS", 8f, FontStyle.Bold, Theme.Muted, Rectangle.Empty);
            section.Dock = DockStyle.Fill; section.TextAlign = ContentAlignment.MiddleLeft; section.Margin = new Padding(0);
            root.Controls.Add(section, 0, 2);

            awakeCard = new OptionCard("Keep computer awake", "Prevent automatic sleep while protection is running.", settings.Awake);
            displayCard = new OptionCard("Keep screen on", "Also prevent the display from turning off.", settings.Display);
            restartCard = new OptionCard("Guard shutdowns and restarts", "Intercept requests and cancel pending countdowns.", settings.Restarts);
            root.Controls.Add(awakeCard, 0, 3); root.Controls.Add(displayCard, 0, 4); root.Controls.Add(restartCard, 0, 5);
            awakeCard.Toggle.CheckedChanged += OptionsChanged;
            displayCard.Toggle.CheckedChanged += OptionsChanged;
            restartCard.Toggle.CheckedChanged += OptionsChanged;

            Panel durationRow = new Panel(); durationRow.Dock = DockStyle.Fill; durationRow.Margin = new Padding(0);
            durationRow.Controls.Add(Theme.Label("Protect for", 10f, FontStyle.Bold, Theme.Ink, new Rectangle(0, 10, 108, 25)));
            duration.DropDownStyle = ComboBoxStyle.DropDownList;
            duration.Items.AddRange(new object[] { "Until I stop it", "30 minutes", "1 hour", "2 hours", "4 hours", "8 hours" });
            duration.SetBounds(115, 7, 174, 30); duration.AccessibleName = "Protection duration";
            duration.SelectedIndex = Math.Max(0, Array.IndexOf(DurationChoices, settings.Minutes));
            duration.SelectedIndexChanged += DurationChanged; durationRow.Controls.Add(duration);
            session.SetBounds(310, 10, 242, 25); session.TextAlign = ContentAlignment.MiddleRight; session.ForeColor = Theme.Muted;
            durationRow.Controls.Add(session);
            restartDetail.SetBounds(0, 42, 552, 22); restartDetail.Font = new Font("Segoe UI", 8.5f); restartDetail.ForeColor = Theme.Muted;
            durationRow.Controls.Add(restartDetail); root.Controls.Add(durationRow, 0, 6);

            Panel note = new Panel(); note.Dock = DockStyle.Fill; note.BackColor = Color.FromArgb(234, 239, 243); note.Margin = new Padding(0, 0, 0, 10);
            Label noteLabel = Theme.Label("Windows can still force a restart. Manual sleep, lid-close actions,\ncritical battery events and power loss can override protection.", 9f, FontStyle.Regular, Theme.Muted, Rectangle.Empty);
            noteLabel.Dock = DockStyle.Fill; noteLabel.Padding = new Padding(12, 8, 8, 4); note.Controls.Add(noteLabel);
            root.Controls.Add(note, 0, 7);

            TableLayoutPanel actions = new TableLayoutPanel(); actions.Dock = DockStyle.Fill; actions.ColumnCount = 3;
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30)); actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
            actions.Margin = new Padding(0);
            Theme.StyleButton(pause, true); pause.Dock = DockStyle.Fill; pause.Text = "Pause protection"; pause.Margin = new Padding(0, 0, 10, 4);
            pause.Click += delegate { ToggleProtection(); }; actions.Controls.Add(pause, 0, 0);
            Button hide = new Button(); Theme.StyleButton(hide, false); hide.Text = "Hide to tray"; hide.Dock = DockStyle.Fill; hide.Margin = new Padding(0, 0, 10, 4);
            hide.Click += delegate { HideToTray(); }; actions.Controls.Add(hide, 1, 0);
            Button exit = new Button(); Theme.StyleButton(exit, false); exit.Text = "Exit"; exit.Dock = DockStyle.Fill; exit.Margin = new Padding(0, 0, 0, 4);
            exit.Click += delegate { ExitApp(); }; actions.Controls.Add(exit, 2, 0);
            root.Controls.Add(actions, 0, 8);

            Panel footer = new Panel(); footer.Dock = DockStyle.Fill; footer.Margin = new Padding(0);
            startup.Text = "Start at Windows sign-in"; startup.SetBounds(0, 5, 250, 30); startup.ForeColor = Theme.Muted; startup.Font = new Font("Segoe UI", 9f);
            try { startup.Checked = StartupSetting.IsEnabled(); } catch (Exception ex) { preferenceError = "Startup setting could not be read: " + ex.Message; }
            startup.CheckedChanged += StartupChanged; footer.Controls.Add(startup);
            Label help = Theme.Label("?", 10f, FontStyle.Bold, Theme.Accent, new Rectangle(516, 7, 30, 26));
            help.TextAlign = ContentAlignment.MiddleCenter; help.Cursor = Cursors.Hand; help.TabStop = true; help.AccessibleName = "Help and limitations";
            help.Click += delegate { ShowHelp(); }; help.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) ShowHelp(); };
            footer.Controls.Add(help); root.Controls.Add(footer, 0, 9);
            lastEvent.Dock = DockStyle.Fill; lastEvent.Font = new Font("Segoe UI", 8.5f); lastEvent.ForeColor = Theme.Muted; lastEvent.Margin = new Padding(0, 4, 0, 0);
            root.Controls.Add(lastEvent, 0, 10);

            ContextMenuStrip menu = new ContextMenuStrip();
            trayStatus.Enabled = false; menu.Items.Add(trayStatus); menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open AwakeGuard", null, delegate { ShowWindow(); });
            trayPause.Click += delegate { ToggleProtection(); }; menu.Items.Add(trayPause);
            menu.Items.Add("Exit and stop protection", null, delegate { ExitApp(); });
            tray.Icon = Icon; tray.Text = "AwakeGuard"; tray.ContextMenuStrip = menu; tray.Visible = true;
            tray.DoubleClick += delegate { ShowWindow(); };
            timer.Interval = 1000; timer.Tick += delegate { if (guard != null) { guard.Tick(); UpdateState(); } };
            ResumeLayout(false);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            guard = new GuardController(new WindowsGuard(Handle), delegate { return clock.Elapsed; });
            guard.KeepAwake = settings.Awake; guard.KeepDisplay = settings.Display; guard.GuardRestarts = settings.Restarts; guard.DurationMinutes = settings.Minutes;
            guard.Changed += UpdateState;
            guard.Notification += delegate(string text) { BeginInvoke((Action)delegate { if (!exitRequested) Balloon(text); }); };
            guard.Start(); UpdateState(); timer.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (startInTray) BeginInvoke((Action)delegate { HideToTray(); });
        }

        private void OptionsChanged(object sender, EventArgs e)
        {
            if (updating || guard == null) return;
            guard.KeepAwake = awakeCard.Toggle.Checked;
            guard.KeepDisplay = displayCard.Toggle.Checked;
            guard.GuardRestarts = restartCard.Toggle.Checked;
            if (!guard.KeepAwake && !guard.GuardRestarts) guard.Stop("Protection paused. Enable an option to start again.");
            else guard.Apply();
            SavePreferences(); UpdateState();
        }

        private void DurationChanged(object sender, EventArgs e)
        { if (guard == null || updating) return; guard.ChangeDuration(DurationChoices[duration.SelectedIndex]); SavePreferences(); UpdateState(); }

        private void SavePreferences()
        {
            settings.Awake = awakeCard.Toggle.Checked; settings.Display = displayCard.Toggle.Checked;
            settings.Restarts = restartCard.Toggle.Checked; settings.Minutes = DurationChoices[duration.SelectedIndex];
            preferenceError = settings.Save();
        }

        private void StartupChanged(object sender, EventArgs e)
        {
            if (updating) return;
            try { StartupSetting.Set(startup.Checked); }
            catch (Exception ex)
            {
                updating = true; startup.Checked = !startup.Checked; updating = false;
                MessageBox.Show(this, "The Windows sign-in setting could not be changed.\n\n" + ex.Message, "AwakeGuard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ToggleProtection()
        {
            if (guard == null) return;
            if (guard.Active) guard.Stop("Protection paused. Normal sleep and restart behavior restored.");
            else guard.Start();
            UpdateState();
        }

        private void UpdateState()
        {
            if (guard == null || IsDisposed) return;
            displayCard.Toggle.Enabled = guard.KeepAwake;
            bool warning = guard.PowerError.Length > 0 || guard.RestartError.Length > 0;
            hero.Active = guard.Active; hero.Warning = warning;
            hero.Headline = guard.Active ? (warning ? "Protection needs attention" : "Protection is running") : "Protection is paused";
            List<string> state = new List<string>();
            if (guard.PowerApplied) state.Add(guard.KeepDisplay ? "PC and screen kept awake" : "PC kept awake");
            if (guard.RestartApplied) state.Add("Restart guard enabled");
            hero.Detail = guard.Active ? (state.Count > 0 ? String.Join("  /  ", state.ToArray()) : "Windows could not enable protection.") : "Windows can sleep and restart normally.";
            hero.Badge = guard.Active ? (warning ? "CHECK STATUS" : "ACTIVE") : "PAUSED"; hero.Invalidate();
            pause.Text = guard.Active ? "Pause protection" : "Start protection";
            pause.Enabled = guard.Active || guard.KeepAwake || guard.GuardRestarts;
            session.Text = guard.Active ? (guard.DurationMinutes == 0 ? "Active " + FormatTime(guard.SessionElapsed) : "Remaining " + FormatTime(guard.Remaining)) : "Session stopped";
            if (!guard.Active || !guard.GuardRestarts) restartDetail.Text = "Close the window to keep the app in your system tray.";
            else restartDetail.Text = guard.CountdownAvailable ? "Restart guard: requests + countdowns  |  Intercepted: " + guard.Intercepted : "Restart guard: requests only  |  Countdown cancellation unavailable";
            restartDetail.ForeColor = guard.Active && guard.GuardRestarts && !guard.CountdownAvailable ? Theme.Warning : Theme.Muted;
            List<string> errors = new List<string>();
            if (guard.PowerError.Length > 0) errors.Add(guard.PowerError);
            if (guard.RestartError.Length > 0) errors.Add(guard.RestartError);
            if (guard.CountdownError.Length > 0 && guard.Active) errors.Add(guard.CountdownError);
            if (preferenceError.Length > 0) errors.Add(preferenceError);
            lastEvent.Text = errors.Count > 0 ? String.Join(" ", errors.ToArray()) : guard.LastEvent;
            lastEvent.ForeColor = errors.Count > 0 ? Theme.Warning : Theme.Muted;
            trayPause.Text = guard.Active ? "Pause protection" : "Start protection";
            trayPause.Enabled = pause.Enabled;
            trayStatus.Text = !guard.Active ? "Protection paused" : warning ? "Protection needs attention" : "Protection running";
            tray.Text = "AwakeGuard - " + (guard.Active ? (warning ? "check status" : "protection running") : "paused");
        }

        private static string FormatTime(TimeSpan span)
        { return ((int)Math.Max(0, span.TotalHours)).ToString("00") + ":" + Math.Max(0, span.Minutes).ToString("00") + ":" + Math.Max(0, span.Seconds).ToString("00"); }

        private void HideToTray()
        {
            Hide();
            if (!trayTipShown) { Balloon("AwakeGuard is in your system tray. Double-click its shield to open it. Use Exit to stop protection."); trayTipShown = true; }
        }
        private void ShowWindow()
        { Show(); WindowState = FormWindowState.Normal; Activate(); BringToFront(); }
        private void Balloon(string text)
        { tray.ShowBalloonTip(4000, "AwakeGuard", text, ToolTipIcon.Info); }
        private void ExitApp()
        { exitRequested = true; if (guard != null) guard.Stop("Protection stopped."); Close(); }

        private void ShowHelp()
        {
            MessageBox.Show(this,
                "AwakeGuard keeps your PC awake while it is running.\n\n" +
                "Restart guard uses Windows shutdown-blocking APIs and tries once per second to cancel shutdown/restart countdowns. Windows may show a blocking-app screen: choose Cancel to stay running. Restart anyway overrides the guard.\n\n" +
                "Forced updates, immediate restarts, manual sleep, lid-close actions, critical battery events, crashes and power loss can override protection. Sign-out is allowed. Countdown cancellation needs your account's shutdown privilege; the status line reports if it is unavailable.\n\n" +
                "Close or minimize the window to keep protecting in the tray. Pause or Exit restores normal behavior. A timer releases protection when it expires. Startup is optional and starts a new protection session at sign-in.\n\n" +
                "Power-plan settings and Windows Update services are not changed. Keeping a laptop awake uses battery; leave ventilation clear.\n\nVersion 1.0.0",
                "About AwakeGuard", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        protected override void OnResize(EventArgs e)
        { base.OnResize(e); if (WindowState == FormWindowState.Minimized && guard != null) HideToTray(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!exitRequested && e.CloseReason == CloseReason.UserClosing)
            { e.Cancel = true; HideToTray(); return; }
            // Never delay an already-approved forced shutdown or sign-out during cleanup.
            exitRequested = true; timer.Stop();
            if (guard != null) guard.Dispose();
            tray.Visible = false;
            base.OnFormClosing(e);
        }

        protected override void WndProc(ref Message message)
        {
            if ((uint)message.Msg == showMessage && showMessage != 0)
            { if (guard != null) ShowWindow(); message.Result = IntPtr.Zero; return; }
            if (message.Msg == 0x0011 && guard != null) // WM_QUERYENDSESSION
            { message.Result = guard.QueryEndSession(message.LParam.ToInt64()) ? new IntPtr(1) : IntPtr.Zero; return; }
            if (message.Msg == 0x0016 && message.WParam != IntPtr.Zero && guard != null) // WM_ENDSESSION
            { exitRequested = true; timer.Stop(); guard.Dispose(); tray.Visible = false; }
            if (message.Msg == 0x0218 && guard != null &&
                (message.WParam.ToInt64() == 7 || message.WParam.ToInt64() == 18)) // Resume
            { guard.Tick(); if (guard.Active) guard.Apply(); }
            base.WndProc(ref message);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                if (guard != null) guard.Dispose();
                tray.Visible = false;
                if (tray.ContextMenuStrip != null) tray.ContextMenuStrip.Dispose();
                tray.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal static class Theme
    {
        internal static readonly Color Canvas = Color.FromArgb(244, 246, 248);
        internal static readonly Color Ink = Color.FromArgb(22, 44, 57);
        internal static readonly Color Muted = Color.FromArgb(96, 113, 124);
        internal static readonly Color Accent = Color.FromArgb(0, 115, 105);
        internal static readonly Color Mint = Color.FromArgb(113, 236, 195);
        internal static readonly Color Warning = Color.FromArgb(143, 83, 13);

        internal static Label Label(string text, float size, FontStyle style, Color color, Rectangle bounds)
        {
            Label label = new Label(); label.Text = text; label.Font = new Font("Segoe UI", size, style);
            label.ForeColor = color; label.Bounds = bounds; label.AutoSize = false;
            return label;
        }

        internal static void StyleButton(Button button, bool primary)
        {
            button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(207, 217, 224);
            button.BackColor = primary ? Accent : Color.White;
            button.ForeColor = primary ? Color.White : Ink;
            button.Font = new Font("Segoe UI", 10f, primary ? FontStyle.Bold : FontStyle.Regular);
            button.Cursor = Cursors.Hand; button.UseVisualStyleBackColor = false;
        }

        internal static GraphicsPath Rounded(RectangleF rect, float radius)
        {
            float d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
        }

        internal static void DrawShield(Graphics graphics, RectangleF rect, Color color, bool check)
        {
            PointF[] points = {
                new PointF(rect.X + rect.Width * .5f, rect.Y), new PointF(rect.Right, rect.Y + rect.Height * .18f),
                new PointF(rect.Right - rect.Width * .06f, rect.Y + rect.Height * .61f), new PointF(rect.X + rect.Width * .5f, rect.Bottom),
                new PointF(rect.X + rect.Width * .06f, rect.Y + rect.Height * .61f), new PointF(rect.X, rect.Y + rect.Height * .18f)
            };
            using (SolidBrush fill = new SolidBrush(color)) graphics.FillPolygon(fill, points);
            if (check)
                using (Pen pen = new Pen(color == Accent ? Color.White : Ink, Math.Max(2f, rect.Width * .08f)))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                    graphics.DrawLines(pen, new PointF[] { new PointF(rect.X + rect.Width * .28f, rect.Y + rect.Height * .49f),
                        new PointF(rect.X + rect.Width * .43f, rect.Y + rect.Height * .63f), new PointF(rect.X + rect.Width * .73f, rect.Y + rect.Height * .33f) });
                }
        }

        internal static Bitmap CreateMark(int size)
        {
            Bitmap bitmap = new Bitmap(size, size);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            { graphics.SmoothingMode = SmoothingMode.AntiAlias; DrawShield(graphics, new RectangleF(5, 3, size - 10, size - 6), Accent, true); }
            return bitmap;
        }

        internal static Icon CreateIcon()
        {
            using (Bitmap bitmap = CreateMark(64))
            {
                IntPtr handle = bitmap.GetHicon();
                try { using (Icon temporary = Icon.FromHandle(handle)) return (Icon)temporary.Clone(); }
                finally { NativeMethods.DestroyIcon(handle); }
            }
        }
    }

    internal sealed class HeroPanel : Panel
    {
        internal bool Active;
        internal bool Warning;
        internal string Headline = "Starting protection...";
        internal string Detail = "Checking Windows protection APIs";
        internal string Badge = "STARTING";
        internal HeroPanel() { DoubleBuffered = true; BackColor = Theme.Canvas; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics; graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float scale = graphics.DpiX / 96f;
            using (GraphicsPath path = Theme.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), 12 * scale))
            using (SolidBrush brush = new SolidBrush(Theme.Ink)) graphics.FillPath(brush, path);
            using (Font small = new Font("Segoe UI", 8f, FontStyle.Bold))
            using (Font large = new Font("Segoe UI", 19f, FontStyle.Bold))
            using (Font regular = new Font("Segoe UI", 9f))
            {
                Color tint = Warning ? Color.FromArgb(255, 208, 136) : Active ? Theme.Mint : Color.FromArgb(173, 192, 204);
                TextRenderer.DrawText(graphics, Badge, small, new Rectangle((int)(22 * scale), (int)(16 * scale), Width - (int)(104 * scale), (int)(20 * scale)), tint, TextFormatFlags.NoPadding);
                TextRenderer.DrawText(graphics, Headline, large, new Rectangle((int)(20 * scale), (int)(39 * scale), Width - (int)(74 * scale), (int)(36 * scale)), Color.White, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(graphics, Detail, regular, new Rectangle((int)(22 * scale), (int)(83 * scale), Width - (int)(32 * scale), (int)(25 * scale)), Color.FromArgb(197, 219, 227), TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                Theme.DrawShield(graphics, new RectangleF(Width - 58 * scale, 16 * scale, 31 * scale, 38 * scale), tint, Active && !Warning);
            }
        }
    }

    internal sealed class OptionCard : UserControl
    {
        internal readonly ToggleSwitch Toggle = new ToggleSwitch();
        internal OptionCard(string title, string detail, bool selected)
        {
            Dock = DockStyle.Fill; Margin = new Padding(0, 0, 0, 8); BackColor = Color.White; DoubleBuffered = true;
            Padding = new Padding(14, 9, 14, 9);
            TableLayoutPanel row = new TableLayoutPanel(); row.Dock = DockStyle.Fill; row.ColumnCount = 2; row.RowCount = 2; row.Margin = new Padding(0);
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
            row.RowStyles.Add(new RowStyle(SizeType.Percent, 53)); row.RowStyles.Add(new RowStyle(SizeType.Percent, 47));
            Label name = Theme.Label(title, 11f, FontStyle.Bold, Theme.Ink, Rectangle.Empty); name.Dock = DockStyle.Fill; name.Margin = new Padding(0); name.TextAlign = ContentAlignment.MiddleLeft;
            Label description = Theme.Label(detail, 8.75f, FontStyle.Regular, Theme.Muted, Rectangle.Empty); description.Dock = DockStyle.Fill; description.Margin = new Padding(0); description.TextAlign = ContentAlignment.MiddleLeft;
            row.Controls.Add(name, 0, 0); row.Controls.Add(description, 0, 1);
            Toggle.Checked = selected; Toggle.AccessibleName = title; Toggle.AccessibleDescription = detail;
            Toggle.Anchor = AnchorStyles.None; Toggle.Size = new Size(52, 30); Toggle.Margin = new Padding(4, 0, 0, 0);
            row.Controls.Add(Toggle, 1, 0); row.SetRowSpan(Toggle, 2); Controls.Add(row);
            name.Click += delegate { if (Toggle.Enabled) Toggle.Checked = !Toggle.Checked; };
            description.Click += delegate { if (Toggle.Enabled) Toggle.Checked = !Toggle.Checked; };
        }
        protected override void OnPaint(PaintEventArgs e)
        { base.OnPaint(e); using (Pen border = new Pen(Color.FromArgb(220, 227, 232))) e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1); }
    }

    internal sealed class ToggleSwitch : CheckBox
    {
        internal ToggleSwitch()
        {
            AutoSize = false; Appearance = Appearance.Button; FlatStyle = FlatStyle.Flat; Text = "";
            Cursor = Cursors.Hand; SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics; graphics.Clear(Parent == null ? Color.White : Parent.BackColor); graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float pad = Height * .14f;
            RectangleF track = new RectangleF(2, pad, Width - 4, Height - 2 * pad);
            Color fill = !Enabled ? Color.FromArgb(224, 230, 234) : Checked ? Theme.Accent : Color.FromArgb(160, 174, 183);
            using (GraphicsPath path = Theme.Rounded(track, track.Height / 2))
            using (SolidBrush brush = new SolidBrush(fill)) graphics.FillPath(brush, path);
            float diameter = track.Height - 6;
            float left = Checked ? track.Right - diameter - 3 : track.Left + 3;
            using (SolidBrush brush = new SolidBrush(Color.White)) graphics.FillEllipse(brush, left, track.Top + 3, diameter, diameter);
            if (Focused) ControlPaint.DrawFocusRectangle(graphics, ClientRectangle);
        }
    }
}
