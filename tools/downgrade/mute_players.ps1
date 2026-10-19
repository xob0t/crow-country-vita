# Mutes every audio session of the game's players on this PC at the Windows level (the mixer's
# per-app mute), whatever the game does with its own volume. -Watch keeps doing it every second;
# -Until <pid> stops watching once that process is gone (run_player.py starts one per run).
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools/downgrade/mute_players.ps1 [-Watch] [-Name CrowCountry] [-Until <pid>]
param([switch]$Watch, [string]$Name = 'CrowCountry', [int]$Until = 0)

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class CrowMute
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumerator { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        int GetCount(out int count);
        int Item(int index, out IMMDevice device);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object face);
    }

    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
        int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
        int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, out IAudioSessionControl2 session);
    }

    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioSessionControl2
    {
        int GetState(out int state);
        int GetDisplayName(out IntPtr name);
        int SetDisplayName(IntPtr name, IntPtr context);
        int GetIconPath(out IntPtr path);
        int SetIconPath(IntPtr path, IntPtr context);
        int GetGroupingParam(out Guid param);
        int SetGroupingParam(ref Guid param, IntPtr context);
        int RegisterAudioSessionNotification(IntPtr client);
        int UnregisterAudioSessionNotification(IntPtr client);
        int GetSessionIdentifier(out IntPtr id);
        int GetSessionInstanceIdentifier(out IntPtr id);
        int GetProcessId(out uint pid);
    }

    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, ref Guid context);
        int GetMasterVolume(out float level);
        int SetMute(bool mute, ref Guid context);
        int GetMute(out bool mute);
    }

    // Mutes the sessions of these processes on every active render device; returns how many it newly muted.
    public static int Mute(HashSet<uint> pids)
    {
        int muted = 0;
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDeviceCollection devices;
        enumerator.EnumAudioEndpoints(0, 1, out devices);  // eRender, DEVICE_STATE_ACTIVE
        int count;
        devices.GetCount(out count);
        Guid managerId = typeof(IAudioSessionManager2).GUID;
        for (int d = 0; d < count; d++)
        {
            IMMDevice device;
            devices.Item(d, out device);
            object face;
            if (device.Activate(ref managerId, 23, IntPtr.Zero, out face) != 0) continue;  // CLSCTX_ALL
            IAudioSessionEnumerator sessions;
            ((IAudioSessionManager2)face).GetSessionEnumerator(out sessions);
            int n;
            sessions.GetCount(out n);
            for (int s = 0; s < n; s++)
            {
                IAudioSessionControl2 session;
                sessions.GetSession(s, out session);
                uint pid;
                session.GetProcessId(out pid);
                if (!pids.Contains(pid)) continue;
                var volume = (ISimpleAudioVolume)session;
                bool already;
                volume.GetMute(out already);
                if (already) continue;
                Guid context = Guid.Empty;
                volume.SetMute(true, ref context);
                muted++;
            }
        }
        return muted;
    }
}
'@

do {
    $pids = New-Object 'System.Collections.Generic.HashSet[uint32]'
    foreach ($p in Get-Process -Name $Name -ErrorAction SilentlyContinue) { [void]$pids.Add([uint32]$p.Id) }
    if ($pids.Count -gt 0) {
        $n = [CrowMute]::Mute($pids)
        if ($n -gt 0) { Write-Output ("{0:HH:mm:ss} muted {1} session(s) of {2} player(s)" -f (Get-Date), $n, $pids.Count) }
    }
    if ($Until -and -not (Get-Process -Id $Until -ErrorAction SilentlyContinue)) { break }
    if ($Watch) { Start-Sleep -Milliseconds 500 }
} while ($Watch)
