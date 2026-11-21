using System.Runtime.InteropServices;

namespace DocAgent.Infrastructure.Embeddings;

/// <summary>
/// ONNX Runtime allocates from glibc's malloc. Loading the model briefly needs two copies of its weights (the parsed file and the initializers) and
/// each batch allocates large temporary buffers; glibc keeps freed memory in its arenas instead of returning it to the OS, so the resident set stays at
/// its high-water mark. <c>malloc_trim</c> hands the free pages back. On a 512 MB container that is the difference between a comfortable and a dangerous peak.
/// A no-op everywhere that is not Linux with glibc.
/// </summary>
internal static class NativeHeap
{
    [DllImport("libc.so.6", EntryPoint = "malloc_trim")]
    private static extern int MallocTrim(nuint pad);

    public static void Trim()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        try
        {
            MallocTrim(0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // musl or an unusual libc: nothing to trim
        }
    }
}
