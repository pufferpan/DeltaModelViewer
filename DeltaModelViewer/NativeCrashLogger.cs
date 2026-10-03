using System.Runtime.InteropServices;
using DeltaModelViewer.Core;

namespace DeltaModelViewer;

/// <summary>
/// 原生异常兜底记录：用 VEH（向量异常处理器）在进程崩溃前把致命异常码、
/// 地址和所属模块写进日志，用于定位原生层闪退。
/// 注意：处理器必须保持极轻量（不能碰进程 API、不能锁），模块表在安装时缓存一次。
/// 仅 x64。
/// </summary>
internal static unsafe class NativeCrashLogger
{
    private readonly struct ModuleInfo
    {
        public readonly long Base;
        public readonly long Size;
        public readonly string Name;
        public ModuleInfo(long b, long s, string n) { Base = b; Size = s; Name = n; }
    }

    private static ModuleInfo[] _modules = Array.Empty<ModuleInfo>();

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int VectoredHandler(ExceptionPointers* exceptionInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct ExceptionPointers
    {
        public ExceptionRecord* ExceptionRecord;
        public void* Context;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExceptionRecord
    {
        public uint ExceptionCode;
        public uint ExceptionFlags;
        public ExceptionRecord* Inner;
        public IntPtr ExceptionAddress;
        public uint NumberParameters;
        public fixed ulong ExceptionInformation[15];
    }

    private static readonly VectoredHandler HandlerDelegate = OnException;

    [DllImport("kernel32.dll")]
    private static extern IntPtr AddVectoredExceptionHandler(uint first, VectoredHandler handler);

    public static void Install()
    {
        try
        {
            // 缓存模块表（只做一次，处理器内不再碰任何进程 API）
            var list = new List<ModuleInfo>();
            foreach (System.Diagnostics.ProcessModule m in System.Diagnostics.Process.GetCurrentProcess().Modules)
                list.Add(new ModuleInfo(m.BaseAddress.ToInt64(), m.ModuleMemorySize, m.ModuleName!));
            _modules = list.ToArray();
            AddVectoredExceptionHandler(1, HandlerDelegate);
        }
        catch
        {
            // 安装失败不影响主流程
        }
    }

    private static int OnException(ExceptionPointers* ep)
    {
        try
        {
            uint code = ep->ExceptionRecord->ExceptionCode;
            // 只记录真正的致命异常，跳过 .NET/调试器常见的良性一等异常
            if (IsFatal(code))
            {
                long addr = ep->ExceptionRecord->ExceptionAddress.ToInt64();
                Log.Error($"原生异常 0x{code:X8} @ 0x{addr:X} ({FindModule(addr)})");
            }
        }
        catch
        {
            // 崩溃路径里日志失败可接受
        }
        return 0; // EXCEPTION_CONTINUE_SEARCH
    }

    private static bool IsFatal(uint code) => code switch
    {
        0xC0000005 or // 访问冲突
        0xC00000FD or // 栈溢出
        0xC000001D or // 非法指令
        0xC0000006 or // 页内错误
        0xC0000094 or // 除零
        0xC0000374 or // 堆损坏
        0xC0000409 or // fail-fast
        0xC0000602 => true, // fast-fail
        _ => false,
    };

    private static string FindModule(long addr)
    {
        foreach (var m in _modules)
        {
            if (addr >= m.Base && addr < m.Base + m.Size)
                return m.Name;
        }
        return "未知模块";
    }
}
