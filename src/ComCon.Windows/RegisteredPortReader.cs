using System.IO;
using Microsoft.Win32;

namespace ComCon.Windows;

/// <summary>Reads registered port names only; never opens a serial device.</summary>
public static class RegisteredPortReader
{
    public static IReadOnlyList<string> Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM", writable: false);
        if (key is null) return Array.Empty<string>();
        var values = new List<string>();
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is not string port)
                throw new IOException("A registered serial-port entry is missing or is not a string. Refresh again.");
            values.Add(port);
        }
        return values.AsReadOnly();
    }
}
