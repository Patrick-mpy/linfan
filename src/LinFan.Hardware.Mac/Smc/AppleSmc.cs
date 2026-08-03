// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LinFan.Hardware.Mac.Smc;

/// <summary>
/// Realer <see cref="ISmc"/>-Adapter über den AppleSMC-UserClient (IOKit). Kapselt die gesamte
/// native Interop (P/Invoke, <c>SMCKeyData_t</c>-Layout, Kommando-Selektoren) — die einzige Stelle im
/// Backend mit macOS-Bindung. Lesen ist ohne erhöhte Rechte möglich; Schreiben (Steuer-Pfad) braucht Root.
/// <para>
/// Struktur- und Interop-Details wurden auf echter Apple-Silicon-Hardware verifiziert:
/// <c>sizeof(SMCKeyData_t) == 80</c>, Task-Port über das Datensymbol <c>mach_task_self_</c>,
/// Master-Port <c>0</c> (<c>kIOMainPortDefault</c>).
/// </para>
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class AppleSmc : ISmc
{
    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";

    // IOConnectCallStructMethod-Selektor + data8-Kommandos (SMC-Protokoll).
    private const uint KernelIndexSmc = 2;
    private const byte CmdReadBytes = 5;
    private const byte CmdWriteBytes = 6;
    private const byte CmdReadKeyInfo = 9;

    private uint _conn;
    private bool _open;
    private bool _disposed;

    public void Open()
    {
        if (_open) return;
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("AppleSmc läuft nur unter macOS.");

        IntPtr matching = IOServiceMatching("AppleSMC");
        uint service = IOServiceGetMatchingService(0, matching); // 0 = kIOMainPortDefault
        if (service == 0)
            throw new InvalidOperationException("AppleSMC-Dienst nicht gefunden (kein SMC?).");

        int rc = IOServiceOpen(service, MachTaskSelf(), 0, out _conn);
        IOObjectRelease(service);
        if (rc != 0)
            throw new InvalidOperationException($"IOServiceOpen(AppleSMC) fehlgeschlagen (rc={rc}).");

        _open = true;
    }

    public bool TryReadKey(string key, out SmcValue value)
    {
        value = default;
        if (!_open) return false;

        var input = SMCKeyData.New();
        input.key = FourCC(key);
        input.data8 = CmdReadKeyInfo;
        if (!Call(ref input, out var info)) return false;

        uint size = info.keyInfo.dataSize;
        if (size == 0 || size > 32) return false;

        var read = SMCKeyData.New();
        read.key = input.key;
        read.data8 = CmdReadBytes;
        read.keyInfo.dataSize = size;
        if (!Call(ref read, out var result)) return false;

        value = new SmcValue(FromFourCC(info.keyInfo.dataType), result.bytes[..(int)size]);
        return true;
    }

    public bool TryWriteKey(string key, SmcValue value)
    {
        if (!_open || value.Data.Length is 0 or > 32) return false;

        // dataSize der Firmware ermitteln — nur schreiben, wenn die Länge passt (kein Raten am Steuer-Register).
        var probe = SMCKeyData.New();
        probe.key = FourCC(key);
        probe.data8 = CmdReadKeyInfo;
        if (!Call(ref probe, out var info)) return false;
        if (info.keyInfo.dataSize != value.Data.Length) return false;

        var write = SMCKeyData.New();
        write.key = probe.key;
        write.data8 = CmdWriteBytes;
        write.keyInfo.dataSize = info.keyInfo.dataSize;
        Array.Copy(value.Data, write.bytes, value.Data.Length);
        return Call(ref write, out _);
    }

    private bool Call(ref SMCKeyData input, out SMCKeyData output)
    {
        output = SMCKeyData.New();
        ulong size = (ulong)Marshal.SizeOf<SMCKeyData>();
        ulong outSize = size;
        int rc = IOConnectCallStructMethod(_conn, KernelIndexSmc, ref input, size, ref output, ref outSize);
        // rc == kIOReturnSuccess UND das SMC-interne result-Byte == 0 (0x85 = „key not found", 0x84 = Länge).
        return rc == 0 && output.result == 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_open)
        {
            IOServiceClose(_conn);
            _open = false;
        }
    }

    // --- FourCC-Helfer --------------------------------------------------------

    private static uint FourCC(string s) =>
        ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3];

    private static string FromFourCC(uint v)
    {
        Span<char> c = stackalloc char[4];
        c[0] = (char)((v >> 24) & 0xff);
        c[1] = (char)((v >> 16) & 0xff);
        c[2] = (char)((v >> 8) & 0xff);
        c[3] = (char)(v & 0xff);
        return new string(c);
    }

    /// <summary>
    /// Task-Port des eigenen Prozesses. <c>mach_task_self()</c> ist ein Makro auf das exportierte
    /// <b>Datensymbol</b> <c>mach_task_self_</c> — nicht per <c>DllImport</c> aufrufbar; daher über
    /// <see cref="NativeLibrary"/> die Adresse holen und den Wert lesen.
    /// </summary>
    private static uint MachTaskSelf()
    {
        IntPtr lib = NativeLibrary.Load("/usr/lib/libSystem.dylib");
        IntPtr sym = NativeLibrary.GetExport(lib, "mach_task_self_");
        return (uint)Marshal.ReadInt32(sym);
    }

    // --- IOKit P/Invoke -------------------------------------------------------

    [DllImport(IOKit)] private static extern IntPtr IOServiceMatching(string name);
    [DllImport(IOKit)] private static extern uint IOServiceGetMatchingService(uint masterPort, IntPtr matching);
    [DllImport(IOKit)] private static extern int IOServiceOpen(uint service, uint owningTask, uint type, out uint connect);
    [DllImport(IOKit)] private static extern int IOServiceClose(uint connect);
    [DllImport(IOKit)] private static extern int IOObjectRelease(uint obj);

    [DllImport(IOKit)]
    private static extern int IOConnectCallStructMethod(
        uint connection, uint selector,
        ref SMCKeyData input, ulong inputSize,
        ref SMCKeyData output, ref ulong outputSize);

    // --- SMCKeyData_t (exaktes Layout, 80 Bytes) ------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SMCVersion
    {
        public byte major, minor, build, reserved;
        public ushort release;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMCPLimitData
    {
        public ushort version, length;
        public uint cpuPLimit, memPLimit, ioPLimit;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMCKeyInfoData
    {
        public uint dataSize;
        public uint dataType;
        public byte dataAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMCKeyData
    {
        public uint key;
        public SMCVersion vers;
        public SMCPLimitData pLimitData;
        public SMCKeyInfoData keyInfo;
        public byte result;
        public byte status;
        public byte data8;
        public uint data32;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] bytes;

        public static SMCKeyData New() => new() { bytes = new byte[32] };
    }
}
