using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ComCon.Core.Presentation;

namespace ComCon.Windows;

public sealed class WindowPositionStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string path;

    public WindowPositionStore(string windowId)
    {
        if (string.IsNullOrWhiteSpace(windowId) ||
            windowId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            throw new ArgumentException("Window identity must contain only ASCII letters, digits or hyphens.", nameof(windowId));
        }

        path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ComCon", "Windows", windowId + ".json");
    }

    public void Restore(nint windowHandle)
    {
        if (!File.Exists(path))
        {
            return;
        }

        WindowPosition position = JsonSerializer.Deserialize<WindowPosition>(
            File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Saved window position is null.");
        if (!position.IsReasonable)
        {
            throw new InvalidDataException("Saved window position is outside permitted coordinate bounds.");
        }

        if (!GetWindowRect(windowHandle, out NativeRect rectangle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        NativeRect titleArea = new()
        {
            Left = position.X,
            Top = position.Y,
            Right = checked(position.X + Math.Min(rectangle.Right - rectangle.Left, 160)),
            Bottom = checked(position.Y + 32)
        };
        if (MonitorFromRect(ref titleArea, 0) == 0)
        {
            throw new InvalidDataException("Saved window title area is no longer on a monitor.");
        }

        if (!SetWindowPos(windowHandle, 0, position.X, position.Y, 0, 0, 0x0015))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Save(nint windowHandle)
    {
        if (IsIconic(windowHandle) || IsZoomed(windowHandle))
        {
            return;
        }

        if (!GetWindowRect(windowHandle, out NativeRect rectangle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        WindowPosition position = new(rectangle.Left, rectangle.Top);
        NativeRect titleArea = new()
        {
            Left = rectangle.Left,
            Top = rectangle.Top,
            Right = rectangle.Left + Math.Min(rectangle.Right - rectangle.Left, 160),
            Bottom = rectangle.Top + 32
        };
        if (!position.IsReasonable || MonitorFromRect(ref titleArea, 0) == 0)
        {
            throw new InvalidDataException("Window position is invalid or its title area is off-screen.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(position, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRect rectangle);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref NativeRect rectangle, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsZoomed(nint windowHandle);
}
