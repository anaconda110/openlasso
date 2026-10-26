using System.Runtime.InteropServices;

namespace OpenLasso;

/// <summary>通过 Windows GPU Engine 性能计数器读取整机 GPU 引擎占用（0..100）。</summary>
public sealed class GpuMonitor : IDisposable
{
    const string CounterPath = @"\GPU Engine(*)\Utilization Percentage";
    const uint PDH_FMT_DOUBLE = 0x00000200;
    const uint PDH_MORE_DATA = 0x800007D2;
    const uint PDH_NO_DATA = 0x800007D5;
    const uint PDH_CSTATUS_NO_INSTANCE = 0x800007D1;
    const uint PDH_CSTATUS_VALID_DATA = 0x00000000;
    const uint PDH_CSTATUS_NEW_DATA = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE
    {
        public uint CStatus;
        public double DoubleValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PDH_FMT_COUNTERVALUE_ITEM
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string Name;
        public PDH_FMT_COUNTERVALUE Value;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhOpenQuery(string dataSource, UIntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhAddEnglishCounter(IntPtr query, string fullCounterPath, UIntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize,
        out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    static extern uint PdhCloseQuery(IntPtr query);

    enum ReadResult { Success, NoInstance, Failed }

    readonly object _sync = new();
    IntPtr _query;
    IntPtr _counter;
    bool _disposed;
    bool _available;
    int _failureCount;
    DateTime _nextRetryUtc = DateTime.MinValue;
    string _engineName = "";
    string _statusText = "GPU 监控初始化中";
    string _lastError = "";

    public bool Available { get { lock (_sync) return _available; } }
    public string EngineName { get { lock (_sync) return _engineName; } }
    public string StatusText { get { lock (_sync) return _statusText; } }
    public string LastError { get { lock (_sync) return _lastError; } }

    public GpuMonitor()
    {
        lock (_sync) OpenQueryCore();
    }

    public bool TrySample(out double load)
    {
        load = 0;
        lock (_sync)
        {
            if (_disposed)
            {
                SetStatus("GPU 监控已停止");
                return false;
            }

            DateTime now = DateTime.UtcNow;
            if ((_query == IntPtr.Zero || _counter == IntPtr.Zero) && now < _nextRetryUtc)
            {
                int seconds = Math.Max(1, (int)Math.Ceiling((_nextRetryUtc - now).TotalSeconds));
                SetStatus($"GPU 监控重试等待（{seconds} 秒）");
                return false;
            }

            if ((_query == IntPtr.Zero || _counter == IntPtr.Zero) && !OpenQueryCore())
                return false;

            try
            {
                uint collectStatus = PdhCollectQueryData(_query);
                if (collectStatus == PDH_NO_DATA)
                {
                    MarkNoInstance("GPU 暂无活动引擎实例");
                    return false;
                }
                if (collectStatus != 0)
                {
                    MarkFailure("GPU 采样失败", collectStatus);
                    return false;
                }

                ReadResult result = TryRead(out load, out string engineType, out uint readStatus);
                if (result == ReadResult.NoInstance)
                {
                    MarkNoInstance("GPU 暂无活动引擎实例");
                    return false;
                }
                if (result == ReadResult.Failed)
                {
                    MarkFailure("GPU 计数器读取失败", readStatus);
                    return false;
                }

                _failureCount = 0;
                _nextRetryUtc = DateTime.MinValue;
                _lastError = "";
                _available = true;
                _engineName = FriendlyEngineName(engineType);
                SetStatus($"GPU 监控正常（{_engineName}最繁忙）");
                return true;
            }
            catch (Exception ex)
            {
                MarkFailure("GPU 监控异常", 0, ex.Message);
                return false;
            }
        }
    }

    ReadResult TryRead(out double load, out string engineType, out uint failureStatus)
    {
        load = 0;
        engineType = "";
        failureStatus = 0;
        uint size = 0;
        uint count = 0;
        uint status = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out count, IntPtr.Zero);

        if (status == PDH_CSTATUS_NO_INSTANCE || status == PDH_NO_DATA) return ReadResult.NoInstance;
        if (status != 0 && status != PDH_MORE_DATA)
        {
            failureStatus = status;
            return ReadResult.Failed;
        }
        if (count == 0 || size == 0) return ReadResult.NoInstance;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArray(_counter, PDH_FMT_DOUBLE, ref size, out count, buffer);
            if (status == PDH_CSTATUS_NO_INSTANCE || status == PDH_NO_DATA) return ReadResult.NoInstance;
            if (status != 0)
            {
                failureStatus = status;
                return ReadResult.Failed;
            }

            int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            var engines = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var engineTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(
                    IntPtr.Add(buffer, i * itemSize));
                if (item.Value.CStatus != PDH_CSTATUS_VALID_DATA && item.Value.CStatus != PDH_CSTATUS_NEW_DATA)
                    continue;

                string key = EngineKey(item.Name ?? "");
                double value = Math.Clamp(item.Value.DoubleValue, 0d, 100d);
                engines[key] = engines.TryGetValue(key, out var sum) ? sum + value : value;
                engineTypes[key] = EngineType(item.Name ?? "");
            }

            if (engines.Count == 0) return ReadResult.NoInstance;

            // 同一引擎上的多进程占用相加；整机取最繁忙的“适配器 + 引擎类型”作为 GPU 占用。
            var busiest = engines.MaxBy(x => x.Value);
            load = Math.Clamp(busiest.Value, 0d, 100d);
            engineType = engineTypes.TryGetValue(busiest.Key, out string type) ? type : "";
            return ReadResult.Success;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static string EngineKey(string instance)
    {
        const string marker = "_engtype_";
        int typeIndex = instance.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (typeIndex < 0) return instance;

        int luidIndex = instance.IndexOf("_luid_", StringComparison.OrdinalIgnoreCase);
        if (luidIndex >= 0)
            return instance[luidIndex..typeIndex] + instance[typeIndex..];

        int pidIndex = instance.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
        int scopeStart = pidIndex >= 0
            ? instance.IndexOf('_', pidIndex + 4) is int end && end >= 0 ? end + 1 : 0
            : 0;
        return instance[scopeStart..typeIndex] + instance[typeIndex..];
    }

    static string EngineType(string instance)
    {
        const string marker = "_engtype_";
        int index = instance.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return "GPU";
        string type = instance[(index + marker.Length)..].Trim();
        int end = type.IndexOf('_');
        return end >= 0 ? type[..end] : type;
    }

    internal static string FriendlyEngineName(string type) => type.ToLowerInvariant() switch
    {
        "3d" => "3D 引擎",
        "copy" => "复制引擎",
        "videodecode" => "视频解码引擎",
        "videoencode" => "视频编码引擎",
        "compute" => "计算引擎",
        "graphics" => "图形引擎",
        _ => string.IsNullOrWhiteSpace(type) ? "GPU 引擎" : type + " 引擎",
    };

    bool OpenQueryCore()
    {
        CloseQueryCore();
        if (_disposed)
        {
            SetStatus("GPU 监控已停止");
            return false;
        }

        try
        {
            uint status = PdhOpenQuery(null, UIntPtr.Zero, out _query);
            if (status != 0)
            {
                MarkFailure("GPU 计数器初始化失败", status);
                return false;
            }

            status = PdhAddEnglishCounter(_query, CounterPath, UIntPtr.Zero, out _counter);
            if (status != 0)
            {
                MarkFailure("GPU 计数器路径不可用", status);
                return false;
            }

            // GPU Engine 是速率计数器；先采集一次，后续采样才有可计算的时间窗口。
            status = PdhCollectQueryData(_query);
            if (status != 0 && status != PDH_NO_DATA)
            {
                MarkFailure("GPU 计数器预热失败", status);
                return false;
            }

            _failureCount = 0;
            _nextRetryUtc = DateTime.MinValue;
            _lastError = "";
            SetStatus("GPU 监控等待首个采样");
            return true;
        }
        catch (Exception ex)
        {
            MarkFailure("GPU 计数器初始化异常", 0, ex.Message);
            return false;
        }
    }

    void MarkNoInstance(string status)
    {
        _available = false;
        _engineName = "";
        _failureCount = 0;
        _lastError = "";
        _nextRetryUtc = DateTime.MinValue;
        SetStatus(status);
    }

    void MarkFailure(string kind, uint status, string detail = "")
    {
        _available = false;
        _failureCount++;
        int delaySeconds = Math.Min(60, 1 << Math.Min(_failureCount - 1, 5));
        _nextRetryUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
        _lastError = detail.Length > 0
            ? $"{kind}：{detail}"
            : status != 0 ? $"{kind}（0x{status:X8}）" : kind;
        CloseQueryCore();
        SetStatus($"GPU 监控不可用：{_lastError}；{delaySeconds} 秒后重试");
    }

    void SetStatus(string text) => _statusText = string.IsNullOrWhiteSpace(text) ? "GPU 监控不可用" : text;

    void CloseQueryCore()
    {
        if (_query != IntPtr.Zero)
        {
            try { PdhCloseQuery(_query); } catch { }
        }
        _query = IntPtr.Zero;
        _counter = IntPtr.Zero;
        _available = false;
        _engineName = "";
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            CloseQueryCore();
            _lastError = "";
            SetStatus("GPU 监控已停止");
        }
    }
}
