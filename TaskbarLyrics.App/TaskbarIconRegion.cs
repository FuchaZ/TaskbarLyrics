using System.Runtime.InteropServices;
using System.Windows.Automation;
using TaskbarLyrics.Core.Utilities;

namespace TaskbarLyrics.App;

/// <summary>
/// 任务栏图标区边界的一次探测结果。坐标是物理屏幕像素，与 UI Automation 的
/// <c>BoundingRectangle</c> 和 Win32 的 <c>GetWindowRect</c> 一致；<c>null</c> 表示该边界本次不可用。
/// </summary>
internal readonly record struct TaskbarIconRegion(double? LeftIconsRightPhysical, double? TrayLeftPhysical)
{
    public static readonly TaskbarIconRegion Unavailable = new(null, null);

    /// <summary>
    /// 换算为逻辑像素，并把原点从屏幕左上角移到 <paramref name="originPhysicalX"/> 所在的垂直线：
    /// 悬浮模式传 0（结果即屏幕逻辑坐标），嵌入模式传任务栏客户区原点的物理 X
    /// （结果即任务栏客户区坐标，可直接交给 SetWindowPos）。
    /// </summary>
    public TaskbarHorizontalAnchors ToLogical(double pixelsPerDip, double originPhysicalX)
    {
        var scale = double.IsFinite(pixelsPerDip) && pixelsPerDip > 0 ? pixelsPerDip : 1;
        return new TaskbarHorizontalAnchors(
            LeftIconsRightPhysical is { } iconsRight ? (iconsRight - originPhysicalX) / scale : null,
            TrayLeftPhysical is { } trayLeft ? (trayLeft - originPhysicalX) / scale : null);
    }
}

/// <summary>
/// 水平锚点计算所需的图标区边界，单位是逻辑像素，原点由调用方约定：
/// 悬浮模式下是屏幕左上角，嵌入模式下是任务栏客户区左上角。
/// </summary>
/// <remarks>
/// 只承载探测结果。位置换算与夹紧规则在 <see cref="EmbeddedTaskbarLayoutCalculator"/>，
/// 以保证「读环境」和「算位置」互不耦合、算法可独立测试。
/// </remarks>
internal readonly record struct TaskbarHorizontalAnchors(double? LeftIconsRight, double? TrayLeft)
{
    public static readonly TaskbarHorizontalAnchors Unavailable = new(null, null);

    /// <summary>
    /// 从上游占用空间快照（<see cref="TaskbarOccupiedSpaceProvider"/> 的逻辑区间）推导图标区边界：
    /// 区间排序后按 <paramref name="clusterGapTolerance"/> 合并成簇，第一簇的右缘即左侧图标组
    /// （开始/搜索/应用按钮）的右缘，最后一簇的左缘即系统托盘左缘。
    /// </summary>
    /// <remarks>
    /// 簇数不足两个时返回 <see cref="Unavailable"/>（托盘边界不可信，交给调用方退回同步探测）；
    /// 相邻按钮之间只有几个像素的间隙，合并容差必须明显大于它，才能把整段图标区当成一簇。
    /// </remarks>
    public static TaskbarHorizontalAnchors FromOccupiedIntervals(
        IReadOnlyList<TaskbarInterval> occupied,
        double clusterGapTolerance = 32)
    {
        if (occupied.Count == 0)
        {
            return Unavailable;
        }

        var sorted = occupied.Where(interval =>
                double.IsFinite(interval.Start) && double.IsFinite(interval.End) && interval.End > interval.Start)
            .OrderBy(interval => interval.Start)
            .ToList();
        if (sorted.Count == 0)
        {
            return Unavailable;
        }

        var clusterEnd = sorted[0].End;
        double? firstClusterEnd = clusterEnd;
        double? lastClusterStart = null;
        foreach (var interval in sorted.Skip(1))
        {
            if (interval.Start - clusterEnd <= clusterGapTolerance)
            {
                clusterEnd = Math.Max(clusterEnd, interval.End);
                if (lastClusterStart is null)
                {
                    // 仍在第一簇内生长，右缘跟着推进；出现首个间隙后定格，
                    // 之后的合并属于托盘等其他簇，不能再改动第一簇的右缘。
                    firstClusterEnd = clusterEnd;
                }
                continue;
            }

            lastClusterStart = interval.Start;
            clusterEnd = interval.End;
        }

        if (lastClusterStart is null)
        {
            // 只有一簇：要么整条任务栏被占满（托盘与图标区连成一片），要么探测不完整。
            return Unavailable;
        }

        return new TaskbarHorizontalAnchors(firstClusterEnd, lastClusterStart);
    }
}

