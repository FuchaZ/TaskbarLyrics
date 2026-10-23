namespace TaskbarLyrics.App;

/// <summary>
/// 定位承载歌词窗口的任务栏窗口：主显示器是 <c>Shell_TrayWnd</c>，
/// 其他显示器是 <c>Shell_SecondaryTrayWnd</c>，按与目标显示器矩形的交集面积选择。
/// </summary>
internal static class TaskbarWindowLocator
{
    private const string PrimaryTaskbarClass = "Shell_TrayWnd";
    private const string SecondaryTaskbarClass = "Shell_SecondaryTrayWnd";

    /// <summary>
    /// 找到目标显示器对应的任务栏窗口；<paramref name="targetDisplay"/> 为 <c>null</c> 时表示主任务栏。
    /// </summary>
    public static IntPtr FindTaskbar(DisplayMonitor? targetDisplay, out string diagnostic)
    {
        var primaryTaskbar = TaskbarNativeMethods.FindWindow(PrimaryTaskbarClass, null);
        if (targetDisplay is null)
        {
            diagnostic = $"Target=primary Primary={primaryTaskbar != IntPtr.Zero}";
            return primaryTaskbar;
        }

        var taskbars = new List<IntPtr>();
        if (primaryTaskbar != IntPtr.Zero)
        {
            taskbars.Add(primaryTaskbar);
        }

        var secondaryCount = 0;
        var secondaryTaskbar = IntPtr.Zero;
        while (true)
        {
            secondaryTaskbar = TaskbarNativeMethods.FindWindowEx(
                IntPtr.Zero,
                secondaryTaskbar,
                SecondaryTaskbarClass,
                null);
            if (secondaryTaskbar == IntPtr.Zero)
            {
                break;
            }

            secondaryCount++;
            taskbars.Add(secondaryTaskbar);
        }

        var match = taskbars
            .Select(handle => new { Handle = handle, Score = CalculateTaskbarScore(handle, targetDisplay.Bounds) })
            .OrderByDescending(candidate => candidate.Score.IntersectionArea)
            .ThenBy(candidate => candidate.Score.CenterDistanceSquared)
            .FirstOrDefault();
        var bounds = targetDisplay.Bounds;
        diagnostic =
            $"Target={bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom} " +
            $"Primary={(primaryTaskbar != IntPtr.Zero)} Secondary={secondaryCount} " +
            $"BestIntersection={match?.Score.IntersectionArea ?? 0}";
        return match is not null && match.Score.IntersectionArea > 0
            ? match.Handle
            : IntPtr.Zero;
    }

    private static (long IntersectionArea, long CenterDistanceSquared) CalculateTaskbarScore(
        IntPtr taskbar,
        NativeRect displayBounds)
    {
        if (!TaskbarNativeMethods.GetWindowRect(taskbar, out var taskbarBounds))
        {
            return (0, long.MaxValue);
        }

        var intersectionArea = EmbeddedTaskbarLayoutCalculator.CalculateIntersectionArea(
            new NativeRect(taskbarBounds.Left, taskbarBounds.Top, taskbarBounds.Right, taskbarBounds.Bottom),
            displayBounds);
        var taskbarCenterX = ((long)taskbarBounds.Left + taskbarBounds.Right) / 2;
        var taskbarCenterY = ((long)taskbarBounds.Top + taskbarBounds.Bottom) / 2;
        var displayCenterX = ((long)displayBounds.Left + displayBounds.Right) / 2;
        var displayCenterY = ((long)displayBounds.Top + displayBounds.Bottom) / 2;
        var deltaX = taskbarCenterX - displayCenterX;
        var deltaY = taskbarCenterY - displayCenterY;
        return (intersectionArea, (deltaX * deltaX) + (deltaY * deltaY));
    }
}
