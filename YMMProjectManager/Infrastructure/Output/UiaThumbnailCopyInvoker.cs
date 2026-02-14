using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using YMMProjectManager.Infrastructure;

namespace YMMProjectManager.Infrastructure.Output;

public sealed class UiaThumbnailCopyInvoker
{
    private const string CopyButtonLabel = "\u30B5\u30E0\u30CD\u30A4\u30EB\u753B\u50CF\u3092\u30AF\u30EA\u30C3\u30D7\u30DC\u30FC\u30C9\u306B\u51FA\u529B";
    private const string NextEditPointLabel = "\u6B21\u306E\u7DE8\u96C6\u70B9\u3078\u79FB\u52D5";
    private const string NextFrameLabel = "\u6B21\u306E\u30D5\u30EC\u30FC\u30E0\u3078\u79FB\u52D5";
    private const string PrevFrameLabel = "\u524D\u306E\u30D5\u30EC\u30FC\u30E0\u3078\u79FB\u52D5";
    private const string MainWindowTitlePrefix = "\u3086\u3063\u304F\u308AMovieMaker v4";

    private readonly FileLogger logger;
    private readonly object sync = new();
    private AutomationElement? cachedElement;
    private UiaButtonKind? cachedElementKind;

    public UiaThumbnailCopyInvoker(FileLogger logger)
    {
        this.logger = logger;
    }

    public bool InvokeCopyThumbnailToClipboard()
        => TryInvoke(UiaButtonKind.CopyThumbnail, out _);

    public bool InvokeNextEditPoint(out string reason)
        => TryInvoke(UiaButtonKind.NextEditPoint, out reason);

    public bool InvokeNextFrame(out string reason)
        => TryInvoke(UiaButtonKind.NextFrame, out reason);

    public bool InvokePrevFrame(out string reason)
        => TryInvoke(UiaButtonKind.PrevFrame, out reason);

    public bool ClickNextEditPoint(out string reason)
        => TryClick(UiaButtonKind.NextEditPoint, out reason);

    public void InvalidateNudgeCaches()
    {
        InvalidateCache(UiaButtonKind.NextFrame);
        InvalidateCache(UiaButtonKind.PrevFrame);
        InvalidateCache(UiaButtonKind.NextEditPoint);
    }

