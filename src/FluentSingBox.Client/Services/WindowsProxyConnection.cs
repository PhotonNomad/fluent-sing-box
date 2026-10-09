using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FluentSingBox.Client.Services;

// WinINet LAN connection options, also used by v2rayN. Written independently
// against the Windows API; this does not enumerate or alter dial-up profiles.
internal static class WindowsProxyConnection
{
    [StructLayout(LayoutKind.Explicit)] private struct Value
    {
        [FieldOffset(0)] public int Flags;
        [FieldOffset(0)] public IntPtr Text;
        [FieldOffset(0)] public long FileTime;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Option { public int Id; public Value Data; }
    [StructLayout(LayoutKind.Sequential)] private struct OptionList
    {
        public int Size;
        public IntPtr Connection;
        public int Count, Error;
        public IntPtr Options;
    }
    public static int? ReadFlags()
    {
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<Option>());
        try
        {
            Marshal.StructureToPtr(new Option { Id = 1 }, buffer, false);
            var list = new OptionList { Size = Marshal.SizeOf<OptionList>(), Count = 1, Options = buffer };
            var size = list.Size;
            return InternetQueryOption(IntPtr.Zero, 75, ref list, ref size)
                ? Marshal.PtrToStructure<Option>(buffer).Data.Flags : null;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    public static void Set(int flags, string? server, string? bypass, string? autoUrl)
    {
        var strings = new[] { Marshal.StringToHGlobalUni(server ?? ""), Marshal.StringToHGlobalUni(bypass ?? ""), Marshal.StringToHGlobalUni(autoUrl ?? "") };
        var stride = Marshal.SizeOf<Option>();
        var buffer = Marshal.AllocHGlobal(stride * 4);
        try
        {
            var options = new[]
            {
                new Option { Id = 1, Data = new Value { Flags = flags } },
                new Option { Id = 2, Data = new Value { Text = strings[0] } },
                new Option { Id = 3, Data = new Value { Text = strings[1] } },
                new Option { Id = 4, Data = new Value { Text = strings[2] } }
            };
            for (var i = 0; i < options.Length; i++) Marshal.StructureToPtr(options[i], IntPtr.Add(buffer, stride * i), false);
            var list = new OptionList { Size = Marshal.SizeOf<OptionList>(), Count = 4, Options = buffer };
            if (!InternetSetOption(IntPtr.Zero, 75, ref list, list.Size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), Localization.Get("WindowsProxyConnection_FailedToSetTheWindowsConnectionProxy"));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            foreach (var pointer in strings) Marshal.FreeHGlobal(pointer);
        }
    }
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InternetSetOption(IntPtr handle, int option, ref OptionList buffer, int length);
    [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InternetQueryOption(IntPtr handle, int option, ref OptionList buffer, ref int length);
}
