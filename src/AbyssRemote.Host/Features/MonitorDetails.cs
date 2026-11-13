using System.Runtime.InteropServices;

namespace AbyssRemote.Host.Features;

/// <summary>What a monitor says about itself: its model name ("DELL U2720Q", empty when it
/// does not say) and its refresh rate in Hz (0 when unknown).</summary>
public sealed record MonitorDetails(string FriendlyName, int RefreshHz);

/// <summary>Reads monitor model names and refresh rates with the Windows display configuration
/// API (QueryDisplayConfig and DisplayConfigGetDeviceInfo), the source the Settings app uses.
/// Screen.AllScreens only knows device names such as \\.\DISPLAY1, which tell a person nothing
/// in a monitor picker. Struct layouts and constants are from wingdi.h (Windows SDK 10.0.26100).</summary>
internal static unsafe class MonitorCatalog
{
    private const uint OnlyActivePaths = 0x2;                // QDC_ONLY_ACTIVE_PATHS
    private const uint GetSourceName = 1, GetTargetName = 2; // DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME / _TARGET_NAME
    private const int ModeInfoSize = 64;                     // sizeof(DISPLAYCONFIG_MODE_INFO)

    // Monitors rarely change, but DesktopEngine.Displays() runs for every captured frame and
    // every pointer message, so one answer is reused for two seconds.
    private const long LifetimeMs = 2000;
    private sealed record Snapshot(IReadOnlyDictionary<string, MonitorDetails> Monitors, long ReadAtMs);
    private static volatile Snapshot? _latest;

    /// <summary>Details by Windows device name (\\.\DISPLAY1), at most two seconds old.
    /// A monitor Windows cannot describe is simply missing.</summary>
    public static IReadOnlyDictionary<string, MonitorDetails> Current()
    {
        var latest = _latest;
        long now = Environment.TickCount64;
        if (latest is null || now - latest.ReadAtMs > LifetimeMs)
            _latest = latest = new Snapshot(Read(), now);
        return latest.Monitors;
    }

    /// <summary>Asks Windows now. Returns what it could find; never throws for a display
    /// problem, because a missing name must not stop capture or input.</summary>
    internal static Dictionary<string, MonitorDetails> Read()
    {
        var monitors = new Dictionary<string, MonitorDetails>(StringComparer.OrdinalIgnoreCase);
        if (GetDisplayConfigBufferSizes(OnlyActivePaths, out uint pathCount, out uint modeCount) != 0 || pathCount == 0)
            return monitors;

        // A "path" connects a source (a desktop area, \\.\DISPLAY1) to a target (a monitor).
        var paths = new DisplayConfigPath[pathCount];
        var modes = new byte[modeCount * ModeInfoSize]; // never read, but the call wants room for them
        fixed (DisplayConfigPath* pathBuffer = paths)
        fixed (byte* modeBuffer = modes)
        {
            if (QueryDisplayConfig(OnlyActivePaths, ref pathCount, pathBuffer, ref modeCount, modeBuffer, IntPtr.Zero) != 0)
                return monitors;
        }

        foreach (var path in paths.AsSpan(0, (int)Math.Min(pathCount, (uint)paths.Length)))
        {
            var source = new DisplayConfigSourceName
            {
                Type = GetSourceName, Size = (uint)sizeof(DisplayConfigSourceName),
                AdapterLow = path.SourceAdapterLow, AdapterHigh = path.SourceAdapterHigh, Id = path.SourceId,
            };
            var target = new DisplayConfigTargetName
            {
                Type = GetTargetName, Size = (uint)sizeof(DisplayConfigTargetName),
                AdapterLow = path.TargetAdapterLow, AdapterHigh = path.TargetAdapterHigh, Id = path.TargetId,
            };
            if (DisplayConfigGetDeviceInfo(&source) != 0 || DisplayConfigGetDeviceInfo(&target) != 0) continue;

            int hz = path.RefreshDenominator == 0 ? 0
                : (int)Math.Round((double)path.RefreshNumerator / path.RefreshDenominator); // 59.94 is shown as 60
            // The name comes from the monitor's own memory (EDID), so it is cleaned like any
            // outside text. A mirrored desktop has two paths; the first monitor names it.
            monitors.TryAdd(Text(source.GdiDeviceName, 32), new MonitorDetails(StateStore.CleanName(Text(target.FriendlyName, 64)), hz));
        }
        return monitors;
    }

    private static string Text(char* buffer, int capacity)
    {
        var text = new ReadOnlySpan<char>(buffer, capacity);
        int end = text.IndexOf('\0');
        return new string(end < 0 ? text : text[..end]);
    }

    /// <summary>DISPLAYCONFIG_PATH_INFO (72 bytes): source info, target info, flags.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigPath
    {
        // DISPLAYCONFIG_PATH_SOURCE_INFO: adapterId (LUID), id, modeInfoIdx, statusFlags
        public uint SourceAdapterLow; public int SourceAdapterHigh; public uint SourceId, SourceModeIndex, SourceStatus;
        // DISPLAYCONFIG_PATH_TARGET_INFO: adapterId (LUID), id, modeInfoIdx, outputTechnology, rotation, scaling,
        // refreshRate (DISPLAYCONFIG_RATIONAL), scanLineOrdering, targetAvailable, statusFlags
        public uint TargetAdapterLow; public int TargetAdapterHigh; public uint TargetId, TargetModeIndex;
        public uint OutputTechnology, Rotation, Scaling, RefreshNumerator, RefreshDenominator, ScanLineOrdering;
        public int TargetAvailable; public uint TargetStatus;
        public uint Flags;
    }

    /// <summary>DISPLAYCONFIG_SOURCE_DEVICE_NAME (84 bytes): request header, then the GDI device name.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigSourceName
    {
        public uint Type, Size, AdapterLow; public int AdapterHigh; public uint Id; // DISPLAYCONFIG_DEVICE_INFO_HEADER
        public fixed char GdiDeviceName[32];
    }

    /// <summary>DISPLAYCONFIG_TARGET_DEVICE_NAME (420 bytes): request header, then what the monitor reports.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct DisplayConfigTargetName
    {
        public uint Type, Size, AdapterLow; public int AdapterHigh; public uint Id; // DISPLAYCONFIG_DEVICE_INFO_HEADER
        public uint Flags, OutputTechnology;
        public ushort EdidManufactureId, EdidProductCodeId;
        public uint ConnectorInstance;
        public fixed char FriendlyName[64];
        public fixed char DevicePath[128];
    }

    // Pointers instead of ref/array parameters: the structs go to Windows exactly as laid out
    // above, with no marshalling step that could treat the text as 8-bit characters.
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")] private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, DisplayConfigPath* paths, ref uint modeCount, byte* modes, IntPtr currentTopology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(void* request);
}
