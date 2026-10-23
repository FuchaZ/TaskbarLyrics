using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TaskbarLyrics.Core.Utilities;

namespace TaskbarLyrics.App;

internal readonly record struct EmbeddedTaskbarNativeBounds(int Left, int Top, int Width, int Height);

/// <summary>
/// 一次水平滑动的逐帧定位会话。构造时读入父窗口矩形与窗口物理尺寸，
/// 场景内的每一帧只更新水平位置，因此不需要重复查询窗口矩形，也不会改窗口尺寸。
/// </summary>
internal struct EmbeddedTaskbarHorizontalSlide
{
    /// <remarks>
    /// <c>SWP_ASYNCWINDOWPOS</c> 让调用方立即返回：嵌入模式的窗口是 explorer 进程的子窗口，
    /// 同步等待会把每一帧都拖到 explorer 的消息循环上，这是滑动不够顺的主要原因。
    /// </remarks>
    private const uint MoveOnlyFlags =
        TaskbarNativeMethods.SWP_NOSIZE |
        TaskbarNativeMethods.SWP_NOZORDER |
        TaskbarNativeMethods.SWP_NOACTIVATE |
        TaskbarNativeMethods.SWP_ASYNCWINDOWPOS;

    private readonly IntPtr _windowHandle;
    private readonly double _pixelsPerDip;
    private readonly int _top;
    private readonly int _width;
    private readonly int _height;

    /// <summary>
    /// 亚像素位置。非整数 DPI（如 125%、150%）下若每帧先取整再比较，
    /// 多个插值点会落到同一物理像素而被跳过，动画看起来会一段一段地卡住。
    /// 这里保留亚像素累积，只在真正发出调用时量化。
    /// </summary>
    private double _subpixelLeft;
    private int _lastSentLeft;

    internal EmbeddedTaskbarHorizontalSlide(
        IntPtr windowHandle,
        EmbeddedTaskbarNativeBounds currentBounds,
        double pixelsPerDip)
    {
        _windowHandle = windowHandle;
        _pixelsPerDip = double.IsFinite(pixelsPerDip) && pixelsPerDip > 0 ? pixelsPerDip : 1;
        _top = currentBounds.Top;
        _width = currentBounds.Width;
        _height = currentBounds.Height;
        _subpixelLeft = currentBounds.Left / _pixelsPerDip;
        _lastSentLeft = currentBounds.Left;
    }

    /// <summary>
    /// 移动到指定的任务栏客户区逻辑左缘。量化到物理像素后与上一帧相同就不再调用，
    /// 避免为「这一帧没有位移」白白付出一次跨进程窗口操作。
    /// </summary>
    public bool MoveTo(double clientLeft)
    {
        _subpixelLeft = double.IsFinite(clientLeft) ? clientLeft : _subpixelLeft;
        var targetLeft = (int)Math.Round(_subpixelLeft * _pixelsPerDip, MidpointRounding.AwayFromZero);
        if (targetLeft == _lastSentLeft)
        {
            return true;
        }

        var moved = TaskbarNativeMethods.SetWindowPos(
            _windowHandle,
            IntPtr.Zero,
            targetLeft,
            _top,
            _width,
            _height,
            MoveOnlyFlags);
        if (moved)
        {
            _lastSentLeft = targetLeft;
        }

        return moved;
    }
}

internal readonly record struct EmbeddedTaskbarDisplayTarget(bool UsesPrimaryTaskbar, string? Id)
{
    public static EmbeddedTaskbarDisplayTarget Create(DisplayMonitor? targetDisplay) =>
        new(targetDisplay is null, targetDisplay?.Id);

    public bool Matches(DisplayMonitor? targetDisplay) =>
        UsesPrimaryTaskbar == (targetDisplay is null) &&
        string.Equals(Id, targetDisplay?.Id, StringComparison.OrdinalIgnoreCase);
}

internal enum EmbeddedTaskbarAttachResult
{
    Unavailable,
    Attached,
    AttachedPositionPending
}

internal static class EmbeddedTaskbarEmbeddingPolicy
{
    public static EmbeddedTaskbarAttachResult FromPositionResult(bool positioned) =>
        positioned
            ? EmbeddedTaskbarAttachResult.Attached
            : EmbeddedTaskbarAttachResult.AttachedPositionPending;

    public static bool ShouldKeepEmbedded(EmbeddedTaskbarAttachResult result) =>
        result is EmbeddedTaskbarAttachResult.Attached or EmbeddedTaskbarAttachResult.AttachedPositionPending;

    public static bool ShouldKeepExistingAttachment(
        bool sameWindow,
        bool sameTargetDisplay,
        bool parentIsValid) =>
        sameWindow && sameTargetDisplay && parentIsValid;

    // A re-parent of an established embedding to another taskbar cannot revive the
    // invalidated DWM composition surface of the same HWND (verified with native
    // probes), so the host must replace the window instead of re-parenting in place.
    public static bool RequiresWindowReplacement(
        EmbeddedTaskbarDisplayTarget? attachedTarget,
        DisplayMonitor? targetDisplay) =>
        attachedTarget is { } attached && !attached.Matches(targetDisplay);
}

