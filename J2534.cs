using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace CanScanmatik;

internal sealed class PassThruDriverInfo
{
    public PassThruDriverInfo(string registryName, string vendor, string name, string functionLibrary, string? configApplication)
    {
        RegistryName = registryName;
        Vendor = vendor;
        Name = name;
        FunctionLibrary = functionLibrary;
        ConfigApplication = configApplication;
    }

    public string RegistryName { get; }
    public string Vendor { get; }
    public string Name { get; }
    public string FunctionLibrary { get; }
    public string? ConfigApplication { get; }
    public string DisplayName => string.IsNullOrWhiteSpace(Vendor) ? Name : $"{Vendor} {Name}";

    public override string ToString()
    {
        return DisplayName;
    }
}

internal static class PassThruRegistry
{
    private const string BaseKeyPath = @"SOFTWARE\PassThruSupport.04.04";

    public static IReadOnlyList<PassThruDriverInfo> GetInstalledDrivers()
    {
        Dictionary<string, PassThruDriverInfo> drivers = new(StringComparer.OrdinalIgnoreCase);

        ReadFromView(RegistryView.Registry32, drivers);
        ReadFromView(RegistryView.Registry64, drivers);

        return drivers.Values
            .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void ReadFromView(RegistryView view, IDictionary<string, PassThruDriverInfo> drivers)
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using RegistryKey? passThruKey = baseKey.OpenSubKey(BaseKeyPath);
            if (passThruKey is null)
            {
                return;
            }

            foreach (string subKeyName in passThruKey.GetSubKeyNames())
            {
                using RegistryKey? deviceKey = passThruKey.OpenSubKey(subKeyName);
                if (deviceKey is null)
                {
                    continue;
                }

                string functionLibrary = Convert.ToString(deviceKey.GetValue("FunctionLibrary")) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(functionLibrary))
                {
                    continue;
                }

                string vendor = Convert.ToString(deviceKey.GetValue("Vendor")) ?? string.Empty;
                string name = Convert.ToString(deviceKey.GetValue("Name")) ?? subKeyName;
                string? configApplication = Convert.ToString(deviceKey.GetValue("ConfigApplication"));

                drivers[functionLibrary] = new PassThruDriverInfo(subKeyName, vendor, name, functionLibrary, configApplication);
            }
        }
        catch
        {
            // Ignore registry access issues and return what we can read from other views.
        }
    }
}

internal enum PassThruProtocolId : uint
{
    Can = 5,
    CanPs = 0x00008004
}

internal static class PassThruFlags
{
    public const uint Can29BitId = 0x00000100;
}

internal enum PassThruIoctlId : uint
{
    SetConfig = 0x02
}

internal enum PassThruConfigId : uint
{
    J1962Pins = 0x8001
}

internal enum PassThruFilterType : uint
{
    PassFilter = 1
}

internal enum PassThruStatus : uint
{
    NoError = 0x00,
    ErrNotSupported = 0x01,
    ErrInvalidChannelId = 0x02,
    ErrInvalidProtocolId = 0x03,
    ErrNullParameter = 0x04,
    ErrInvalidIoctlValue = 0x05,
    ErrInvalidFlags = 0x06,
    ErrFailed = 0x07,
    ErrDeviceNotConnected = 0x08,
    ErrTimeout = 0x09,
    ErrInvalidMsg = 0x0A,
    ErrInvalidTimeInterval = 0x0B,
    ErrExceededLimit = 0x0C,
    ErrInvalidMsgId = 0x0D,
    ErrDeviceInUse = 0x0E,
    ErrInvalidIoctlId = 0x0F,
    ErrBufferEmpty = 0x10,
    ErrBufferFull = 0x11,
    ErrBufferOverflow = 0x12,
    ErrPinInvalid = 0x13,
    ErrChannelInUse = 0x14,
    ErrMsgProtocolId = 0x15,
    ErrInvalidFilterId = 0x16,
    ErrNoFlowControl = 0x17,
    ErrNotUnique = 0x18,
    ErrInvalidBaudrate = 0x19,
    ErrInvalidDeviceId = 0x1A
}

internal readonly record struct CanFrame(uint Id, bool IsExtended, int Dlc, byte[] Data, uint DeviceTimestamp, DateTime ReceivedAtUtc);

internal sealed class PassThruException : Exception
{
    public PassThruException(string operation, PassThruStatus status, string? driverError)
        : base(BuildMessage(operation, status, driverError))
    {
        Operation = operation;
        Status = status;
        DriverError = driverError;
    }

    public string Operation { get; }
    public PassThruStatus Status { get; }
    public string? DriverError { get; }

