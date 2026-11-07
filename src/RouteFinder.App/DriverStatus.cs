using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RouteFinder.App;

internal static class DriverStatus
{
    public static string Read(string name)
    {
        nint manager = OpenSCManagerW(null, null, 0x0001);
        if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            nint service = OpenServiceW(manager, name, 0x0004);
            if (service == 0)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 1060) return "not installed";
                throw new Win32Exception(error);
            }
            try
            {
                if (!QueryServiceStatus(service, out var status))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                return status.CurrentState switch
                {
                    1 => "stopped",
                    2 => "starting",
                    3 => "stopping",
                    4 => "running",
                    5 => "resuming",
                    6 => "pausing",
                    7 => "paused",
                    _ => $"unknown state {status.CurrentState}"
                };
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode,
            ServiceSpecificExitCode, CheckPoint, WaitHint;
    }

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint OpenServiceW(nint manager, string serviceName, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryServiceStatus(nint service, out ServiceStatus status);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseServiceHandle(nint handle);
}
