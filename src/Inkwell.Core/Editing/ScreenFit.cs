using System.Windows;

namespace Inkwell.Core.Editing;

/// <summary>
/// Where windows go so they are never partly off the screen (the title bar above the top of the display, or a
/// window bigger than a small screen). Pure maths, so it is unit tested; the windows apply the results.
/// </summary>
public static class ScreenFit
{
    /// <summary>
    /// The first start's window: the preferred size shrunk to fit the work area (with a margin), centred on it. When
    /// even the minimum size doesn't fit, the window should open maximized.
    /// </summary>
    public static (Rect Bounds, bool Maximize) FirstStart(Size preferred, Size minimum, Rect work, double margin = 24)
    {
        var width = Math.Min(preferred.Width, work.Width - 2 * margin);
        var height = Math.Min(preferred.Height, work.Height - 2 * margin);
        if (width < minimum.Width || height < minimum.Height)
            return (new Rect(work.Left, work.Top, Math.Min(preferred.Width, work.Width), Math.Min(preferred.Height, work.Height)), true);
        return (new Rect(work.Left + (work.Width - width) / 2, work.Top + (work.Height - height) / 2, width, height), false);
    }

    /// <summary>
    /// Keeps a window (device pixels) inside the work area of its display: shrunk to fit and moved in. Returns null
    /// when it already fits. <paramref name="maximize"/> says the window should be maximized instead (its minimum
    /// size doesn't fit and it can be resized).
    /// </summary>
    public static Rect? KeepInside(Rect window, Rect work, Size minimum, bool canResize, out bool maximize)
    {
        maximize = false;
        if ((minimum.Width > work.Width || minimum.Height > work.Height) && canResize)
        {
            maximize = true;
            return null;
        }
        var width = Math.Min(window.Width, work.Width);
        var height = Math.Min(window.Height, work.Height);
        var left = MathEx.Clamp(window.Left, work.Left, work.Right - width);
        var top = MathEx.Clamp(window.Top, work.Top, work.Bottom - height);
        if (left == window.Left && top == window.Top && width == window.Width && height == window.Height) return null;
        return new Rect(left, top, width, height);
    }
}