/// <summary>
/// 探测单个任务栏窗口的图标区边界：左侧图标组（开始/搜索/应用按钮）的右缘，
/// 以及右侧系统托盘的左缘。
/// </summary>
/// <remarks>
/// 探测同时覆盖 Windows 11 的 XAML 任务栏（UI Automation）和 Windows 10 的传统子窗口
/// （Win32），并按任务栏窗口句柄缓存结果，因此每个显示器的任务栏各自独立取值。
/// UI Automation 查询是跨进程调用，节流后由调用方的低频轮询驱动。
/// </remarks>
internal static class TaskbarIconRegionProbe
{
    private const string TrayNotifyWindowClass = "TrayNotifyWnd";
    private const string TaskbarFrameAutomationClass = "Taskbar.TaskbarFrameAutomationPeer";
    private const string TaskListButtonAutomationClass = "Taskbar.TaskListButtonAutomationPeer";
    private const string ToggleButtonAutomationClass = "ToggleButton";
    private const string SystemTrayAutomationClassPrefix = "SystemTray.";
    private const string LegacyStartButtonClass = "Button";
    private const string LegacyTaskBandRebarClass = "ReBarWindow32";
    private const long ScanThrottleMilliseconds = 400;
    private const int CachedTaskbarLimit = 8;

    private static readonly object CacheLock = new();
    private static readonly Dictionary<IntPtr, CachedRegion> Cache = [];
    private static int _hasReportedScanFailure;

    public static TaskbarIconRegion GetRegion(IntPtr taskbarHandle)
    {
        if (taskbarHandle == IntPtr.Zero)
        {
            return TaskbarIconRegion.Unavailable;
        }

        lock (CacheLock)
        {
            if (Cache.TryGetValue(taskbarHandle, out var cached) &&
                Environment.TickCount64 - cached.ScannedAtTick < ScanThrottleMilliseconds)
            {
                return cached.Region;
            }
        }

        var region = Scan(taskbarHandle);
        lock (CacheLock)
        {
            if (Cache.Count >= CachedTaskbarLimit)
            {
                // 任务栏重建会留下失效句柄；整体丢弃比逐项验证句柄存活更简单，代价只是多一次扫描。
                Cache.Clear();
            }

            Cache[taskbarHandle] = new CachedRegion(Environment.TickCount64, region);
        }

        return region;
    }

    private static TaskbarIconRegion Scan(IntPtr taskbarHandle)
    {
        try
        {
            return new TaskbarIconRegion(
                ScanLeftIconsRightEdgePhysical(taskbarHandle),
                ScanTrayLeftEdgePhysical(taskbarHandle));
        }
        catch (Exception exception) when (IsRecoverableAutomationFailure(exception))
        {
            ReportScanFailure(exception);
            return TaskbarIconRegion.Unavailable;
        }
    }

    /// <summary>
    /// UI Automation 跨进程读取可能因权限、超时、界面重建而失败；这些都属于「本次探测不可用」，
    /// 不应让异常穿透到界面线程。真正的编程错误（如参数越界）仍然照常抛出。
    /// </summary>
    private static bool IsRecoverableAutomationFailure(Exception exception) =>
        exception is InvalidOperationException
            or COMException
            or ElementNotAvailableException
            or UnauthorizedAccessException
            or TimeoutException;

    private static void ReportScanFailure(Exception exception)
    {
        if (Interlocked.Exchange(ref _hasReportedScanFailure, 1) != 0)
        {
            return;
        }

        Log.Diagnostic(
            "TASKBAR-REGION",
            $"Taskbar icon region scan failed: {exception.GetType().Name}: {exception.Message}");
    }

    /// <summary>
    /// 左侧图标组右缘：Windows 11 走任务栏 XAML 框架内的按钮，Windows 10 走传统子窗口。
    /// </summary>
    /// <remarks>
    /// 传统子窗口回退只在两类情况执行：系统本身是 Windows 10，或 Windows 11 上探测不到
    /// XAML 框架（辅助功能被关闭、任务栏仍处于旧实现）。这样在 Windows 11 正常路径上
    /// 不会每次扫描都遍历一遍全部任务栏子窗口。
    /// </remarks>
    private static double? ScanLeftIconsRightEdgePhysical(IntPtr taskbarHandle)
    {
        var isWindows11 = Environment.OSVersion.Version.Build >= 22000;
        var frameRight = ScanTaskbarFrameRightEdgePhysical(taskbarHandle);
        if (frameRight is not null)
        {
            return frameRight;
        }

        return isWindows11 ? null : ScanLegacyStartButtonRightEdgePhysical(taskbarHandle);
    }

