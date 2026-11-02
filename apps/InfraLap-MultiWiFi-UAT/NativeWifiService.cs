using System.Runtime.InteropServices;
using System.Text;
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
    private const int CurrentConnectionOpcode = 7;
    private const int SecondaryStaInterfacesOpcode = 17;
    private const int SecondaryStaSynchronizedConnectionsOpcode = 18;
    private const uint ProfileGetPlaintextKey = 0x00000004;
    private static IntPtr _dualStaHandle = IntPtr.Zero;
    private static Guid? _dualStaPrimaryGuid;
    private static Guid? _dualStaSecondaryGuid;
    private static readonly object DualStaLock = new();

    private enum WlanConnectionMode { Profile = 0, TemporaryProfile = 1, DiscoverySecure = 2, DiscoveryUnsecure = 3, Auto = 4, Invalid = 5 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WlanConnectionParameters
    {
        public WlanConnectionMode ConnectionMode;
        [MarshalAs(UnmanagedType.LPWStr)] public string ProfileName;
        public IntPtr Dot11Ssid;
        public IntPtr DesiredBssidList;
        public int Dot11BssType;
        public uint Flags;
    }

    internal sealed record SecondaryStaAttempt(string Message, Guid SecondaryGuid);
    internal sealed record SecondaryStaVerification(bool IsIndependentConnection, string PrimarySsid, string SecondarySsid, string Message);

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
    private static extern uint WlanSetInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opcode,
        uint dataSize, IntPtr data, IntPtr reserved);

    [DllImport("wlanapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint WlanSetProfile(IntPtr clientHandle, ref Guid interfaceGuid, uint flags,
        string profileXml, IntPtr allUserProfileSecurity, [MarshalAs(UnmanagedType.Bool)] bool overwrite,
        IntPtr reserved, out uint reasonCode);

    [DllImport("wlanapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint WlanConnect(IntPtr clientHandle, ref Guid interfaceGuid,
        ref WlanConnectionParameters connectionParameters, IntPtr reserved);

    [DllImport("wlanapi.dll", ExactSpelling = true)]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    public static SecondaryStaAttempt TryConnectSecondarySta(string adapterDescription, string ssid, string password)
    {
        lock (DualStaLock)
        {
            ReleaseDualStaInternal();
            var error = WlanOpenHandle(2, IntPtr.Zero, out _, out _dualStaHandle);
            if (error != 0) throw new InvalidOperationException("WlanOpenHandle failed (Windows error " + error + ").");

            IntPtr primaryList = IntPtr.Zero;
            IntPtr secondaryList = IntPtr.Zero;
            IntPtr enableData = IntPtr.Zero;
            try
            {
                error = WlanEnumInterfaces(_dualStaHandle, IntPtr.Zero, out primaryList);
                if (error != 0) throw new InvalidOperationException("WlanEnumInterfaces failed (Windows error " + error + ").");

                var count = Marshal.ReadInt32(primaryList);
                var infoSize = Marshal.SizeOf<WlanInterfaceInfo>();
                WlanInterfaceInfo? primary = null;
                for (var i = 0; i < count; i++)
                {
                    var current = Marshal.PtrToStructure<WlanInterfaceInfo>(IntPtr.Add(primaryList, 8 + i * infoSize));
                    if (string.Equals(current.Description, adapterDescription, StringComparison.OrdinalIgnoreCase))
                    {
                        primary = current;
                        break;
                    }
                }
                if (primary is null) throw new InvalidOperationException("The selected adapter was not found in the Native Wi-Fi interface list.");

                var primaryInfo = primary.Value;
                var primarySsid = GetCurrentSsid(_dualStaHandle, primaryInfo.InterfaceGuid, out var primaryState);
                if (primaryState != 1 || string.IsNullOrWhiteSpace(primarySsid))
                    throw new InvalidOperationException("The selected primary adapter is not connected. Connect Connection 1 first.");
                _dualStaPrimaryGuid = primaryInfo.InterfaceGuid;

                uint dataSize;
                error = WlanQueryInterface(_dualStaHandle, ref primaryInfo.InterfaceGuid,
                    SecondaryStaInterfacesOpcode, IntPtr.Zero, out dataSize, out secondaryList, IntPtr.Zero);
                if (error != 0) throw new InvalidOperationException("Secondary-STA query failed (Windows error " + error + ").");

                var secondaryCount = secondaryList == IntPtr.Zero ? 0 : Marshal.ReadInt32(secondaryList);
                if (secondaryCount == 0)
                    throw new InvalidOperationException("This Wi-Fi driver exposes zero secondary STA interfaces. The single-adapter test cannot proceed.");

                var secondaryInfo = Marshal.PtrToStructure<WlanInterfaceInfo>(IntPtr.Add(secondaryList, 8));
                var secondaryGuid = secondaryInfo.InterfaceGuid;
                _dualStaSecondaryGuid = secondaryGuid;

                enableData = Marshal.AllocHGlobal(sizeof(int));
                Marshal.WriteInt32(enableData, 1);
                error = WlanSetInterface(_dualStaHandle, ref primaryInfo.InterfaceGuid,
                    SecondaryStaSynchronizedConnectionsOpcode, sizeof(int), enableData, IntPtr.Zero);
                if (error != 0)
                    throw new InvalidOperationException("Windows rejected the secondary-STA enable request (error " + error + ").");

                const string profileName = "InfraLap-MultiWiFi-SecondarySTA";
                var profileXml = BuildWpa2Profile(profileName, ssid, password);
                uint reasonCode;
                error = WlanSetProfile(_dualStaHandle, ref secondaryGuid, 0, profileXml,
                    IntPtr.Zero, true, IntPtr.Zero, out reasonCode);
                if (error != 0)
                    throw new InvalidOperationException("Windows rejected saving the WPA2 profile to the secondary STA GUID (error "
                        + error + "; WLAN reason code " + reasonCode + "). The driver may not allow per-secondary-interface profiles.");

                var parameters = new WlanConnectionParameters
                {
                    ConnectionMode = WlanConnectionMode.Profile,
                    ProfileName = profileName,
                    Dot11Ssid = IntPtr.Zero,
                    Dot11BssType = 3,
                    DesiredBssidList = IntPtr.Zero,
                    Flags = 0
                };
                error = WlanConnect(_dualStaHandle, ref secondaryGuid, ref parameters, IntPtr.Zero);
                if (error != 0)
                    throw new InvalidOperationException("WlanConnect on the secondary STA GUID was rejected (Windows error "
                        + error + "). Windows/driver may reserve this interface for synchronized connectivity.");

                return new SecondaryStaAttempt(
                    "WlanConnect request accepted for '" + ssid + "' on secondary STA GUID " + secondaryGuid
                    + ". This is only an accepted request, not a confirmed connection.",
                    secondaryGuid);
            }
            catch
            {
                ReleaseDualStaInternal();
                throw;
            }
            finally
            {
                if (enableData != IntPtr.Zero) Marshal.FreeHGlobal(enableData);
                if (secondaryList != IntPtr.Zero) WlanFreeMemory(secondaryList);
                if (primaryList != IntPtr.Zero) WlanFreeMemory(primaryList);
            }
        }
    }

    public static SecondaryStaVerification VerifySecondaryStaConnection(string expectedSsid)
    {
        lock (DualStaLock)
        {
            if (_dualStaHandle == IntPtr.Zero || _dualStaPrimaryGuid is null || _dualStaSecondaryGuid is null)
                return new SecondaryStaVerification(false, "", "", "No active secondary-STA session. Retry the experimental connection test.");

            try
            {
                var primarySsid = GetCurrentSsid(_dualStaHandle, _dualStaPrimaryGuid.Value, out var primaryState);
                var secondarySsid = GetCurrentSsid(_dualStaHandle, _dualStaSecondaryGuid.Value, out var secondaryState);

                if (primaryState != 1 || string.IsNullOrWhiteSpace(primarySsid))
                    return new SecondaryStaVerification(false, primarySsid, secondarySsid,
                        "Primary connection is no longer reported as connected. Two links are not verified.");
                if (secondaryState != 1 || string.IsNullOrWhiteSpace(secondarySsid))
                    return new SecondaryStaVerification(false, primarySsid, secondarySsid,
                        "Windows has not reported the secondary STA as connected. The driver may manage it automatically or reject an app-selected SSID.");
                if (!string.Equals(primarySsid, secondarySsid, StringComparison.Ordinal))
                {
                    var expected = string.Equals(secondarySsid, expectedSsid, StringComparison.Ordinal);
                    return new SecondaryStaVerification(expected, primarySsid, secondarySsid,
                        expected ? "Two different SSIDs were verified on primary and secondary STA."
                            : "Secondary STA connected to '" + secondarySsid + "', not requested SSID '" + expectedSsid + "'.");
                }
                return new SecondaryStaVerification(false, primarySsid, secondarySsid,
                    "The driver synchronized the secondary STA to the same SSID as the primary ('" + primarySsid + "').");
            }
            catch (Exception ex)
            {
                return new SecondaryStaVerification(false, "", "", "Secondary connection verification failed: " + ex.Message);
            }
        }
    }

    public static string ReleaseDualSta()
    {
        lock (DualStaLock)
        {
            var hadSession = _dualStaHandle != IntPtr.Zero;
            ReleaseDualStaInternal();
            return hadSession
                ? "Secondary STA request released. Windows may tear down the secondary connection after the WLAN handle is closed."
                : "No active secondary STA session.";
        }
    }

    private static void ReleaseDualStaInternal()
    {
        if (_dualStaHandle != IntPtr.Zero)
        {
            if (_dualStaPrimaryGuid is Guid primaryGuid)
            {
                var disableData = Marshal.AllocHGlobal(sizeof(int));
                try
                {
                    Marshal.WriteInt32(disableData, 0);
                    WlanSetInterface(_dualStaHandle, ref primaryGuid, SecondaryStaSynchronizedConnectionsOpcode,
                        sizeof(int), disableData, IntPtr.Zero);
                }
                catch { }
                finally { Marshal.FreeHGlobal(disableData); }
            }
            WlanCloseHandle(_dualStaHandle, IntPtr.Zero);
            _dualStaHandle = IntPtr.Zero;
        }
        _dualStaPrimaryGuid = null;
        _dualStaSecondaryGuid = null;
    }

    private static string BuildWpa2Profile(string profileName, string ssid, string password)
    {
        XNamespace ns = "http://www.microsoft.com/networking/WLAN/profile/v1";
        var ssidBytes = Encoding.UTF8.GetBytes(ssid);
        var document = new XDocument(new XElement(ns + "WLANProfile",
            new XElement(ns + "name", profileName),
            new XElement(ns + "SSIDConfig", new XElement(ns + "SSID",
                new XElement(ns + "hex", Convert.ToHexString(ssidBytes)),
                new XElement(ns + "name", ssid))),
            new XElement(ns + "connectionType", "ESS"),
            new XElement(ns + "connectionMode", "manual"),
            new XElement(ns + "MSM", new XElement(ns + "security",
                new XElement(ns + "authEncryption",
                    new XElement(ns + "authentication", "WPA2PSK"),
                    new XElement(ns + "encryption", "AES"),
                    new XElement(ns + "useOneX", "false")),
                new XElement(ns + "sharedKey",
                    new XElement(ns + "keyType", "passPhrase"),
                    new XElement(ns + "protected", "false"),
                    new XElement(ns + "keyMaterial", password))))));
        return document.ToString(SaveOptions.DisableFormatting);
    }

    private static string GetCurrentSsid(IntPtr handle, Guid interfaceGuid, out int state)
    {
        IntPtr data = IntPtr.Zero;
        state = 0;
        try
        {
            uint size;
            var error = WlanQueryInterface(handle, ref interfaceGuid, CurrentConnectionOpcode,
                IntPtr.Zero, out size, out data, IntPtr.Zero);
            if (error != 0)
                throw new InvalidOperationException("Current-connection query failed for interface "
                    + interfaceGuid + " (Windows error " + error + ").");
            if (data == IntPtr.Zero || size < 524)
                throw new InvalidOperationException("Windows returned an incomplete current-connection structure.");

            state = Marshal.ReadInt32(data);
            if (state != 1) return "";
            var length = Math.Clamp(Marshal.ReadInt32(data, 520), 0, 32);
            if (length == 0) return "";
            var bytes = new byte[length];
            Marshal.Copy(IntPtr.Add(data, 524), bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            if (data != IntPtr.Zero) WlanFreeMemory(data);
        }
    }

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