    private bool TryInvoke(UiaButtonKind kind, out string reason)
    {
        reason = string.Empty;
        if (!TryGetButton(kind, out var button, out reason))
        {
            return false;
        }

        if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out var patternObject)
            || patternObject is not InvokePattern invokePattern)
        {
            reason = "InvokePattern unavailable";
            InvalidateCache(kind);
            return false;
        }

        if (!(bool)button.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty))
        {
            reason = "disabled";
            InvalidateCache(kind);
            return false;
        }

        try
        {
            invokePattern.Invoke();
            return true;
        }
        catch (ElementNotAvailableException)
        {
            InvalidateCache(kind);
            if (!TryGetButton(kind, out var retryButton, out reason))
            {
                return false;
            }

            if (!retryButton.TryGetCurrentPattern(InvokePattern.Pattern, out var retryPatternObject)
                || retryPatternObject is not InvokePattern retryInvokePattern)
            {
                reason = "InvokePattern unavailable after re-find";
                return false;
            }

            retryInvokePattern.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            logger.Error($"UIA invoke failed. kind={kind}", ex);
            logger.Flush();
            return false;
        }
    }

    private bool TryClick(UiaButtonKind kind, out string reason)
    {
        reason = string.Empty;
        if (!TryGetButton(kind, out var button, out reason))
        {
            return false;
        }

        _ = TryBringHostToForeground(button);
        var point = ResolveClickPoint(button);
        if (point is null)
        {
            reason = "no clickable point or bounds";
            return false;
        }

        logger.Info($"Fast: nudge click point x={point.Value.X},y={point.Value.Y}");
        logger.Flush();
        if (!SendLeftClick(point.Value.X, point.Value.Y))
        {
            reason = "SendInput failed";
            return false;
        }

        return true;
    }

    private bool TryGetButton(UiaButtonKind kind, out AutomationElement button, out string reason)
    {
        button = null!;
        reason = string.Empty;

        lock (sync)
        {
            if (cachedElement is not null)
            {
                if (cachedElementKind != kind)
                {
                    cachedElement = null;
                    cachedElementKind = null;
                }
                else
                {
                    try
                    {
                        var enabled = (bool)cachedElement.GetCurrentPropertyValue(AutomationElement.IsEnabledProperty);
                        var offscreen = (bool)cachedElement.GetCurrentPropertyValue(AutomationElement.IsOffscreenProperty);
                        if (enabled && !offscreen)
                        {
                            button = cachedElement;
                            return true;
                        }
                    }
                    catch
                    {
                    }

                    cachedElement = null;
                    cachedElementKind = null;
                }
            }
        }

        if (!TryFindBestButton(kind, out button, out reason))
        {
            return false;
        }

        lock (sync)
        {
            cachedElement = button;
            cachedElementKind = kind;
        }

        return true;
    }

    private void InvalidateCache(UiaButtonKind kind)
    {
        lock (sync)
        {
            if (cachedElementKind == kind)
            {
                cachedElement = null;
                cachedElementKind = null;
            }
        }
    }

    private bool TryFindBestButton(UiaButtonKind kind, out AutomationElement button, out string reason)
    {
        button = null!;
        reason = string.Empty;

        if (!TryFindMainWindow(out var window, out reason))
        {
            return false;
        }

        var label = GetLabel(kind);
        var condition = new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new OrCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, label),
                new PropertyCondition(AutomationElement.NameProperty, label)));

        var candidates = window.FindAll(TreeScope.Descendants, condition);
        logger.Info($"UIA candidate scan kind={kind} count={candidates.Count}");
        logger.Flush();
        if (candidates.Count == 0)
        {
            reason = "button not found";
            return false;
        }

        var bestScore = int.MinValue;
        AutomationElement? best = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var id = c.Current.AutomationId ?? string.Empty;
            var name = c.Current.Name ?? string.Empty;
            var className = c.Current.ClassName ?? string.Empty;
            var rect = c.Current.BoundingRectangle;
            var offscreen = c.Current.IsOffscreen;
            var enabled = c.Current.IsEnabled;
            var runtimeId = string.Join(",", c.GetRuntimeId() ?? []);
            logger.Info($"UIA candidate[{i}] kind={kind} name={name} id={id} class={className} rect={rect} offscreen={offscreen} enabled={enabled} runtimeId={runtimeId}");
            logger.Flush();

            var hasRect = rect.Width > 0 && rect.Height > 0;
            var hasInvoke = c.TryGetCurrentPattern(InvokePattern.Pattern, out _);
            var score = 0;
            if (enabled) score += 4;
            if (!offscreen) score += 4;
            if (hasRect) score += 3;
            if (hasInvoke) score += 2;
            if (string.Equals(id, label, StringComparison.Ordinal)) score += 2;
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        if (best is null)
        {
            reason = "no selectable candidate";
            return false;
        }

        button = best;
        return true;
    }

    private static string GetLabel(UiaButtonKind kind) => kind switch
    {
        UiaButtonKind.CopyThumbnail => CopyButtonLabel,
        UiaButtonKind.NextEditPoint => NextEditPointLabel,
        UiaButtonKind.NextFrame => NextFrameLabel,
        UiaButtonKind.PrevFrame => PrevFrameLabel,
        _ => string.Empty,
    };

    private static bool TryFindMainWindow(out AutomationElement window, out string reason)
    {
        window = null!;
        reason = string.Empty;

        var root = AutomationElement.RootElement;
        if (root is null)
        {
            reason = "RootElement null";
            return false;
        }

        var processId = Process.GetCurrentProcess().Id;
        var topLevelCondition = new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, processId),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));

        var windows = root.FindAll(TreeScope.Children, topLevelCondition);
        foreach (AutomationElement candidate in windows)
        {
            var title = candidate.Current.Name ?? string.Empty;
            if (title.StartsWith(MainWindowTitlePrefix, StringComparison.Ordinal))
            {
                window = candidate;
                return true;
            }
        }

        if (windows.Count > 0)
        {
            window = windows[0];
            return true;
        }

        reason = "main window not found";
        return false;
    }

    private static Point? ResolveClickPoint(AutomationElement element)
    {
        try
        {
            if (element.TryGetClickablePoint(out var clickable))
            {
                return clickable;
            }
        }
        catch
        {
        }

        try
        {
            var rect = element.Current.BoundingRectangle;
            if (rect.Width > 0 && rect.Height > 0)
            {
                return new Point(rect.Left + (rect.Width / 2.0), rect.Top + (rect.Height / 2.0));
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool TryBringHostToForeground(AutomationElement element)
    {
        try
        {
            var handle = new IntPtr(element.Current.NativeWindowHandle);
            return handle != IntPtr.Zero && SetForegroundWindow(handle);
        }
        catch
        {
            return false;
        }
    }

    private static bool SendLeftClick(double x, double y)
    {
        if (!GetCursorPos(out var oldPos))
        {
            return false;
        }

        try
        {
            if (!SetCursorPos((int)Math.Round(x), (int)Math.Round(y)))
            {
                return false;
            }

            var inputs = new[]
            {
                new INPUT
                {
                    type = 0,
                    U = new InputUnion { mi = new MOUSEINPUT { dwFlags = 0x0002 } },
                },
                new INPUT
                {
                    type = 0,
                    U = new InputUnion { mi = new MOUSEINPUT { dwFlags = 0x0004 } },
                },
            };

            var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            return sent == inputs.Length;
        }
        finally
        {
            _ = SetCursorPos(oldPos.X, oldPos.Y);
        }
    }

    private enum UiaButtonKind
    {
        CopyThumbnail,
        NextEditPoint,
        NextFrame,
        PrevFrame,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
