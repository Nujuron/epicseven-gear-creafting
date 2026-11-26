using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

internal sealed class FocusLostException : InvalidOperationException
{
    public string ForegroundApp { get; private set; }

    public FocusLostException(string foregroundApp)
        : base("Epic Seven lost focus to " + foregroundApp + ".")
    {
        ForegroundApp = foregroundApp;
    }
}

internal sealed class GameWindow
{
    public const int Width = 1496;
    public const int Height = 841;
    private const string ProcessName = "EpicSeven_Steam";
    private readonly IntPtr handle;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref Point point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mouse;
    }

    private GameWindow(IntPtr handle) { this.handle = handle; }

    public static GameWindow Find()
    {
        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            using (process)
            {
                if (process.MainWindowHandle != IntPtr.Zero && process.MainWindowTitle == "Epic Seven")
                    return new GameWindow(process.MainWindowHandle);
            }
        }
        throw new InvalidOperationException("Open Epic Seven in Steam before starting.");
    }

    public static bool EscapeDown { get { return GetAsyncKeyState(0x1B) < 0; } }

    private bool IsForeground { get { return GetForegroundWindow() == handle; } }

    public async Task WaitForForeground(Action waiting, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // The Start or Check button was just pressed, so the app can normally
        // hand foreground ownership to Epic Seven. Windows can decline the request.
        if (IsIconic(handle))
            ShowWindowAsync(handle, 9); // SW_RESTORE
        if (!IsForeground)
            SetForegroundWindow(handle);
        await WaitForFocusReturn(waiting, token);
    }

    public Task WaitForFocusReturn(Action waiting, CancellationToken token)
    {
        return WaitForStableFocus(() => IsWindow(handle), () => IsForeground, waiting, token);
    }

    private void CheckFocus()
    {
        var foreground = GetForegroundWindow();
        if (foreground == handle)
            return;
        if (!IsWindow(handle))
            throw new InvalidOperationException("Epic Seven closed.");
        uint processId;
        if (foreground != IntPtr.Zero && GetWindowThreadProcessId(foreground, out processId) != 0)
        {
            string processName = null;
            try
            {
                using (var process = Process.GetProcessById((int)processId))
                    processName = process.ProcessName + ".exe";
            }
            catch (ArgumentException) { } // The foreground window may have just closed.
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
            if (processName != null)
                throw new FocusLostException(processName);
        }
        throw new FocusLostException("an unidentified window");
    }

    internal static async Task WaitForStableFocus(Func<bool> exists, Func<bool> focused, Action waiting,
        CancellationToken token, int interval = 125)
    {
        bool announced = false;
        int stableChecks = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!exists())
                throw new InvalidOperationException("Epic Seven closed while waiting for its window.");
            if (focused())
            {
                if (++stableChecks == 3)
                    return;
            }
            else
            {
                stableChecks = 0;
                if (!announced)
                {
                    announced = true;
                    waiting();
                }
            }
            await Task.Delay(interval, token);
        }
    }

    public Rectangle Bounds()
    {
        Rect rect;
        var origin = Point.Empty;
        CheckFocus();
        if (!GetClientRect(handle, out rect) || !ClientToScreen(handle, ref origin))
            throw new InvalidOperationException("The Epic Seven window is unavailable.");
        var size = new Size(rect.Right, rect.Bottom);
        if (size.Width < 1200 || Math.Abs((double)size.Width / size.Height - 16.0 / 9) > .01)
            throw new InvalidOperationException("Use a 16:9 game window at least 1200 pixels wide.");
        return new Rectangle(origin, size);
    }

    public Bitmap Capture(out Rectangle bounds)
    {
        bounds = Bounds();
        if (!System.Windows.Forms.SystemInformation.VirtualScreen.Contains(bounds))
            throw new InvalidOperationException("Keep the entire game window on screen.");
        using (var raw = new Bitmap(bounds.Width, bounds.Height))
        {
            using (var g = Graphics.FromImage(raw))
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            if (Bounds() != bounds)
                throw new InvalidOperationException("The game window moved during capture.");
            return new Bitmap(raw, Width, Height);
        }
    }

    public async Task Click(Point target, Rectangle observedBounds, TimeSpan clickDelay, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (EscapeDown)
            throw new OperationCanceledException();
        var bounds = Bounds();
        if (bounds != observedBounds)
            throw new InvalidOperationException("The game window moved after verification.");
        var point = new Point(bounds.X + target.X * bounds.Width / Width,
            bounds.Y + target.Y * bounds.Height / Height);
        if (GetAncestor(WindowFromPoint(point), 2) != handle || !SetCursorPos(point.X, point.Y))
        {
            CheckFocus();
            throw new InvalidOperationException("The game button is covered or unavailable.");
        }
        // Let the game update its hovered control before delivering the button press.
        var minimumHover = TimeSpan.FromMilliseconds(100);
        await Task.Delay(clickDelay > minimumHover ? clickDelay : minimumHover, token);
        if (Bounds() != observedBounds)
            throw new InvalidOperationException("The game window moved after verification.");
        Point cursor;
        if (!GetCursorPos(out cursor) || cursor != point)
            throw new InvalidOperationException("The mouse moved away from the game button. Check the current game screen.");
        if (GetAncestor(WindowFromPoint(point), 2) != handle)
            throw new InvalidOperationException("The game button is covered or unavailable.");
        token.ThrowIfCancellationRequested();
        if (EscapeDown)
            throw new OperationCanceledException();
        if (!SendMouse(0x0002)) // MOUSEEVENTF_LEFTDOWN
            throw new InvalidOperationException("Windows did not deliver the click. Check the current game screen.");
        try
        {
            // The game can miss a press and release delivered in the same frame.
            await Task.Delay(60, token);
        }
        finally
        {
            if (!SendMouse(0x0004)) // MOUSEEVENTF_LEFTUP
                throw new InvalidOperationException("Windows did not release the mouse button. Check the current game screen.");
        }
    }

    private static bool SendMouse(uint flags)
    {
        var input = new[] { new Input { Mouse = new MouseInput { Flags = flags } } };
        return SendInput(1, input, Marshal.SizeOf(typeof(Input))) == 1;
    }
}
