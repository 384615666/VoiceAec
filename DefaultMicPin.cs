using System.Runtime.InteropServices;

// 系统默认输入设备守护:把默认麦克风固定到虚拟声卡(CABLE Output),重启电脑后
// 不再被先就绪的实体设备抢走。Windows 没有公开的"设置默认设备"API,这里走
// PolicyConfig 未公开 COM 接口,只使用布局已知的 IPolicyConfig IID。
internal static class DefaultMicPin
{
    private static readonly Guid ClassPolicyConfig = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");

    // 不对其他 IID 复用槽位:不同版本的接口布局可能不同。
    private static readonly Guid[] InterfaceIids =
    {
        new("f8679f50-850a-41cf-9c72-430f290290c8"),
    };

    private const int SetDefaultEndpointSlot = 13;   // IUnknown 3 槽 + 第 11 个方法

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int SetDefaultEndpointDelegate(nint self, nint deviceId, int role);

    /// <summary>把指定采集端点设为全部三种角色(默认设备/默认通信设备)的默认输入。</summary>
    public static bool SetDefaultCapture(string endpointId)
    {
        var type = Type.GetTypeFromCLSID(ClassPolicyConfig);
        if (type == null) return false;
        object? client = null;
        try { client = Activator.CreateInstance(type); } catch { return false; }
        if (client == null) return false;

        var unk = Marshal.GetIUnknownForObject(client);
        try
        {
            var deviceId = Marshal.StringToCoTaskMemUni(endpointId);
            if (deviceId == nint.Zero) return false;
            try
            {
                foreach (var candidate in InterfaceIids)
                {
                    var iid = candidate;
                    if (Marshal.QueryInterface(unk, in iid, out var policy) != 0) continue;
                    try
                    {
                        var vtable = Marshal.ReadIntPtr(policy);
                        var slot = Marshal.ReadIntPtr(vtable, SetDefaultEndpointSlot * nint.Size);
                        var setDefault = Marshal.GetDelegateForFunctionPointer<SetDefaultEndpointDelegate>(slot);
                        var ok = true;
                        for (var role = 0; role <= 2 && ok; role++)   // Console / Multimedia / Communications
                            ok = setDefault(policy, deviceId, role) >= 0;
                        if (ok) return true;
                    }
                    finally { Marshal.Release(policy); }
                }
                return false;
            }
            finally { Marshal.FreeCoTaskMem(deviceId); }
        }
        finally
        {
            Marshal.Release(unk);
            if (Marshal.IsComObject(client)) Marshal.ReleaseComObject(client);
        }
    }
}
