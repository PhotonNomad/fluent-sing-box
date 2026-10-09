using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace FluentSingBox.Client.Services;

public static class CoreControlPipe
{
    // The ordinary client creates the endpoint before requesting elevation.
    // Explicit ACLs allow the elevated helper, without opening the pipe to Everyone.
    // CurrentUserOnly also compares elevation levels and must not be used here.
    public static NamedPipeServerStream Create(string name)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException(Localization.Get("CoreControlPipe_CannotIdentifyTheCurrentWindowsUser"));
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
    }
    public static async Task AcceptAsync(NamedPipeServerStream pipe, int expectedPid, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try { await pipe.WaitForConnectionAsync(cancellation.Token); }
        catch (OperationCanceledException e) { throw new TimeoutException(Localization.Get("CoreControlPipe_TimedOutWaitingForTheElevatedHostConnection"), e); }
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (pid != expectedPid) throw new InvalidOperationException(Localization.Get("CoreControlPipe_TheConnectedProcessIsNotTheCoreHost"));
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}
