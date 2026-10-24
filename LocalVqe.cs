using System.Runtime.InteropServices;

namespace VoiceAec;

internal static class LocalVqe
{
    private const string Dll = "liblocalvqe.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern IntPtr localvqe_new(string modelPath);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr localvqe_options_new();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void localvqe_options_free(IntPtr opts);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private static extern int localvqe_options_set_model_path(IntPtr opts, string modelPath);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int localvqe_options_set_threads(IntPtr opts, int nThreads);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr localvqe_new_with_options(IntPtr opts);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void localvqe_free(IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr localvqe_last_error(IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int localvqe_sample_rate(IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int localvqe_hop_length(IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int localvqe_process_frame_f32(IntPtr ctx,
        float[] mic, float[] reference, int hopSamples, [Out] float[] output);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void localvqe_reset(IntPtr ctx);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int localvqe_set_noise_gate(IntPtr ctx, int enabled, float thresholdDbfs);

    public sealed class Context : IDisposable
    {
        public readonly IntPtr Handle;
        public readonly int SampleRate;
        public readonly int Hop;

        private Context(IntPtr h)
        {
            Handle = h;
            SampleRate = localvqe_sample_rate(h);
            Hop = localvqe_hop_length(h);
        }

        public static Context Create(string modelPath, int threads)
        {
            var opts = localvqe_options_new();
            if (opts == IntPtr.Zero)
                throw new Exception("localvqe_options_new failed");
            localvqe_options_set_model_path(opts, modelPath);
            if (threads > 0) localvqe_options_set_threads(opts, threads);
            var h = localvqe_new_with_options(opts);
            localvqe_options_free(opts);
            if (h == IntPtr.Zero)
                throw new Exception($"localvqe_new failed for '{modelPath}' (threads={threads})");
            return new Context(h);
        }

        /// <returns>0 on success; non-zero rc means output was replaced with mic passthrough.</returns>
        public int Process(float[] mic, float[] reference, float[] output)
        {
            var rc = localvqe_process_frame_f32(Handle, mic, reference, mic.Length, output);
            if (rc != 0)
            {
                var errPtr = localvqe_last_error(Handle);
                var err = errPtr == IntPtr.Zero ? "unknown" : Marshal.PtrToStringAnsi(errPtr) ?? "unknown";
                Console.Error.WriteLine($"[lvqe] process failed rc={rc}: {err}");
            }
            return rc;
        }

        public void Reset() => localvqe_reset(Handle);

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) localvqe_free(Handle);
            GC.SuppressFinalize(this);
        }
    }
}
