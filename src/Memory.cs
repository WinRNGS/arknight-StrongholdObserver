using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace StrongholdObserver;

interface IReadMemory
{
    byte[] Read(ulong address, int size);
}

sealed class ReadFault(string message) : Exception(message);
sealed class NoSession(string message) : Exception(message);

static class Mem
{
    public static bool IsPointer(ulong p) => p >= 0x10000 && p < 0x0000800000000000 && (p & 7) == 0;

    public static ulong U64(this IReadMemory m, ulong a) => BinaryPrimitives.ReadUInt64LittleEndian(m.Read(a, 8));
    public static long I64(this IReadMemory m, ulong a) => unchecked((long)m.U64(a));
    public static int I32(this IReadMemory m, ulong a) => BinaryPrimitives.ReadInt32LittleEndian(m.Read(a, 4));
    public static float F32(this IReadMemory m, ulong a) => BitConverter.Int32BitsToSingle(m.I32(a));
    public static byte U8(this IReadMemory m, ulong a) => m.Read(a, 1)[0];

    public static ulong Pointer(this IReadMemory m, ulong a, string label)
    {
        var p = m.U64(a);
        if (!IsPointer(p)) throw new NoSession($"{label}尚未初始化");
        return p;
    }

    public static string? ManagedString(this IReadMemory m, ulong p)
    {
        if (!IsPointer(p)) return null;
        var len = m.I32(p + 0x10);
        if (len < 0 || len > 512) throw new ReadFault("运行时字符串长度无效。");
        return len == 0 ? "" : Encoding.Unicode.GetString(m.Read(p + 0x14, len * 2));
    }

    public static string? CString(this IReadMemory m, ulong p)
    {
        if (!IsPointer(p) && (p < 0x10000 || p >= 0x0000800000000000)) return null;
        var chars = new List<byte>();
        for (var i = 0; i < 128; i++)
        {
            var x = m.U8(p + (ulong)i);
            if (x == 0) break;
            chars.Add(x);
        }
        return Encoding.UTF8.GetString(chars.ToArray());
    }
}

sealed class ProcessMemory : IReadMemory, IDisposable
{
    const uint VmRead = 0x10, QueryLimited = 0x1000, SnapModule = 0x08, SnapModule32 = 0x10;
    readonly SafeProcessHandle _handle;
    public int Pid { get; }
    public ulong AssemblyBase { get; }
    public string AssemblyPath { get; }
    public bool Running => GetExitCodeProcess(_handle, out var code) && code == 259;
    public string Identity { get; }

    ProcessMemory(SafeProcessHandle handle, int pid, ulong baseAddress, string assemblyPath)
    {
        _handle = handle;
        Pid = pid;
        AssemblyBase = baseAddress;
        AssemblyPath = assemblyPath;
        GetProcessTimes(handle, out var creation, out _, out _, out _);
        Identity = $"{pid:X}:{creation:X}";
    }

    public static bool Elevated => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static ProcessMemory Attach(string expectedSha)
    {
        var session = Process.GetCurrentProcess().SessionId;
        var found = Process.GetProcessesByName("Arknights");
        try
        {
            var target = found.FirstOrDefault(p => p.SessionId == session && !p.HasExited);
            if (target == null) throw new NoSession("没有检测到当前桌面的《明日方舟》PC 客户端。");
            return Open(target.Id, expectedSha);
        }
        finally { foreach (var p in found) p.Dispose(); }
    }

    public static ProcessMemory Open(int pid, string expectedSha)
    {
        var handle = OpenProcess(VmRead | QueryLimited, false, pid);
        if (handle.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (err == 5)
                throw new NoSession(Elevated ? "当前管理员进程仍未获游戏只读权限（错误 5）。" : "Windows 拒绝读取（错误 5）。请使用工具内的「以管理员权限重开」。");
            throw new NoSession($"打开游戏只读句柄失败（错误 {err}）。");
        }
        try
        {
            var exe = new StringBuilder(1024);
            var len = exe.Capacity;
            if (!QueryFullProcessImageName(handle, 0, exe, ref len) || !Path.GetFileName(exe.ToString()).Equals("Arknights.exe", StringComparison.OrdinalIgnoreCase))
                throw new NoSession("目标进程身份不匹配。");
            using var snapshot = CreateToolhelp32Snapshot(SnapModule | SnapModule32, (uint)pid);
            if (snapshot.IsInvalid) throw new NoSession($"无法列举游戏模块（错误 {Marshal.GetLastWin32Error()}）。");
            var entry = new ModuleEntry { Size = (uint)Marshal.SizeOf<ModuleEntry>() };
            for (var found = Module32First(snapshot, ref entry); found; found = Module32Next(snapshot, ref entry))
            {
                if (!string.Equals(entry.Name, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase)) continue;
                using var file = File.Open(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var sha = Convert.ToHexString(SHA256.HashData(file));
                if (!sha.Equals(expectedSha, StringComparison.OrdinalIgnoreCase))
                    throw new NoSession("游戏程序已更新：这版观察器的字段布局与当前版本不同。");
                var memory = new ProcessMemory(handle, pid, (ulong)entry.Base.ToInt64(), entry.Path);
                if (!memory.Read(memory.AssemblyBase, 2).AsSpan().SequenceEqual("MZ"u8))
                    throw new ReadFault("无法验证目标模块的内存映像。");
                return memory;
            }
            throw new NoSession("正在等待游戏战斗模块加载。");
        }
        catch { handle.Dispose(); throw; }
    }

    public static int Probe(int pid)
    {
        using var h = OpenProcess(VmRead | QueryLimited, false, pid);
        return h.IsInvalid ? Marshal.GetLastWin32Error() : 0;
    }

    public static (string Text, int Error) ReadMarker(int processId, ulong address)
    {
        using var h = OpenProcess(VmRead | QueryLimited, false, processId);
        if (h.IsInvalid) return ("", Marshal.GetLastWin32Error());
        var b = new byte[15];
        if (!ReadProcessMemory(h, (IntPtr)address, b, (nuint)b.Length, out var n) || n != (nuint)b.Length)
            return ("", Marshal.GetLastWin32Error());
        return (Encoding.ASCII.GetString(b), 0);
    }

    public byte[] Read(ulong address, int size)
    {
        if (address < 0x10000 || address >= 0x0000800000000000 || size < 1 || size > 128 * 1024)
            throw new ReadFault("内存地址或读取长度不在预期范围。");
        var b = new byte[size];
        if (!ReadProcessMemory(_handle, (IntPtr)address, b, (nuint)size, out var n) || n != (nuint)size)
            throw new ReadFault($"本次内存读取不完整（错误 {Marshal.GetLastWin32Error()}）。");
        return b;
    }

    public void Dispose() => _handle.Dispose();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ModuleEntry
    {
        public uint Size, ModuleId, ProcessId, GlobalUsage, ProcessUsage;
        public IntPtr Base;
        public uint ModuleSize;
        public IntPtr ModuleHandle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path;
    }

    sealed class SafeSnapshot : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeSnapshot() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool ReadProcessMemory(SafeProcessHandle h, IntPtr address, [Out] byte[] b, nuint size, out nuint read);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(SafeProcessHandle h, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetProcessTimes(SafeProcessHandle h, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)] static extern SafeSnapshot CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Module32FirstW", SetLastError = true)] static extern bool Module32First(SafeSnapshot h, ref ModuleEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Module32NextW", SetLastError = true)] static extern bool Module32Next(SafeSnapshot h, ref ModuleEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(SafeProcessHandle h, int flags, StringBuilder b, ref int size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
