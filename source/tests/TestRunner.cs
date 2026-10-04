using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using AwakeGuard;

internal sealed class FakeWindows : IWindowsGuard
{
    internal bool Awake, Display, Block, Privilege, Disposed;
    internal int PowerError, BlockError, PrivilegeError, AbortCalls;
    internal int AbortResult = 1116;
    public int SetPower(bool awake, bool display) { Awake = awake; Display = display; return PowerError; }
    public int SetRestartBlock(bool enabled) { Block = enabled; return BlockError; }
    public int SetCountdownPrivilege(bool enabled) { Privilege = enabled; return PrivilegeError; }
    public int AbortCountdown() { AbortCalls++; return AbortResult; }
    public void Dispose() { Disposed = true; }
}

internal static class TestRunner
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
    private static int assertions;
    private static void Check(bool passed, string name)
    { if (!passed) throw new Exception("FAILED: " + name); assertions++; Console.WriteLine("PASS: " + name); }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            TimeSpan now = TimeSpan.Zero;
            FakeWindows fake = new FakeWindows();
            using (GuardController guard = new GuardController(fake, delegate { return now; }))
            {
                guard.KeepDisplay = true; guard.DurationMinutes = 30; guard.Start();
                Check(fake.Awake && fake.Display && fake.Block && fake.Privilege, "Start requests wake, display, restart block, countdown privilege");
                Check(guard.PowerApplied && guard.RestartApplied && guard.CountdownAvailable, "Status reflects successful native calls");
                Check(!guard.QueryEndSession(0), "Ordinary shutdown request is vetoed");
                Check(guard.QueryEndSession(0x80000000L), "Sign-out is allowed");
                Check(guard.QueryEndSession(0xC0000001L), "Combined critical, logoff and servicing flags are allowed");
                Check(guard.QueryEndSession(1), "Application replacement notification is allowed");
                fake.AbortResult = 0; guard.Tick();
                Check(guard.Intercepted == 2 && fake.AbortCalls == 1, "Successful countdown cancellation is counted");
                fake.AbortResult = 1116; guard.Tick();
                Check(guard.Intercepted == 2, "No-shutdown-in-progress does not produce alerts");
                fake.AbortResult = 5; guard.Tick(); int calls = fake.AbortCalls; guard.Tick();
                Check(!guard.CountdownAvailable && fake.AbortCalls == calls, "Access error disables countdown polling until refresh");
                now = TimeSpan.FromSeconds(16); fake.AbortResult = 1116; guard.Tick();
                Check(guard.CountdownAvailable, "Native capabilities are retried after refresh interval");
                guard.KeepAwake = false; guard.Apply();
                Check(!fake.Awake && !fake.Display && fake.Block, "Restart-only mode releases wake and display requests");
                now = TimeSpan.FromMinutes(29); guard.Tick(); Check(guard.Active, "Protection remains active before timer deadline");
                now = TimeSpan.FromMinutes(30); guard.Tick();
                Check(!guard.Active && !fake.Block && !fake.Privilege && !fake.Awake, "Timer expiry releases all protection");
                Check(guard.QueryEndSession(0), "Paused guard allows shutdown");
                guard.Start(); now += TimeSpan.FromMinutes(10); guard.ChangeDuration(60);
                Check(guard.Remaining == TimeSpan.FromMinutes(60), "Changing duration starts a fresh duration from now");
                guard.Stop("test");
                guard.KeepAwake = false; guard.GuardRestarts = false; guard.Start();
                Check(!guard.Active, "Empty option selection cannot start protection");
                guard.KeepAwake = true; guard.GuardRestarts = true;
                fake.PowerError = 31; fake.BlockError = 5; fake.PrivilegeError = 1300; guard.Start();
                Check(!guard.PowerApplied && !guard.RestartApplied && !guard.CountdownAvailable, "Failed native calls never report effective protection");
                Check(guard.QueryEndSession(0), "Failed restart registration does not claim to veto shutdown");
            }
            Check(fake.Disposed && !fake.Awake && !fake.Display && !fake.Block && !fake.Privilege, "Dispose releases all native resources");

            using (Form window = new Form())
            using (WindowsGuard native = new WindowsGuard(window.Handle))
            {
                Check(native.SetPower(true, true) == 0, "Real Windows wake/display API accepts request");
                uint prior = NativeMethods.SetThreadExecutionState(0x80000000u);
                Check((prior & 3u) == 3u, "Real thread execution state has system and display flags");
                Check(native.SetPower(false, false) == 0, "Real Windows wake request is released");
                Check(native.SetRestartBlock(true) == 0, "Real Windows shutdown reason is registered");
                StringBuilder reason = new StringBuilder(256); uint length = 256;
                Check(NativeMethods.ShutdownBlockReasonQuery(window.Handle, reason, ref length) && reason.ToString().Contains("AwakeGuard"), "Windows exposes registered blocking reason");
                Check(native.SetRestartBlock(false) == 0, "Real Windows shutdown reason is removed");
                reason.Clear(); length = 256;
                Check(!NativeMethods.ShutdownBlockReasonQuery(window.Handle, reason, ref length), "Reason is absent after removal");
                int privilegeError = native.SetCountdownPrivilege(true);
                Console.WriteLine("INFO: Real countdown privilege result = " + privilegeError);
                Check(native.SetCountdownPrivilege(false) == 0, "Shutdown privilege can be restored or was never granted");
            }

            using (MainForm ui = new MainForm(false, new Preferences()))
            {
                ui.StartPosition = FormStartPosition.Manual;
                ui.Location = new Point(-3000, -3000);
                ui.Show(); Application.DoEvents();
                ui.PerformLayout();
                GuardController guard = (GuardController)typeof(MainForm).GetField("guard", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(ui);
                Check(guard.Active && guard.PowerApplied && guard.RestartApplied, "Actual application initializes effective protection");
                using (Bitmap image = new Bitmap(ui.Width, ui.Height))
                { ui.DrawToBitmap(image, new Rectangle(0, 0, ui.Width, ui.Height)); image.Save(args[0]); }
                CheckLayout(ui);
                Check(SendMessage(ui.Handle, 0x0011, IntPtr.Zero, IntPtr.Zero) == IntPtr.Zero, "Actual window handler vetoes a synthetic shutdown query");
                Check(SendMessage(ui.Handle, 0x0011, IntPtr.Zero, new IntPtr(0x40000000L)) == new IntPtr(1), "Actual window handler permits a synthetic critical query");
                ui.Hide();
                StringBuilder hiddenReason = new StringBuilder(256); uint hiddenLength = 256;
                Check(guard.Active && NativeMethods.ShutdownBlockReasonQuery(ui.Handle, hiddenReason, ref hiddenLength), "Hidden tray window retains active shutdown registration");
                guard.Stop("Test pause"); Check(!guard.Active && !guard.PowerApplied && !guard.RestartApplied, "Actual UI pause releases native protection");
                typeof(MainForm).GetMethod("ExitApp", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(ui, null);
                Check(ui.IsDisposed, "Exit closes and disposes the actual application window");
            }
            Console.WriteLine("SUCCESS: " + assertions + " assertions. No shutdown or restart was initiated.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
    }

    private static void CheckLayout(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is TableLayoutPanel && child.Dock == DockStyle.Fill) Check(child.Bottom <= parent.ClientSize.Height, "Layout fits " + parent.GetType().Name);
            CheckLayout(child);
        }
    }
}