internal static class EmbeddedTaskbarLayoutCalculator
{
    public static long CalculateIntersectionArea(NativeRect first, NativeRect second)
    {
        var width = Math.Max(0, Math.Min(first.Right, second.Right) - Math.Max(first.Left, second.Left));
        var height = Math.Max(0, Math.Min(first.Bottom, second.Bottom) - Math.Max(first.Top, second.Top));
        return (long)width * height;
    }

    /// <summary>
    /// 任务栏客户区内的水平位置。<paramref name="anchors"/> 需已换算为任务栏客户区逻辑像素，
    /// 只有 <see cref="LyricsHorizontalAnchor.AutoFollowIcons"/> 会用到它。
    /// </summary>
    public static double CalculateHorizontalLeft(
        double taskbarWidth,
        double windowWidth,
        LyricsHorizontalAnchor anchor,
        double offset,
        TaskbarHorizontalAnchors anchors = default)
    {
        return anchor switch
        {
            LyricsHorizontalAnchor.Left => offset,
            LyricsHorizontalAnchor.Center => (taskbarWidth - windowWidth) / 2.0 + offset,
            LyricsHorizontalAnchor.AutoFollowIcons => CalculateAutoFollowLeft(taskbarWidth, windowWidth, offset, anchors),
            _ => taskbarWidth - windowWidth + offset
        };
    }

    /// <summary>
    /// 自动跟随锚点的目标左缘：紧贴左侧图标组右缘并避让系统托盘，最后夹在容器范围内；
    /// 图标边界探测不到时退回容器的左侧锚点行为。
    /// </summary>
    /// <param name="containerLeft">容器（任务栏）左缘，与 <paramref name="anchors"/> 同坐标系。</param>
    /// <param name="containerRight">容器（任务栏）右缘，与 <paramref name="anchors"/> 同坐标系。</param>
    /// <remarks>
    /// 夹紧只在这里做一次：调用方（<see cref="TaskbarPlacementService"/> 与
    /// <see cref="EmbeddedTaskbarAnchor"/>）不应再用另一份窗口宽度重新夹紧，
    /// 否则两处宽度不一致时会悄悄吃掉 X 偏移。垂直方向同样不使用 <see cref="ClampHorizontalLeft"/>。
    /// </remarks>
    public static double CalculateAutoFollowLeft(
        double containerLeft,
        double containerRight,
        double windowWidth,
        double xOffset,
        TaskbarHorizontalAnchors anchors)
    {
        var left = anchors.LeftIconsRight is { } iconsRight
            ? iconsRight + IconGap + xOffset
            : containerLeft + xOffset;
        if (anchors.TrayLeft is { } trayLeft)
        {
            left = Math.Min(left, trayLeft - windowWidth - IconGap);
        }

        var maximumLeft = Math.Max(containerLeft, containerRight - windowWidth);
        return Math.Clamp(left, containerLeft, maximumLeft);
    }

    /// <summary>歌词窗口与左侧图标组、系统托盘之间保留的间隙（逻辑像素）。</summary>
    public const double IconGap = 8;

    /// <summary>
    /// 任务栏客户区内的自动跟随位置；容器左缘就是客户区原点，因此容器跨度是 [0, taskbarWidth]。
    /// </summary>
    private static double CalculateAutoFollowLeft(
        double taskbarWidth,
        double windowWidth,
        double offset,
        TaskbarHorizontalAnchors anchors) =>
        CalculateAutoFollowLeft(0, taskbarWidth, windowWidth, offset, anchors);

    public static double CalculateVerticalTop(double taskbarHeight, double windowHeight, double offset, bool alignTop = false)
    {
        return alignTop ? offset : (taskbarHeight - windowHeight) / 2.0 + offset;
    }

    public static double ClampHorizontalLeft(
        double left,
        double taskbarWidth,
        double windowWidth)
    {
        var maximumLeft = Math.Max(0, taskbarWidth - windowWidth);
        return Math.Clamp(left, 0, maximumLeft);
    }

    public static double ClampVerticalTop(
        double top,
        double taskbarHeight,
        double windowHeight)
    {
        var maximumTop = Math.Max(0, taskbarHeight - windowHeight);
        return Math.Clamp(top, 0, maximumTop);
    }

    public static EmbeddedTaskbarNativeBounds ToTaskbarClientBounds(
        double clientLeft,
        double clientTop,
        double width,
        double height,
        double pixelsPerDip)
    {
        pixelsPerDip = double.IsFinite(pixelsPerDip) && pixelsPerDip > 0 ? pixelsPerDip : 1;
        return new EmbeddedTaskbarNativeBounds(
            AlignPhysicalPixel(clientLeft, pixelsPerDip),
            AlignPhysicalPixel(clientTop, pixelsPerDip),
            Math.Max(1, AlignPhysicalPixel(width, pixelsPerDip)),
            Math.Max(1, AlignPhysicalPixel(height, pixelsPerDip)));
    }

    public static bool NeedsNativeBoundsUpdate(
        EmbeddedTaskbarNativeBounds? previousBounds,
        EmbeddedTaskbarNativeBounds targetBounds) =>
        previousBounds is null || previousBounds.Value != targetBounds;

    private static int AlignPhysicalPixel(double value, double pixelsPerDip) =>
        checked((int)Math.Round(value * pixelsPerDip, MidpointRounding.AwayFromZero));
}