    private static double? ScanTaskbarFrameRightEdgePhysical(IntPtr taskbarHandle)
    {
        var taskbarElement = AutomationElement.FromHandle(taskbarHandle);
        if (taskbarElement is null)
        {
            return null;
        }

        var frame = taskbarElement.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ClassNameProperty, TaskbarFrameAutomationClass));
        if (frame is null)
        {
            return null;
        }

        var buttons = frame.FindAll(
            TreeScope.Descendants,
            new OrCondition(
                new PropertyCondition(AutomationElement.ClassNameProperty, TaskListButtonAutomationClass),
                new PropertyCondition(AutomationElement.ClassNameProperty, ToggleButtonAutomationClass)));

        double? maxRight = null;
        foreach (AutomationElement button in buttons)
        {
            var rect = ReadBoundingRectangle(button);
            if (rect is { } visible && (maxRight is null || visible.Right > maxRight))
            {
                maxRight = visible.Right;
            }
        }

        return maxRight;
    }

    private static double? ScanLegacyStartButtonRightEdgePhysical(IntPtr taskbarHandle)
    {
        double? maxRight = null;
        for (var child = TaskbarNativeMethods.FindWindowEx(taskbarHandle, IntPtr.Zero, null, null);
             child != IntPtr.Zero;
             child = TaskbarNativeMethods.FindWindowEx(taskbarHandle, child, null, null))
        {
            var className = GetClassName(child);
            if (!string.Equals(className, LegacyStartButtonClass, StringComparison.Ordinal) &&
                !string.Equals(className, LegacyTaskBandRebarClass, StringComparison.Ordinal))
            {
                continue;
            }

            if (TaskbarNativeMethods.GetWindowRect(child, out var rect) &&
                (maxRight is null || rect.Right > maxRight))
            {
                maxRight = rect.Right;
            }
        }

        return maxRight;
    }

    /// <summary>
    /// 托盘左缘：优先用托盘的 Win32 子窗口，回退到任务栏无障碍树里 <c>SystemTray.</c> 前缀元素的左缘。
    /// </summary>
    /// <remarks>
    /// 回退分支只遍历带托盘前缀的类名元素，避免 <c>Condition.TrueCondition</c> 拉取整棵任务栏子树
    /// （每个 XAML 按钮和托盘图标），那是跨进程调用里最贵的一段。
    /// </remarks>
    private static double? ScanTrayLeftEdgePhysical(IntPtr taskbarHandle)
    {
        var trayNotify = TaskbarNativeMethods.FindWindowEx(taskbarHandle, IntPtr.Zero, TrayNotifyWindowClass, null);
        if (trayNotify != IntPtr.Zero &&
            TaskbarNativeMethods.GetWindowRect(trayNotify, out var trayRect) &&
            trayRect.Right > trayRect.Left)
        {
            return trayRect.Left;
        }

        var taskbarElement = AutomationElement.FromHandle(taskbarHandle);
        if (taskbarElement is null)
        {
            return null;
        }

        // 托盘元素的类名以固定前缀开头（SystemTray.NotifyIconView 等），先用属性条件把候选集缩小。
        var trayCandidates = taskbarElement.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ClassNameProperty, SystemTrayAutomationClassPrefix));

        double? minLeft = null;
        foreach (AutomationElement candidate in trayCandidates)
        {
            var className = candidate.Current.ClassName;
            if (string.IsNullOrEmpty(className) ||
                !className.StartsWith(SystemTrayAutomationClassPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var rect = ReadBoundingRectangle(candidate);
            if (rect is { } visible && (minLeft is null || visible.X < minLeft))
            {
                minLeft = visible.X;
            }
        }

        return minLeft;
    }

    /// <summary>
    /// 一次读取元素的边界矩形。UI Automation 的属性读取代价高，缺失元素直接视为不可用。
    /// </summary>
    private static System.Windows.Rect? ReadBoundingRectangle(AutomationElement element)
    {
        var rect = element.Current.BoundingRectangle;
        return rect.IsEmpty || rect.Width <= 0 ? null : rect;
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var buffer = new char[256];
        var length = TaskbarNativeMethods.GetClassName(hwnd, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private readonly record struct CachedRegion(long ScannedAtTick, TaskbarIconRegion Region);
}
