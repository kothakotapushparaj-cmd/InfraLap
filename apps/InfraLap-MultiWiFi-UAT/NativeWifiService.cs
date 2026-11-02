using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace InfraLap.MultiWiFi;

internal sealed record SavedWifiProfile(
    Guid AdapterGuid,
    string AdapterName,
    string ProfileName,
    string Ssid,
    string Authentication,
    string? Password,
    bool PasswordIsProtected,
    string PasswordStatus);

internal static class NativeWifiService
{
    private const int SecondaryStaInterfacesOpcode = 17;
    private const int SecondaryStaSynchronizedConnectionsOpcode = 18;
    private const uint ProfileGetPlaintextKey = 0x00000004;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Description;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanProfileInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string ProfileName;
        public uint Flags;
    }

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanGetProfileList(IntPtr clientHandle, ref Guid interfaceGuid, IntPtr reserved, out IntPtr profileList);

    [DllImport("wlanapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint WlanGetProfile(IntPtr clientHandle, ref Guid interfaceGuid, string profileName,
        IntPtr reserved, out IntPtr profileXml, ref uint flags, out uint grantedAccess);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opcode,
        IntPtr reserved, out uint dataSize, out IntPtr data, IntPtr valueType);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern void WlanFreeMemory(IntPtr memory);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    public static List<SavedWifiProfile> GetSavedProfiles()
    {
        var result = new List<SavedWifiProfile>();
        IntPtr handle = IntPtr.Zero;
        IntPtr interfaceList = IntPtr.Zero;

        try
        {
            var error = WlanOpenHandle(2, IntPtr.Zero, out _, out handle);
            if (error != 0) throw new InvalidOperationException("WlanOpenHandle failed. Windows error: " + error);

            error = WlanEnumInterfaces(handle, IntPtr.Zero, out interfaceList);
            if (error != 0) throw new InvalidOperationException("WlanEnumInterfaces failed. Windows error: " + error);

            var interfaceCount = Marshal.ReadInt32(interfaceList);
            var interfaceSize = Marshal.SizeOf<WlanInterfaceInfo>();

            for (var i = 0; i < interfaceCount; i++)
            {
                var interfacePointer = IntPtr.Add(interfaceList, 8 + (i * interfaceSize));
                var adapter = Marshal.PtrToStructure<WlanInterfaceInfo>(interfacePointer);
                IntPtr profileList = IntPtr.Zero;

                try
                {
                    error = WlanGetProfileList(handle, ref adapter.InterfaceGuid, IntPtr.Zero, out profileList);
                    if (error != 0) continue;

                    var profileCount = Marshal.ReadInt32(profileList);
                    var profileSize = Marshal.SizeOf<WlanProfileInfo>();

                    for (var j = 0; j < profileCount; j++)
                    {
                        var profilePointer = IntPtr.Add(profileList, 8 + (j * profileSize));
                        var profileInfo = Marshal.PtrToStructure<WlanProfileInfo>(profilePointer);
                        var flags = ProfileGetPlaintextKey;
                        var grantedAccess = 0u;
                        IntPtr xmlPointer = IntPtr.Zero;
                        string? xml = null;
                        string passwordStatus = "";

                        try
                        {
                            error = WlanGetProfile(handle, ref adapter.InterfaceGuid, profileInfo.ProfileName,
                                IntPtr.Zero, out xmlPointer, ref flags, out grantedAccess);
                            if (error == 0 && xmlPointer != IntPtr.Zero)
                                xml = Marshal.PtrToStringUni(xmlPointer);
                            else
                                passwordStatus = "Windows could not read this profile (error " + error + ").";
                        }
                        finally
                        {
                            if (xmlPointer != IntPtr.Zero) WlanFreeMemory(xmlPointer);
                        }

                        string ssid = profileInfo.ProfileName;
                        string authentication = "Unknown";
                        string? password = null;
                        var passwordProtected = false;

                        if (!string.IsNullOrWhiteSpace(xml))
                        {
                            try
                            {
                                var document = XDocument.Parse(xml);
                                var ssidElement = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "SSID");
                                var ssidName = ssidElement?.Elements().FirstOrDefault(e => e.Name.LocalName == "name")?.Value;
                                if (!string.IsNullOrWhiteSpace(ssidName)) ssid = ssidName.Trim();

                                authentication = document.Descendants()
                                    .FirstOrDefault(e => e.Name.LocalName == "authentication")?.Value ?? "Unknown";

                                var sharedKey = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "sharedKey");
                                if (sharedKey is not null)
                                {
                                    var protectedValue = sharedKey.Elements().FirstOrDefault(e => e.Name.LocalName == "protected")?.Value;
                                    var keyMaterial = sharedKey.Elements().FirstOrDefault(e => e.Name.LocalName == "keyMaterial")?.Value;
                                    passwordProtected = string.Equals(protectedValue, "true", StringComparison.OrdinalIgnoreCase);
                                    if (!string.IsNullOrEmpty(keyMaterial) && !passwordProtected)
                                        password = keyMaterial;
                                    else if (passwordProtected)
                                        passwordStatus = "Windows returned an encrypted key. Administrator permission is normally required for plain-text retrieval.";
                                }
                                else
                                {
                                    passwordStatus = "This profile does not contain a shared Wi-Fi password.";
                                }
                            }
                            catch (Exception ex)
                            {
                                passwordStatus = "Could not parse the Windows profile XML: " + ex.Message;
                            }
                        }

                        result.Add(new SavedWifiProfile(
                            adapter.InterfaceGuid,
                            adapter.Description ?? "",
                            profileInfo.ProfileName ?? "",
                            ssid,
                            authentication,
                            password,
                            passwordProtected,
                            passwordStatus));
                    }
                }
                finally
                {
                    if (profileList != IntPtr.Zero) WlanFreeMemory(profileList);
                }
            }
        }
        finally
        {
            if (interfaceList != IntPtr.Zero) WlanFreeMemory(interfaceList);
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }

        return result.OrderBy(x => x.Ssid, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.AdapterName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string GetDualStaSummary()
    {
        IntPtr handle = IntPtr.Zero;
        IntPtr interfaceList = IntPtr.Zero;
        var lines = new List<string>();
        var anySecondary = false;

        try
        {
            var error = WlanOpenHandle(2, IntPtr.Zero, out _, out handle);
            if (error != 0) return "Dual-STA query could not open WLAN API (Windows error " + error + ").";

            error = WlanEnumInterfaces(handle, IntPtr.Zero, out interfaceList);
            if (error != 0) return "Dual-STA query could not enumerate interfaces (Windows error " + error + ").";

            var interfaceCount = Marshal.ReadInt32(interfaceList);
            var interfaceSize = Marshal.SizeOf<WlanInterfaceInfo>();
            if (interfaceCount == 0) return "Dual-STA: no primary Wi-Fi interface was reported.";

            for (var i = 0; i < interfaceCount; i++)
            {
                var pointer = IntPtr.Add(interfaceList, 8 + (i * interfaceSize));
                var adapter = Marshal.PtrToStructure<WlanInterfaceInfo>(pointer);
                IntPtr secondaryList = IntPtr.Zero;
                IntPtr syncData = IntPtr.Zero;

                try
                {
                    uint dataSize;
                    error = WlanQueryInterface(handle, ref adapter.InterfaceGuid, SecondaryStaInterfacesOpcode,
                        IntPtr.Zero, out dataSize, out secondaryList, IntPtr.Zero);

                    if (error != 0)
                    {
                        lines.Add(adapter.Description + ": secondary STA query failed (Windows error " + error + ").");
                        continue;
                    }

                    var secondaryCount = secondaryList == IntPtr.Zero ? 0 : Marshal.ReadInt32(secondaryList);
                    anySecondary |= secondaryCount > 0;
                    var syncText = "state unavailable";

                    if (secondaryCount > 0)
                    {
                        var syncError = WlanQueryInterface(handle, ref adapter.InterfaceGuid,
                            SecondaryStaSynchronizedConnectionsOpcode, IntPtr.Zero,
                            out dataSize, out syncData, IntPtr.Zero);
                        if (syncError == 0 && syncData != IntPtr.Zero)
                            syncText = Marshal.ReadInt32(syncData) != 0 ? "synchronized connections enabled" : "synchronized connections disabled";
                        else
                            syncText = "synchronization state query failed (Windows error " + syncError + ")";
                    }

                    lines.Add(adapter.Description + ": " + secondaryCount + " secondary STA interface(s); " + syncText + ".");
                }
                finally
                {
                    if (syncData != IntPtr.Zero) WlanFreeMemory(syncData);
                    if (secondaryList != IntPtr.Zero) WlanFreeMemory(secondaryList);
                }
            }

            if (anySecondary)
                lines.Add("Important: Windows documents secondary STA connectivity as driver-managed and synchronized with the primary connection; this API does not expose an independent second-SSID/password selector.");
            else
                lines.Add("No secondary STA interface is currently reported. A single-adapter second-SSID mode is not confirmed for this driver.");
        }
        catch (Exception ex)
        {
            return "Dual-STA check failed: " + ex.Message;
        }
        finally
        {
            if (interfaceList != IntPtr.Zero) WlanFreeMemory(interfaceList);
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }

        return string.Join(Environment.NewLine, lines);
    }
}