internal sealed class EmbeddedTaskbarAnchor : IDisposable
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const uint GetAncestorParent = 1;
    private const long WsPopup = 0x80000000L;
    private const long WsChild = 0x40000000L;
    private const long WsVisible = 0x10000000L;
    private const long WsExNoActivate = 0x08000000L;
    private const long WsExToolWindow = 0x00000080L;

    private IntPtr _windowHandle;
    private IntPtr _taskbarHandle;
    private IntPtr _parentHandle;
    private IntPtr _taskListHandle;
    private EmbeddedTaskbarNativeBounds _originalTaskListBounds;
    private EmbeddedTaskbarNativeBounds? _lastTaskListBounds;
    private EmbeddedTaskbarDisplayTarget? _attachedDisplayTarget;
    private long _originalStyle;
    private long _originalExtendedStyle;
    private bool _hasOriginalStyles;
    private bool _taskListSqueezed;
    private bool _disposed;
    private string? _lastAttachDiagnostic;
    private readonly TaskbarOccupiedSpaceProvider _occupiedSpace;
    private DispatcherTimer? _spaceRefreshTimer;
    private Window? _positionWindow;
    private AppSettings? _positionSettings;
    private DisplayMonitor? _positionDisplay;
    private string? _lastSpaceDiagnostic;
    private string? _lastAnchorsDiagnostic;

    public EmbeddedTaskbarAnchor() : this(new TaskbarOccupiedSpaceProvider()) { }

    internal EmbeddedTaskbarAnchor(TaskbarOccupiedSpaceProvider occupiedSpace)
    {
        _occupiedSpace = occupiedSpace;
        _occupiedSpace.SnapshotAvailable += OnSnapshotAvailable;
    }

    public bool IsAttached => _windowHandle != IntPtr.Zero && _parentHandle != IntPtr.Zero;

    public bool IsSurfaceHidden { get; private set; }
    public event EventHandler? SurfaceHidden;

    public bool IsAttachedToDifferentTarget(DisplayMonitor? targetDisplay) =>
        EmbeddedTaskbarEmbeddingPolicy.RequiresWindowReplacement(_attachedDisplayTarget, targetDisplay);

    public EmbeddedTaskbarAttachResult Attach(Window window, AppSettings settings, DisplayMonitor? targetDisplay)
    {
        if (_disposed)
        {
            return EmbeddedTaskbarAttachResult.Unavailable;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            ReportAttachOutcome(
                EmbeddedTaskbarAttachResult.Unavailable,
                $"NoWindowHandle Target={DescribeTarget(targetDisplay)}");
            return EmbeddedTaskbarAttachResult.Unavailable;
        }

        // ForceAlwaysOnTop is a floating-window preference. A taskbar child must
        // never retain a stale topmost state after switching from floating mode.
        window.Topmost = false;

        if (HasAttachmentForDifferentTarget(hwnd, targetDisplay))
        {
            Detach();
        }

        var taskbar = TaskbarWindowLocator.FindTaskbar(targetDisplay, out var taskbarDiagnostic);
        if (taskbar == IntPtr.Zero)
        {
            var keptAttachment = KeepExistingAttachmentIfValid(window, hwnd, settings, targetDisplay);
            ReportAttachOutcome(keptAttachment, $"NoTaskbarForTarget {taskbarDiagnostic}");
            return keptAttachment;
        }

        if (_windowHandle != hwnd || _taskbarHandle != taskbar)
        {
            Detach();
            _windowHandle = hwnd;
            _taskbarHandle = taskbar;
            CaptureOriginalStyles(hwnd);
        }

        var isWindows11 = IsWindows11();
        var parent = isWindows11 ? taskbar : FindTaskbarContainer(taskbar);
        if (parent == IntPtr.Zero)
        {
            var keptAttachment = KeepExistingAttachmentIfValid(window, hwnd, settings, targetDisplay);
            ReportAttachOutcome(keptAttachment, "NoTaskbarContainer");
            return keptAttachment;
        }

        if (_parentHandle != parent || GetAncestor(hwnd, GetAncestorParent) != parent)
        {
            RestoreTaskList();
            _taskListHandle = IntPtr.Zero;
            _originalTaskListBounds = default;

            _ = SetParent(hwnd, parent);
            ApplyChildWindowStyle(hwnd);
            if (GetAncestor(hwnd, GetAncestorParent) != parent)
            {
                ReportAttachOutcome(EmbeddedTaskbarAttachResult.Unavailable, "ReparentRejected");
                return EmbeddedTaskbarAttachResult.Unavailable;
            }

            _parentHandle = parent;
        }

        _attachedDisplayTarget = EmbeddedTaskbarDisplayTarget.Create(targetDisplay);

        // 刷新循环不再区分系统版本：自动跟随的水平动画与占用快照刷新都由它驱动，
        // Win10 上 Position 内部会按需退回同步探测。
        MonitorAvailableSpace(window, settings, targetDisplay);

        if (!isWindows11)
        {
            SqueezeTaskList(parent, settings);
        }

        var attachResult = EmbeddedTaskbarEmbeddingPolicy.FromPositionResult(
            Position(hwnd, parent, window, settings, targetDisplay));
        ReportAttachOutcome(attachResult, $"Positioned Target={DescribeTarget(targetDisplay)}");
        return attachResult;
    }

    public void Detach()
    {
        IsSurfaceHidden = false;
        StopHorizontalSlide();
        _horizontalTargetLeft = double.NaN;
        _spaceRefreshTimer?.Stop();
        _occupiedSpace.Invalidate();
        _positionWindow = null;
        _positionSettings = null;
        _positionDisplay = null;
        RestoreTaskList();

        if (_windowHandle != IntPtr.Zero && IsWindow(_windowHandle))
        {
            _ = SetParent(_windowHandle, IntPtr.Zero);
            RestoreTopLevelStyles(_windowHandle);
        }

        _windowHandle = IntPtr.Zero;
        _taskbarHandle = IntPtr.Zero;
        _parentHandle = IntPtr.Zero;
        _taskListHandle = IntPtr.Zero;
        _originalTaskListBounds = default;
        _lastTaskListBounds = null;
        _attachedDisplayTarget = null;
        _hasOriginalStyles = false;
        _taskListSqueezed = false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _occupiedSpace.SnapshotAvailable -= OnSnapshotAvailable;
        Detach();
        if (_spaceRefreshTimer is not null) _spaceRefreshTimer.Tick -= OnSpaceRefreshTick;
        _occupiedSpace.Dispose();
        _disposed = true;
    }

    private void MonitorAvailableSpace(Window window, AppSettings settings, DisplayMonitor? display)
    {
        _positionWindow = window;
        _positionSettings = settings;
        _positionDisplay = display;
        if (_spaceRefreshTimer is null)
        {
            _spaceRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _spaceRefreshTimer.Tick += OnSpaceRefreshTick;
        }
        _spaceRefreshTimer.Start();
    }

    private void OnSpaceRefreshTick(object? sender, EventArgs e) => RefreshAvailableSpace();

    // The reader publishes from a worker. Apply fresh geometry on the owning
    // dispatcher instead of keeping it unused until the next 500 ms timer tick.
    private void OnSnapshotAvailable(object? sender, EventArgs e)
    {
        var dispatcher = Volatile.Read(ref _positionWindow)?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
        dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(RefreshAvailableSpace));
    }

    private void RefreshAvailableSpace()
    {
        if (!_disposed && _positionWindow is { IsVisible: true } window && _positionSettings is { } settings &&
            _windowHandle != IntPtr.Zero && _parentHandle != IntPtr.Zero && IsWindow(_parentHandle))
            Position(_windowHandle, _parentHandle, window, settings, _positionDisplay);
    }

    internal bool Position(
        IntPtr hwnd,
        IntPtr parent,
        Window window,
        AppSettings settings,
        DisplayMonitor? targetDisplay)
    {
        if (!TaskbarNativeMethods.GetWindowRect(parent, out var parentRect))
        {
            return false;
        }

        var pixelsPerDip = targetDisplay?.PixelsPerDip ?? TaskbarPlacementService.GetPixelsPerDip(window);
        var taskbarWidth = (parentRect.Right - parentRect.Left) / pixelsPerDip;
        var taskbarHeight = (parentRect.Bottom - parentRect.Top) / pixelsPerDip;
        var width = AppSettings.ClampEffectiveWindowWidth(
            settings.WindowWidth,
            taskbarWidth);
        var metrics = LyricsLayoutMetrics.Create(settings, pixelsPerDip, new TaskbarBounds(0, 0, taskbarWidth, taskbarHeight));
        if (metrics.IsVertical) width = taskbarWidth;
        var height = metrics.DesiredWindowHeight;
        height = Math.Min(height, taskbarHeight);

        var anchors = ResolveHorizontalAnchors(parentRect, pixelsPerDip, settings.HorizontalAnchor, metrics.IsVertical);
        var clientLeft = EmbeddedTaskbarLayoutCalculator.CalculateHorizontalLeft(
            taskbarWidth, width, settings.HorizontalAnchor, settings.XOffset, anchors);
        var clientTop = EmbeddedTaskbarLayoutCalculator.CalculateVerticalTop(
            taskbarHeight, height, settings.YOffset, alignTop: metrics.IsVertical);
        if (settings.HorizontalAnchor == LyricsHorizontalAnchor.AutoFollowIcons)
        {
            // 自动跟随保持固定宽度：目标位置已由 CalculateAutoFollowLeft 夹在图标与托盘之间，
            // 不参与 TryPlace 的「压缩到空隙长度」与无空闲时的整体隐藏。
        }
        else if (IsWindows11())
        {
            var occupied = _occupiedSpace.GetOccupiedBounds(parent, new Rect(
                parentRect.Left, parentRect.Top,
                parentRect.Right - parentRect.Left, parentRect.Bottom - parentRect.Top));
            var logical = occupied is null ? null : TaskbarOccupiedSpaceProvider.ToLogicalBounds(
                occupied, metrics.IsVertical ? parentRect.Top : parentRect.Left, pixelsPerDip);
            if (logical is null || !TaskbarFreeSpaceLayout.TryPlace(
                    metrics.IsVertical ? taskbarHeight : taskbarWidth,
                    metrics.IsVertical ? height : width,
                    metrics.IsVertical ? clientTop : clientLeft,
                    logical, out var available))
            {
                ReportSpaceDiagnostic(logical is null ? "WaitingForTaskbarBounds" : "NoFreeSpace");
                if (!IsSurfaceHidden)
                {
                    IsSurfaceHidden = true;
                    SurfaceHidden?.Invoke(this, EventArgs.Empty);
                }
                var current = GetCurrentWindowBounds(hwnd, parentRect);
                // Keep the logical visibility and parent: zero native area cannot
                // obscure buttons and can recover without re-parenting the HWND.
                StopHorizontalSlide();
                _horizontalTargetLeft = double.NaN;
                return current is { Width: 0, Height: 0 } || TaskbarNativeMethods.SetWindowPos(
                    hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    TaskbarNativeMethods.SWP_NOMOVE | TaskbarNativeMethods.SWP_NOZORDER |
                    TaskbarNativeMethods.SWP_NOACTIVATE);
            }
            if (metrics.IsVertical)
            {
                clientTop = available.Start;
                height = available.Length;
            }
            else
            {
                clientLeft = available.Start;
                width = available.Length;
            }
            ReportSpaceDiagnostic($"Left={clientLeft:F1} Top={clientTop:F1} Width={width:F1} Height={height:F1} Scale={pixelsPerDip:F2}");
        }

        IsSurfaceHidden = false;
        if (window.Width != width)
        {
            window.Width = width;
        }

        if (window.Height != height)
        {
            window.Height = height;
        }

        clientLeft = EmbeddedTaskbarLayoutCalculator.ClampHorizontalLeft(
            clientLeft,
            taskbarWidth,
            width);
        clientTop = EmbeddedTaskbarLayoutCalculator.ClampVerticalTop(
            clientTop,
            taskbarHeight,
            height);

        var bounds = EmbeddedTaskbarLayoutCalculator.ToTaskbarClientBounds(
            clientLeft,
            clientTop,
            width,
            height,
            pixelsPerDip);

        // WPF Window.Left/Top enforce top-level screen semantics: after any native
        // move WPF re-syncs them from the child's screen rectangle, and writing them
        // back applies the mixed-in screen coordinates as parent-client coordinates.
        // That feedback breaks every taskbar whose screen origin is not (0, 0), so
        // the embedded position is owned exclusively by the native SetWindowPos below
        // and is verified against the live window rectangle: external moves (WPF
        // size applies, DPI changes) are corrected on the next anchor pass.
        var currentBounds = GetCurrentWindowBounds(hwnd, parentRect);
        if (!EmbeddedTaskbarLayoutCalculator.NeedsNativeBoundsUpdate(currentBounds, bounds))
        {
            return true;
        }

        // 尺寸或垂直位置变化必须整体立即生效：滑动会话缓存了窗口尺寸，
        // 带着旧尺寸滑完再补一次变尺寸会出现明显的二次跳动。
        var sizeOrTopChanged = currentBounds is null ||
            currentBounds.Value.Width != bounds.Width ||
            currentBounds.Value.Height != bounds.Height ||
            currentBounds.Value.Top != bounds.Top;
        if (sizeOrTopChanged || IsSurfaceHidden || !window.IsVisible)
        {
            StopHorizontalSlide();
            _horizontalTargetLeft = clientLeft;
            return MoveWindowNative(hwnd, bounds);
        }

        if (!double.IsFinite(_horizontalTargetLeft))
        {
            // 附着后的首次落位不做过渡；之后由 500ms 刷新与快照事件驱动的位置变化才滑动。
            _horizontalTargetLeft = clientLeft;
            StopHorizontalSlide();
            return MoveWindowNative(hwnd, bounds);
        }

        if (Math.Abs(clientLeft - _horizontalTargetLeft) <= HorizontalFollowThreshold)
        {
            if (_isHorizontalSlideRunning)
            {
                // 定时器与快照事件重复触发同一目标：让进行中的滑动继续，不要重启。
                return true;
            }

            // 目标没变而原生位置偏离（WPF 应用尺寸、外部移动）：立即校正到目标。
            return MoveWindowNative(hwnd, bounds);
        }

        // 目标真的变了：从当前插值位置出发滑向新目标，避免先跳回旧目标再出发的回弹。
        BeginHorizontalSlide(GetCurrentSlideLeft(_horizontalTargetLeft), clientLeft);
        _horizontalTargetLeft = clientLeft;
        return true;
    }

    private static bool MoveWindowNative(
        IntPtr hwnd,
        EmbeddedTaskbarNativeBounds bounds) =>
        TaskbarNativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            TaskbarNativeMethods.SWP_NOZORDER | TaskbarNativeMethods.SWP_NOACTIVATE);

    private void ReportSpaceDiagnostic(string message)
    {
        if (_lastSpaceDiagnostic == message) return;
        _lastSpaceDiagnostic = message;
        Log.Diagnostic("TASKBAR_SPACE", message);
    }

    // ---- 水平滑动过渡（自 MainWindow 的渲染帧驱动动画移植）----
    // 上游的避让是瞬时落位；这里把「只有水平位置变化」的场景改为逐帧滑动，
    // 让图标增减、避让重摆都带过渡。尺寸与垂直变化仍然立即生效。

    /// <summary>目标位置变化小于该阈值（逻辑像素）时不触发新的滑动。</summary>
    private const double HorizontalFollowThreshold = 0.5;

    /// <summary>窗口当前正在前往（或已停在）的水平目标，逻辑像素；未定位时为 NaN。</summary>
    private double _horizontalTargetLeft = double.NaN;
    private double _slideFromLeft;
    private double _slideToLeft;
    private long _slideStartedAt;
    private EmbeddedTaskbarHorizontalSlide? _slideSession;
    private bool _isHorizontalSlideRunning;

    /// <summary>
    /// 取当前插值位置：动画进行中沿同一缓动曲线推算，未动画时即为 <paramref name="settledLeft"/>。
    /// 刻意不读窗口原生矩形——500ms 一次的复检不值得为它多做一次跨进程坐标查询。
    /// </summary>
    private double GetCurrentSlideLeft(double settledLeft)
    {
        if (!_isHorizontalSlideRunning)
        {
            return settledLeft;
        }

        var progress = Math.Clamp(
            Stopwatch.GetElapsedTime(_slideStartedAt).TotalMilliseconds / HorizontalSlideEasing.DurationMilliseconds,
            0,
            1);
        if (progress >= 1)
        {
            return _slideToLeft;
        }

        return _slideFromLeft + ((_slideToLeft - _slideFromLeft) * HorizontalSlideEasing.Evaluate(progress));
    }

    private void BeginHorizontalSlide(double fromLeft, double toLeft)
    {
        _slideFromLeft = fromLeft;
        _slideToLeft = toLeft;
        _slideStartedAt = Stopwatch.GetTimestamp();
        _slideSession = null;
        MoveSlideSession(fromLeft);

        if (_isHorizontalSlideRunning)
        {
            return;
        }

        _isHorizontalSlideRunning = true;
        // CompositionTarget.Rendering 由渲染管线驱动，每显示帧触发一次并与垂直同步对齐，
        // 因此不会像固定间隔定时器那样丢帧或补帧。
        CompositionTarget.Rendering += OnHorizontalSlideFrame;
    }

    private void OnHorizontalSlideFrame(object? sender, EventArgs e)
    {
        var progress = Math.Clamp(
            Stopwatch.GetElapsedTime(_slideStartedAt).TotalMilliseconds / HorizontalSlideEasing.DurationMilliseconds,
            0,
            1);
        if (progress >= 1)
        {
            MoveSlideSession(_slideToLeft);
            StopHorizontalSlide();
            return;
        }

        MoveSlideSession(_slideFromLeft + ((_slideToLeft - _slideFromLeft) * HorizontalSlideEasing.Evaluate(progress)));

        // 歌词窗口静止时内容不变，WPF 可能整帧跳过渲染而不再触发 Rendering；
        // 让主窗口持续产生脏区，保证动画期间每帧都有回调。
        (_positionWindow as MainWindow)?.SustainRenderLoop();
    }

    private void MoveSlideSession(double clientLeft)
    {
        if (_slideSession is null)
        {
            if (!TryCreateSlideSession(out var created, out _))
            {
                // 窗口暂不可用（正在重锚等）：本帧跳过，下一帧重试或由整体校正收尾。
                return;
            }

            _slideSession = created;
        }

        // 每帧只改水平位置：会话已缓存父窗口矩形与窗口尺寸，一帧只有一次跨进程调用，
        // 且不联动窗口尺寸，因此不会触发 WPF 重新布局或 WebView2 重排。
        if (!_slideSession!.Value.MoveTo(clientLeft))
        {
            // SetWindowPos 失败（窗口正在销毁等）：放弃本次动画，下一次 Position 整体校正。
            StopHorizontalSlide();
        }
    }

    /// <summary>
    /// 开始一次滑动前读取窗口矩形与 DPI 建立会话；之后每帧只发一个
    /// <c>SetWindowPos(SWP_NOSIZE | SWP_ASYNCWINDOWPOS)</c>。
    /// </summary>
    private bool TryCreateSlideSession(out EmbeddedTaskbarHorizontalSlide session, out double currentClientLeft)
    {
        session = default;
        currentClientLeft = 0;
        if (!IsAttached ||
            !TaskbarNativeMethods.GetWindowRect(_windowHandle, out var windowRect) ||
            !TaskbarNativeMethods.GetWindowRect(_parentHandle, out var parentRect))
        {
            return false;
        }

        var pixelsPerDip = _positionDisplay?.PixelsPerDip ?? GetPixelsPerDipForHandle(_parentHandle);
        var bounds = new EmbeddedTaskbarNativeBounds(
            windowRect.Left - parentRect.Left,
            windowRect.Top - parentRect.Top,
            windowRect.Right - windowRect.Left,
            windowRect.Bottom - windowRect.Top);
        session = new EmbeddedTaskbarHorizontalSlide(_windowHandle, bounds, pixelsPerDip);
        currentClientLeft = bounds.Left / pixelsPerDip;
        return true;
    }

    private void StopHorizontalSlide()
    {
        if (_isHorizontalSlideRunning)
        {
            CompositionTarget.Rendering -= OnHorizontalSlideFrame;
            _isHorizontalSlideRunning = false;
        }

        _slideSession = null;
    }

    /// <summary>
    /// 自动跟随锚点所需的图标区边界（任务栏客户区逻辑像素）。
    /// Windows 11 优先复用占用空间快照推导（空闲时零额外 UIA 查询），推导不出或非 Win11
    /// 时退回同步探测；其余锚点与竖装任务栏不做探测。
    /// </summary>
    private TaskbarHorizontalAnchors ResolveHorizontalAnchors(
        TaskbarNativeMethods.NativeRect parentRect,
        double pixelsPerDip,
        LyricsHorizontalAnchor anchor,
        bool isVertical)
    {
        if (anchor != LyricsHorizontalAnchor.AutoFollowIcons || isVertical)
        {
            return TaskbarHorizontalAnchors.Unavailable;
        }

        if (IsWindows11())
        {
            var occupied = _occupiedSpace.GetOccupiedBounds(_parentHandle, new Rect(
                parentRect.Left, parentRect.Top,
                parentRect.Right - parentRect.Left, parentRect.Bottom - parentRect.Top));
            if (occupied is not null)
            {
                var logical = TaskbarOccupiedSpaceProvider.ToLogicalBounds(
                    occupied, parentRect.Left, pixelsPerDip);
                var derived = TaskbarHorizontalAnchors.FromOccupiedIntervals(logical);
                if (derived.LeftIconsRight is not null)
                {
                    ReportAnchorsDiagnostic(
                        $"raw=[{FormatIntervals(occupied)}] scale={pixelsPerDip:F2} " +
                        $"derived=({derived.LeftIconsRight:F1},{derived.TrayLeft:F1})");
                    return derived;
                }

                ReportAnchorsDiagnostic($"clustering unavailable, raw=[{FormatIntervals(occupied)}] scale={pixelsPerDip:F2}");
            }
            else
            {
                ReportAnchorsDiagnostic("occupied snapshot unavailable");
            }
        }

        var probe = TaskbarIconRegionProbe.GetRegion(_taskbarHandle).ToLogical(pixelsPerDip, parentRect.Left);
        ReportAnchorsDiagnostic($"probe=({probe.LeftIconsRight:F1},{probe.TrayLeft:F1}) scale={pixelsPerDip:F2}");
        return probe;
    }

    private static string FormatIntervals(IReadOnlyList<TaskbarInterval> intervals) =>
        string.Join(", ", intervals.Select(interval => $"{interval.Start:F0}-{interval.End:F0}"));

    private void ReportAnchorsDiagnostic(string message)
    {
        if (_lastAnchorsDiagnostic == message) return;
        _lastAnchorsDiagnostic = message;
        Log.Diagnostic("TASKBAR_ANCHORS", message);
    }

    private static EmbeddedTaskbarNativeBounds? GetCurrentWindowBounds(
        IntPtr hwnd,
        TaskbarNativeMethods.NativeRect parentRect)
    {
        if (!TaskbarNativeMethods.GetWindowRect(hwnd, out var windowRect))
        {
            return null;
        }

        return new EmbeddedTaskbarNativeBounds(
            windowRect.Left - parentRect.Left,
            windowRect.Top - parentRect.Top,
            windowRect.Right - windowRect.Left,
            windowRect.Bottom - windowRect.Top);
    }

    private void SqueezeTaskList(IntPtr parent, AppSettings settings)
    {
        if (_taskListHandle == IntPtr.Zero || !IsWindow(_taskListHandle))
        {
            var previousTaskListHandle = _taskListHandle;
            _taskListHandle = FindTaskList(parent);
            if (_taskListHandle != previousTaskListHandle)
            {
                _originalTaskListBounds = default;
                _lastTaskListBounds = null;
                _taskListSqueezed = false;
            }
        }

        if (_taskListHandle == IntPtr.Zero ||
            !TaskbarNativeMethods.GetWindowRect(_taskListHandle, out var taskListRect) ||
            !TaskbarNativeMethods.GetWindowRect(parent, out var parentRect))
        {
            return;
        }

        if (!_taskListSqueezed)
        {
            _originalTaskListBounds = new EmbeddedTaskbarNativeBounds(
                taskListRect.Left - parentRect.Left,
                taskListRect.Top - parentRect.Top,
                taskListRect.Right - taskListRect.Left,
                taskListRect.Bottom - taskListRect.Top);
        }

        var pixelsPerDip = GetPixelsPerDipForHandle(parent);
        var reservedWidth = (int)Math.Round(
            (AppSettings.ClampEffectiveWindowWidth(
                settings.WindowWidth,
                (parentRect.Right - parentRect.Left) / pixelsPerDip) +
                Math.Max(0, settings.XOffset)) * pixelsPerDip,
            MidpointRounding.AwayFromZero);
        var targetWidth = Math.Clamp(
            _originalTaskListBounds.Width - reservedWidth,
            0,
            _originalTaskListBounds.Width);

        var targetBounds = new EmbeddedTaskbarNativeBounds(
            _originalTaskListBounds.Left,
            _originalTaskListBounds.Top,
            targetWidth,
            _originalTaskListBounds.Height);
        if (!EmbeddedTaskbarLayoutCalculator.NeedsNativeBoundsUpdate(_lastTaskListBounds, targetBounds))
        {
            _taskListSqueezed = true;
            return;
        }

        var squeezed = MoveWindow(
            _taskListHandle,
            targetBounds.Left,
            targetBounds.Top,
            targetBounds.Width,
            targetBounds.Height,
            true);
        if (squeezed)
        {
            _lastTaskListBounds = targetBounds;
            _taskListSqueezed = true;
        }
    }

    private void RestoreTaskList()
    {
        if (_taskListSqueezed && _taskListHandle != IntPtr.Zero && IsWindow(_taskListHandle))
        {
            _ = MoveWindow(
                _taskListHandle,
                _originalTaskListBounds.Left,
                _originalTaskListBounds.Top,
                _originalTaskListBounds.Width,
                _originalTaskListBounds.Height,
                true);
        }

        _taskListSqueezed = false;
        _lastTaskListBounds = null;
    }

    private void CaptureOriginalStyles(IntPtr hwnd)
    {
        _originalStyle = TaskbarNativeMethods.GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        _originalExtendedStyle = TaskbarNativeMethods.GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        _hasOriginalStyles = true;
    }

    private EmbeddedTaskbarAttachResult KeepExistingAttachmentIfValid(
        Window window,
        IntPtr hwnd,
        AppSettings settings,
        DisplayMonitor? targetDisplay)
    {
        var parentIsValid = _taskbarHandle != IntPtr.Zero &&
            IsWindow(_taskbarHandle) &&
            _parentHandle != IntPtr.Zero &&
            IsWindow(_parentHandle) &&
            GetAncestor(hwnd, GetAncestorParent) == _parentHandle;
        if (!EmbeddedTaskbarEmbeddingPolicy.ShouldKeepExistingAttachment(
                _windowHandle == hwnd,
                IsSameTargetDisplay(targetDisplay),
                parentIsValid))
        {
            return EmbeddedTaskbarAttachResult.Unavailable;
        }

        if (!IsWindows11())
        {
            SqueezeTaskList(_parentHandle, settings);
        }

        return EmbeddedTaskbarEmbeddingPolicy.FromPositionResult(
            Position(hwnd, _parentHandle, window, settings, targetDisplay));
    }

    private bool HasAttachmentForDifferentTarget(IntPtr hwnd, DisplayMonitor? targetDisplay) =>
        _windowHandle == hwnd &&
        _parentHandle != IntPtr.Zero &&
        EmbeddedTaskbarEmbeddingPolicy.RequiresWindowReplacement(_attachedDisplayTarget, targetDisplay);

    private bool IsSameTargetDisplay(DisplayMonitor? targetDisplay) =>
        _attachedDisplayTarget is { } attachedTarget && attachedTarget.Matches(targetDisplay);

    private static bool IsWindows11() =>
        Environment.OSVersion.Version.Major == 10 &&
        Environment.OSVersion.Version.Build >= 22000;

    private void ReportAttachOutcome(EmbeddedTaskbarAttachResult result, string reason)
    {
        var summary = $"{result} Reason={reason}";
        if (string.Equals(summary, _lastAttachDiagnostic, StringComparison.Ordinal))
        {
            return;
        }

        _lastAttachDiagnostic = summary;
        Log.Diagnostic("EMBED", $"AttachResult={summary}");
    }

    private static string DescribeTarget(DisplayMonitor? targetDisplay) =>
        targetDisplay?.Id ?? "primary";

    private static void ApplyChildWindowStyle(IntPtr hwnd)
    {
        var style = TaskbarNativeMethods.GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        _ = TaskbarNativeMethods.SetWindowLongPtr(
            hwnd,
            GwlStyle,
            new IntPtr((style & ~WsPopup) | WsChild | WsVisible));

        var extendedStyle = TaskbarNativeMethods.GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        _ = TaskbarNativeMethods.SetWindowLongPtr(
            hwnd,
            GwlExStyle,
            new IntPtr((extendedStyle & ~WsExToolWindow) | WsExNoActivate));
    }

    private void RestoreTopLevelStyles(IntPtr hwnd)
    {
        if (_hasOriginalStyles)
        {
            _ = TaskbarNativeMethods.SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(_originalStyle));
            _ = TaskbarNativeMethods.SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(_originalExtendedStyle));
            return;
        }

        var style = TaskbarNativeMethods.GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        _ = TaskbarNativeMethods.SetWindowLongPtr(hwnd, GwlStyle, new IntPtr((style & ~WsChild) | WsPopup));
        var extendedStyle = TaskbarNativeMethods.GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        _ = TaskbarNativeMethods.SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(extendedStyle & ~WsExNoActivate));
    }

    private static IntPtr FindTaskbarContainer(IntPtr taskbar)
    {
        var reBar = TaskbarNativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "ReBarWindow32", null);
        return reBar != IntPtr.Zero ? reBar : TaskbarNativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "WorkerW", null);
    }

    private static IntPtr FindTaskList(IntPtr container)
    {
        var taskList = TaskbarNativeMethods.FindWindowEx(container, IntPtr.Zero, "MSTaskSwWClass", null);
        return taskList != IntPtr.Zero
            ? taskList
            : TaskbarNativeMethods.FindWindowEx(container, IntPtr.Zero, "MSTaskListWClass", null);
    }

    private static double GetPixelsPerDipForHandle(IntPtr hwnd)
    {
        var dpi = TaskbarNativeMethods.GetDpiForWindow(hwnd);
        return dpi > 0 ? dpi / 96.0 : 1;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);
}
