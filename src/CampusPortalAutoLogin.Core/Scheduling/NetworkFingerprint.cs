using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CampusPortalAutoLogin.Core.Scheduling;

internal static class NetworkFingerprint
{
    // 只在内存中比较摘要，不把地址、网关或 Wi-Fi 名称写入日志。
    public static string Read()
    {
        var parts = new List<string>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().OrderBy(a => a.Id))
        {
            parts.Add(adapter.Id + ":" + adapter.OperationalStatus);
            if (adapter.OperationalStatus != OperationalStatus.Up) continue;
            var properties = adapter.GetIPProperties();
            parts.AddRange(properties.UnicastAddresses.Select(a => a.Address.ToString()).OrderBy(x => x));
            parts.AddRange(properties.GatewayAddresses.Select(a => a.Address.ToString()).OrderBy(x => x));
            if (adapter.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 && Guid.TryParse(adapter.Id, out var id)) parts.Add(ReadSsid(id));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", parts))));
    }
    private static string ReadSsid(Guid id)
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var handle) != 0) return "";
        IntPtr data = IntPtr.Zero;
        try
        {
            // wlan_intf_opcode_current_connection = 7，SSID 位于 WLAN_CONNECTION_ATTRIBUTES 的 520 字节处。
            if (WlanQueryInterface(handle, ref id, 7, IntPtr.Zero, out var size, out data, out _) != 0 || size < 556) return "";
            var length = Marshal.ReadInt32(data, 520);
            if (length is < 0 or > 32) return "";
            var ssid = new byte[length]; Marshal.Copy(IntPtr.Add(data, 524), ssid, 0, length);
            return Convert.ToHexString(ssid);
        }
        finally { if (data != IntPtr.Zero) WlanFreeMemory(data); WlanCloseHandle(handle, IntPtr.Zero); }
    }
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiatedVersion, out IntPtr client);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr client, ref Guid interfaceGuid, uint opcode, IntPtr reserved, out uint dataSize, out IntPtr data, out uint opcodeType);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr client, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
}
