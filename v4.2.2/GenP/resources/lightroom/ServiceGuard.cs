using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

internal static class ServiceGuard
{
    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr handle, StringBuilder text, int capacity);
    private static readonly string Self = Process.GetCurrentProcess().MainModule.FileName;
    private static readonly Regex ErrorCode = new Regex(@"\berror\s*code\s*:\s*12082\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AccountAction = new Regex(@"\b(start|free|begin)\b.{0,30}\btrial\b|\b(subscribe|purchase|buy now)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Quote(string value)
    {
        if (value.IndexOf('"') >= 0 || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0)
            throw new ArgumentException("Invalid command argument");
        return "\"" + value + "\"";
    }

    private static void Log(string path, string message)
    {
        // Never log dialog contents, account identifiers, or authorization data.
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
        if (File.Exists(path) && new FileInfo(path).Length > 262144)
        {
            string previous = path + ".previous";
            if (File.Exists(previous)) File.Delete(previous);
            File.Move(path, previous);
        }
        File.AppendAllText(path, DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine, Encoding.UTF8);
    }

    private static string Title(IntPtr handle)
    {
        var title = new StringBuilder(512);
        GetWindowText(handle, title, title.Capacity);
        return title.ToString();
    }

    private static bool OwnedBy(IntPtr handle, int expectedPid)
    {
        uint actual;
        GetWindowThreadProcessId(handle, out actual);
        return actual == expectedPid && IsWindowVisible(handle);
    }

    private static string ProcessPath(int pid)
    {
        try { using (var process = Process.GetProcessById(pid)) return Path.GetFullPath(process.MainModule.FileName); }
        catch (ArgumentException) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static bool AllowedProcess(int pid, string lightroom, bool fixture)
    {
        string path = ProcessPath(pid);
        if (path == null) return false;
        if (fixture) return String.Equals(path, Self, StringComparison.OrdinalIgnoreCase);
        if (String.Equals(path, lightroom, StringComparison.OrdinalIgnoreCase)) return true;
        string name = Path.GetFileName(path);
        if (!String.Equals(name, "AdobeIPCBroker.exe", StringComparison.OrdinalIgnoreCase) &&
            !String.Equals(name, "Adobe CEF Helper.exe", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (string root in new[] {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Path.GetDirectoryName(lightroom) })
        {
            if (String.IsNullOrEmpty(root)) continue;
            string allowedRoot = String.Equals(root, Path.GetDirectoryName(lightroom), StringComparison.OrdinalIgnoreCase)
                ? root : Path.Combine(root, "Adobe");
            if (path.StartsWith(allowedRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // This method always runs in a disposable worker process. UI Automation providers
    // can hang inside another application; the watcher enforces an external deadline.
    private static int Dismiss(IntPtr handle, int pid, string lightroom, string log, bool fixture)
    {
        if (!OwnedBy(handle, pid) || !String.Equals(Title(handle), "Lightroom Classic", StringComparison.Ordinal) ||
            !AllowedProcess(pid, lightroom, fixture)) return 1;
        var window = AutomationElement.FromHandle(handle);
        var elements = window.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
        if (elements.Count > 800) return 1;
        var text = new StringBuilder();
        var buttons = new List<AutomationElement>();
        for (int i = 0; i < elements.Count; i++)
        {
            var current = elements[i].Current;
            if (current.IsOffscreen) continue;
            text.AppendLine(current.Name ?? "");
            if (text.Length > 32768) return 1;
            if (current.ControlType == ControlType.Button && current.IsEnabled &&
                String.Equals(current.Name, "Dismiss", StringComparison.Ordinal)) buttons.Add(elements[i]);
        }
        string contents = text.ToString();
        if (!ErrorCode.IsMatch(contents) || contents.IndexOf("Sorry, something went wrong", StringComparison.OrdinalIgnoreCase) < 0 ||
            AccountAction.IsMatch(contents) || buttons.Count != 1) return 1;
        object action;
        if (!buttons[0].TryGetCurrentPattern(InvokePattern.Pattern, out action))
        {
            Log(log, "unsupported-dismiss-control pid=" + pid);
            return 2;
        }
        if (!OwnedBy(handle, pid) || !AllowedProcess(pid, lightroom, fixture)) return 1;
        Log(log, "invoke-dismiss code=12082 pid=" + pid);
        ((InvokePattern)action).Invoke();
        for (int attempt = 0; attempt < 10; attempt++)
        {
            Thread.Sleep(100);
            if (!OwnedBy(handle, pid))
            {
                Log(log, "dismiss-confirmed code=12082 pid=" + pid);
                return 0;
            }
        }
        Log(log, "dismiss-not-confirmed code=12082 pid=" + pid);
        return 2;
    }

    private static int Child(string arguments, int timeoutMilliseconds, out bool timedOut)
    {
        timedOut = false;
        using (var process = Process.Start(new ProcessStartInfo(Self, arguments) {
            UseShellExecute = false, CreateNoWindow = true }))
        {
            if (process.WaitForExit(timeoutMilliseconds)) return process.ExitCode;
            timedOut = true;
            try { process.Kill(); } catch (InvalidOperationException) { }
            if (!process.WaitForExit(2000)) throw new TimeoutException("Worker did not terminate");
            return 3;
        }
    }

    private static bool LightroomRunning(string target)
    {
        bool running = false;
        foreach (var process in Process.GetProcessesByName("Lightroom"))
        {
            using (process)
                if (process.SessionId == Process.GetCurrentProcess().SessionId &&
                    String.Equals(ProcessPath(process.Id), target, StringComparison.OrdinalIgnoreCase)) running = true;
        }
        return running;
    }

    private static int Watch(string lightroom, string log, string stopFile, int ownerPid)
    {
        Process owner = null;
        if (ownerPid > 0)
        {
            try
            {
                owner = Process.GetProcessById(ownerPid);
                // Keep a handle to this exact process, including if its PID is reused.
                var ownerHandle = owner.Handle;
                if (owner.SessionId != Process.GetCurrentProcess().SessionId || owner.HasExited) { owner.Dispose(); return 0; }
            }
            catch (ArgumentException) { if (owner != null) owner.Dispose(); return 0; }
        }
        using (owner)
        {
        bool created;
        using (var mutex = new Mutex(true, "Local\\LightroomServiceDialogGuard-" + Process.GetCurrentProcess().SessionId, out created))
        {
            if (!created) return 0;
            try
            {
                Log(log, "watch-start pid=" + Process.GetCurrentProcess().Id);
                var idle = Stopwatch.StartNew();
                bool seenLightroom = false;
                var retryAfter = new Dictionary<string, DateTime>();
                var attempts = new Queue<DateTime>();
                while (!File.Exists(stopFile))
                {
                    bool running = LightroomRunning(lightroom);
                    if (running) { seenLightroom = true; idle.Restart(); }
                    bool ownerAlive = owner != null && !owner.HasExited;
                    if (!running && !ownerAlive && idle.Elapsed.TotalSeconds > (owner != null || seenLightroom ? 10 : 45)) break;
                    var candidates = new List<Tuple<IntPtr, int>>();
                    EnumWindows(delegate(IntPtr handle, IntPtr unused) {
                        if (running && IsWindowVisible(handle) && String.Equals(Title(handle), "Lightroom Classic", StringComparison.Ordinal))
                        {
                            uint pid;
                            GetWindowThreadProcessId(handle, out pid);
                            if (AllowedProcess((int)pid, lightroom, false)) candidates.Add(Tuple.Create(handle, (int)pid));
                        }
                        return true;
                    }, IntPtr.Zero);
                    foreach (var candidate in candidates)
                    {
                        var now = DateTime.UtcNow;
                        while (attempts.Count > 0 && (now - attempts.Peek()).TotalSeconds > 60) attempts.Dequeue();
                        string key = candidate.Item1.ToInt64() + ":" + candidate.Item2;
                        DateTime next;
                        if (attempts.Count >= 3 || (retryAfter.TryGetValue(key, out next) && now < next)) continue;
                        retryAfter[key] = now.AddSeconds(20);
                        attempts.Enqueue(now);
                        bool timeout;
                        int result = Child("--dismiss " + Quote(lightroom) + " " + Quote(log) + " " +
                            candidate.Item1.ToInt64().ToString(CultureInfo.InvariantCulture) + " " + candidate.Item2, 5000, out timeout);
                        Log(log, "worker-result code=" + result + " timeout=" + timeout + " pid=" + candidate.Item2);
                    }
                    // Bound bookkeeping even across long sessions with many transient windows.
                    if (retryAfter.Count > 128) retryAfter.Clear();
                    Thread.Sleep(750);
                }
                Log(log, "watch-stop");
                return 0;
            }
            finally { mutex.ReleaseMutex(); }
        }
        }
    }

    private sealed class QuietForm : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
    }

    private static bool Fixture(string test, string title, string body, string button, bool enabled, bool wrongPid, bool expectClick, string log)
    {
        IntPtr handle = IntPtr.Zero;
        int clicks = 0;
        Exception uiError = null;
        QuietForm form = null;
        using (var ready = new ManualResetEvent(false))
        {
            var ui = new Thread(delegate() {
                try
                {
                    form = new QuietForm { Text = title, Width = 680, Height = 250, ShowInTaskbar = false,
                        StartPosition = FormStartPosition.CenterScreen };
                    var label = new Label { Text = body, Left = 20, Top = 30, Width = 620, Height = 100 };
                    var dismiss = new Button { Text = button, Enabled = enabled, Left = 500, Top = 150, Width = 110 };
                    dismiss.Click += delegate { Interlocked.Increment(ref clicks); form.Close(); };
                    form.Controls.Add(label);
                    form.Controls.Add(dismiss);
                    form.Shown += delegate { handle = form.Handle; ready.Set(); };
                    Application.Run(form);
                    form.Dispose();
                }
                catch (Exception error) { uiError = error; ready.Set(); }
            });
            ui.IsBackground = true;
            ui.SetApartmentState(ApartmentState.STA);
            ui.Start();
            int result = -1;
            bool timeout = false;
            try
            {
                if (!ready.WaitOne(5000) || uiError != null || handle == IntPtr.Zero) throw new Exception("Fixture did not start", uiError);
                result = Child("--fixture-probe " + Quote(Self) + " " + Quote(log) + " " + handle.ToInt64() + " " +
                    (wrongPid ? Process.GetCurrentProcess().Id + 1 : Process.GetCurrentProcess().Id), 5000, out timeout);
            }
            finally
            {
                if (form != null && !form.IsDisposed)
                {
                    try { form.BeginInvoke(new Action(delegate { form.Close(); })); }
                    catch (InvalidOperationException) { }
                }
                ui.Join(3000);
            }
            bool passed = !timeout && (expectClick ? clicks == 1 && result == 0 : clicks == 0 && result == 1);
            Console.WriteLine((passed ? "PASS " : "FAIL ") + test + " clicks=" + clicks + " worker=" + result);
            return passed;
        }
    }

    private static int SelfTest(string log)
    {
        const string body = "Sorry, something went wrong. Check your internet connection. Error code: 12082.";
        int failures = 0;
        if (!Fixture("exact service error", "Lightroom Classic", body, "Dismiss", true, false, true, log)) failures++;
        if (!Fixture("different error", "Lightroom Classic", body.Replace("12082", "12081"), "Dismiss", true, false, false, log)) failures++;
        if (!Fixture("other window title", "Save image", body, "Dismiss", true, false, false, log)) failures++;
        if (!Fixture("account action", "Lightroom Classic", body + " Start your free trial.", "Dismiss", true, false, false, log)) failures++;
        if (!Fixture("other button", "Lightroom Classic", body, "Continue", true, false, false, log)) failures++;
        if (!Fixture("disabled button", "Lightroom Classic", body, "Dismiss", false, false, false, log)) failures++;
        if (!Fixture("wrong owner", "Lightroom Classic", body, "Dismiss", true, true, false, log)) failures++;
        bool timeout;
        int hung = Child("--test-hang", 500, out timeout);
        bool deadline = timeout && hung == 3;
        Console.WriteLine((deadline ? "PASS " : "FAIL ") + "hung worker terminated");
        if (!deadline) failures++;
        Console.WriteLine("Checks=8 Failures=" + failures);
        return failures == 0 ? 0 : 1;
    }

    [MTAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--test-hang") { Thread.Sleep(10000); return 0; }
            if (args.Length == 2 && args[0] == "--self-test") return SelfTest(Path.GetFullPath(args[1]));
            if ((args.Length == 4 || args.Length == 5) && args[0] == "--watch")
                return Watch(Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), Path.GetFullPath(args[3]),
                    args.Length == 5 ? Int32.Parse(args[4], CultureInfo.InvariantCulture) : 0);
            if (args.Length == 5 && (args[0] == "--dismiss" || args[0] == "--fixture-probe"))
                return Dismiss(new IntPtr(Int64.Parse(args[3], CultureInfo.InvariantCulture)), Int32.Parse(args[4], CultureInfo.InvariantCulture),
                    Path.GetFullPath(args[1]), Path.GetFullPath(args[2]), args[0] == "--fixture-probe");
            Console.Error.WriteLine("Expected --watch, --dismiss, or --self-test arguments.");
            return 2;
        }
        catch (Exception error)
        {
            // Provider exception messages can contain window contents; keep output bounded.
            Console.Error.WriteLine("Service guard failed: " + error.GetType().Name);
            return 2;
        }
    }
}