    private static string BuildMessage(string operation, PassThruStatus status, string? driverError)
    {
        if (string.IsNullOrWhiteSpace(driverError))
        {
            return $"{operation} failed: {status} (0x{(uint)status:X2}).";
        }

        return $"{operation} failed: {status} (0x{(uint)status:X2}) - {driverError.Trim()}";
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct PassThruMsg
{
    public uint ProtocolId;
    public uint RxStatus;
    public uint TxFlags;
    public uint Timestamp;
    public uint DataSize;
    public uint ExtraDataIndex;

    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4128)]
    public byte[] Data;

    public static PassThruMsg Create()
    {
        return new PassThruMsg
        {
            Data = new byte[4128]
        };
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct PassThruSConfig
{
    public uint Parameter;
    public uint Value;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PassThruSConfigList
{
    public uint NumOfParams;
    public IntPtr ConfigPtr;
}

internal sealed class PassThruApi : IDisposable
{
    private const int ReadVersionBufferLength = 80;
    private const int LastErrorBufferLength = 256;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate PassThruStatus PassThruOpenDelegate(IntPtr name, out uint deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate PassThruStatus PassThruCloseDelegate(uint deviceId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate PassThruStatus PassThruConnectDelegate(uint deviceId, uint protocolId, uint flags, uint baudRate, out uint channelId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate PassThruStatus PassThruDisconnectDelegate(uint channelId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate PassThruStatus PassThruReadMsgsDelegate(uint channelId, [In, Out] PassThruMsg[] messages, ref uint numMsgs, uint timeout);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate PassThruStatus PassThruWriteMsgsDelegate(uint channelId, [In, Out] PassThruMsg[] messages, ref uint numMsgs, uint timeout);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate PassThruStatus PassThruStartMsgFilterDelegate(uint channelId, uint filterType, ref PassThruMsg maskMessage, ref PassThruMsg patternMessage, IntPtr flowControlMessage, ref uint filterId);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate PassThruStatus PassThruIoctlDelegate(uint handleId, uint ioctlId, IntPtr inputPtr, IntPtr outputPtr);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate PassThruStatus PassThruReadVersionDelegate(uint deviceId, StringBuilder firmwareVersion, StringBuilder dllVersion, StringBuilder apiVersion);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate PassThruStatus PassThruGetLastErrorDelegate(StringBuilder errorDescription);

    private readonly nint nativeLibraryHandle;
    private readonly PassThruOpenDelegate passThruOpen;
    private readonly PassThruCloseDelegate passThruClose;
    private readonly PassThruConnectDelegate passThruConnect;
    private readonly PassThruDisconnectDelegate passThruDisconnect;
    private readonly PassThruReadMsgsDelegate passThruReadMsgs;
    private readonly PassThruWriteMsgsDelegate passThruWriteMsgs;
    private readonly PassThruStartMsgFilterDelegate passThruStartMsgFilter;
    private readonly PassThruIoctlDelegate passThruIoctl;
    private readonly PassThruReadVersionDelegate passThruReadVersion;
    private readonly PassThruGetLastErrorDelegate passThruGetLastError;
    private readonly object syncRoot = new();

    private uint deviceId;
    private uint channelId;
    private PassThruProtocolId currentCanProtocolId = PassThruProtocolId.Can;
    private bool isDisposed;

    public PassThruApi(PassThruDriverInfo driver)
    {
        Driver = driver ?? throw new ArgumentNullException(nameof(driver));

        if (!File.Exists(driver.FunctionLibrary))
        {
            throw new FileNotFoundException("J2534 DLL was not found.", driver.FunctionLibrary);
        }

        nativeLibraryHandle = NativeLibrary.Load(driver.FunctionLibrary);
        passThruOpen = GetDelegate<PassThruOpenDelegate>("PassThruOpen");
        passThruClose = GetDelegate<PassThruCloseDelegate>("PassThruClose");
        passThruConnect = GetDelegate<PassThruConnectDelegate>("PassThruConnect");
        passThruDisconnect = GetDelegate<PassThruDisconnectDelegate>("PassThruDisconnect");
        passThruReadMsgs = GetDelegate<PassThruReadMsgsDelegate>("PassThruReadMsgs");
        passThruWriteMsgs = GetDelegate<PassThruWriteMsgsDelegate>("PassThruWriteMsgs");
        passThruStartMsgFilter = GetDelegate<PassThruStartMsgFilterDelegate>("PassThruStartMsgFilter");
        passThruIoctl = GetDelegate<PassThruIoctlDelegate>("PassThruIoctl");
        passThruReadVersion = GetDelegate<PassThruReadVersionDelegate>("PassThruReadVersion");
        passThruGetLastError = GetDelegate<PassThruGetLastErrorDelegate>("PassThruGetLastError");
    }

    public PassThruDriverInfo Driver { get; }

    public (string FirmwareVersion, string DllVersion, string ApiVersion) OpenAndReadVersion()
    {
        EnsureNotDisposed();
        lock (syncRoot)
        {
            EnsureSuccess(passThruOpen(IntPtr.Zero, out deviceId), "PassThruOpen");

            StringBuilder firmwareVersion = new(ReadVersionBufferLength);
            StringBuilder dllVersion = new(ReadVersionBufferLength);
            StringBuilder apiVersion = new(ReadVersionBufferLength);

            EnsureSuccess(passThruReadVersion(deviceId, firmwareVersion, dllVersion, apiVersion), "PassThruReadVersion");
            return (firmwareVersion.ToString(), dllVersion.ToString(), apiVersion.ToString());
        }
    }

    public void ConnectCan(int baudRate, bool useExtendedIdentifiers, uint? j1962Pins = null)
    {
        EnsureNotDisposed();
        lock (syncRoot)
        {
            if (deviceId == 0)
            {
                throw new InvalidOperationException("PassThru device is not open.");
            }

            PassThruProtocolId protocolId = j1962Pins.HasValue ? PassThruProtocolId.CanPs : PassThruProtocolId.Can;
            uint flags = useExtendedIdentifiers ? PassThruFlags.Can29BitId : 0u;
            EnsureSuccess(passThruConnect(deviceId, (uint)protocolId, flags, (uint)baudRate, out channelId), "PassThruConnect");
            if (j1962Pins.HasValue)
            {
                SetJ1962Pins(j1962Pins.Value);
            }

            currentCanProtocolId = protocolId;
            TryStartCatchAllFilter();
        }
    }

    public IReadOnlyList<CanFrame> ReadCanFrames(int timeoutMs, int batchSize)
    {
        EnsureNotDisposed();
        lock (syncRoot)
        {
            if (channelId == 0)
            {
                return Array.Empty<CanFrame>();
            }

            PassThruMsg[] messages = CreateMessageBuffer(batchSize);
            uint numberOfMessages = (uint)messages.Length;
            PassThruStatus status = passThruReadMsgs(channelId, messages, ref numberOfMessages, (uint)Math.Max(timeoutMs, 0));
            if (status is PassThruStatus.ErrTimeout or PassThruStatus.ErrBufferEmpty)
            {
                return Array.Empty<CanFrame>();
            }

            EnsureSuccess(status, "PassThruReadMsgs");

            List<CanFrame> frames = new((int)numberOfMessages);
            for (int index = 0; index < numberOfMessages; index++)
            {
                CanFrame? frame = TryParse(messages[index]);
                if (frame.HasValue)
                {
                    frames.Add(frame.Value);
                }
            }

            return frames;
        }
    }

    public void WriteCanFrame(uint identifier, bool useExtendedIdentifiers, byte[] data, int dlc, int timeoutMs)
    {
        EnsureNotDisposed();
        ArgumentNullException.ThrowIfNull(data);

        if (dlc < 0 || dlc > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(dlc), "DLC must be between 0 and 8 for classic CAN.");
        }

        if (data.Length < dlc)
        {
            throw new ArgumentException("The payload buffer is shorter than the selected DLC.", nameof(data));
        }

        lock (syncRoot)
        {
            if (channelId == 0)
            {
                throw new InvalidOperationException("CAN channel is not connected.");
            }

            PassThruMsg[] messages = [CreateTransmitMessage(currentCanProtocolId, identifier, useExtendedIdentifiers, data, dlc)];
            uint numberOfMessages = 1;
            EnsureSuccess(passThruWriteMsgs(channelId, messages, ref numberOfMessages, (uint)Math.Max(timeoutMs, 0)), "PassThruWriteMsgs");
        }
    }

    public void Dispose()
    {
        if (isDisposed)
        {
            return;
        }

        isDisposed = true;

        lock (syncRoot)
        {
            try
            {
                if (channelId != 0)
                {
                    passThruDisconnect(channelId);
                    channelId = 0;
                }
            }
            catch
            {
            }

            try
            {
                if (deviceId != 0)
                {
                    passThruClose(deviceId);
                    deviceId = 0;
                }
            }
            catch
            {
            }
        }

        NativeLibrary.Free(nativeLibraryHandle);
    }

    private void TryStartCatchAllFilter()
    {
        if (channelId == 0)
        {
            return;
        }

        PassThruMsg mask = CreateFilterMessage(0);
        PassThruMsg pattern = CreateFilterMessage(0);
        uint filterId = 0;

        _ = passThruStartMsgFilter(channelId, (uint)PassThruFilterType.PassFilter, ref mask, ref pattern, IntPtr.Zero, ref filterId);
    }

    private PassThruStatus GetLastError(out string? driverError)
    {
        StringBuilder builder = new(LastErrorBufferLength);
        PassThruStatus status = passThruGetLastError(builder);
        driverError = status == PassThruStatus.NoError ? builder.ToString() : null;
        return status;
    }

    private void EnsureSuccess(PassThruStatus status, string operation)
    {
        if (status == PassThruStatus.NoError)
        {
            return;
        }

        _ = GetLastError(out string? driverError);
        throw new PassThruException(operation, status, driverError);
    }

    private static PassThruMsg[] CreateMessageBuffer(int batchSize)
    {
        int actualSize = Math.Clamp(batchSize, 1, 64);
        PassThruMsg[] buffer = new PassThruMsg[actualSize];
        for (int index = 0; index < buffer.Length; index++)
        {
            buffer[index] = PassThruMsg.Create();
        }

        return buffer;
    }

    private void SetJ1962Pins(uint j1962Pins)
    {
        PassThruSConfig config = new()
        {
            Parameter = (uint)PassThruConfigId.J1962Pins,
            Value = j1962Pins
        };

        IntPtr configPtr = IntPtr.Zero;
        IntPtr configListPtr = IntPtr.Zero;

        try
        {
            configPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PassThruSConfig>());
            Marshal.StructureToPtr(config, configPtr, fDeleteOld: false);

            PassThruSConfigList configList = new()
            {
                NumOfParams = 1,
                ConfigPtr = configPtr
            };

            configListPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PassThruSConfigList>());
            Marshal.StructureToPtr(configList, configListPtr, fDeleteOld: false);

            EnsureSuccess(
                passThruIoctl(channelId, (uint)PassThruIoctlId.SetConfig, configListPtr, IntPtr.Zero),
                "PassThruIoctl(SET_CONFIG/J1962_PINS)");
        }
        finally
        {
            if (configListPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(configListPtr);
            }

            if (configPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(configPtr);
            }
        }
    }

    private PassThruMsg CreateFilterMessage(uint identifier)
    {
        PassThruMsg message = PassThruMsg.Create();
        message.ProtocolId = (uint)currentCanProtocolId;
        message.DataSize = 4;
        WriteIdentifier(identifier, message.Data);
        return message;
    }

    private static PassThruMsg CreateTransmitMessage(PassThruProtocolId protocolId, uint identifier, bool useExtendedIdentifiers, byte[] data, int dlc)
    {
        PassThruMsg message = PassThruMsg.Create();
        message.ProtocolId = (uint)protocolId;
        message.TxFlags = useExtendedIdentifiers ? PassThruFlags.Can29BitId : 0u;
        message.DataSize = (uint)(4 + dlc);
        WriteIdentifier(useExtendedIdentifiers ? (identifier & 0x1FFFFFFFu) : (identifier & 0x7FFu), message.Data);
        if (dlc > 0)
        {
            Array.Copy(data, 0, message.Data, 4, dlc);
        }

        return message;
    }

    private static void WriteIdentifier(uint identifier, byte[] buffer)
    {
        buffer[0] = (byte)((identifier >> 24) & 0xFF);
        buffer[1] = (byte)((identifier >> 16) & 0xFF);
        buffer[2] = (byte)((identifier >> 8) & 0xFF);
        buffer[3] = (byte)(identifier & 0xFF);
    }

    private static CanFrame? TryParse(PassThruMsg message)
    {
        if (message.ProtocolId != (uint)PassThruProtocolId.Can &&
            message.ProtocolId != (uint)PassThruProtocolId.CanPs)
        {
            return null;
        }

        if (message.Data is null || message.DataSize < 4 || message.Data.Length < 4)
        {
            return null;
        }

        uint rawIdentifier = ((uint)message.Data[0] << 24) |
                             ((uint)message.Data[1] << 16) |
                             ((uint)message.Data[2] << 8) |
                             message.Data[3];

        bool isExtended = (message.RxStatus & PassThruFlags.Can29BitId) != 0 ||
                          (message.TxFlags & PassThruFlags.Can29BitId) != 0;

        uint identifier = isExtended ? (rawIdentifier & 0x1FFFFFFFu) : (rawIdentifier & 0x7FFu);
        int dataLength = (int)Math.Min(Math.Max(message.DataSize - 4, 0), 64);
        byte[] data = new byte[dataLength];
        if (dataLength > 0)
        {
            Array.Copy(message.Data, 4, data, 0, dataLength);
        }

        return new CanFrame(identifier, isExtended, dataLength, data, message.Timestamp, DateTime.UtcNow);
    }

    private T GetDelegate<T>(string exportName) where T : Delegate
    {
        nint export = NativeLibrary.GetExport(nativeLibraryHandle, exportName);
        return Marshal.GetDelegateForFunctionPointer<T>(export);
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(isDisposed, this);
    }
}
