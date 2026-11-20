// PhoneBox Games Tab collector.
// Measures the FPS of the game you are playing from real-time ETW "Present" events (DXGI, D3D9 and DxgKrnl, which also
// covers Vulkan/OpenGL), works out which game it is (Steam, Epic, Xbox/Game Pass or a plain .exe), tracks play time and
// sends everything to the PhoneBox agent with POST /api/push/game, where the Games tab's HTML widget draws it.
// While a game runs it also keeps /api/push/playing on (ttl 15 s), so a PhoneBox rule can open the Games tab.
//
// No administrator rights needed: the Windows user only has to be in the "Performance Log Users" group.
//
// Usage: PhoneBoxGamesTab.exe [--port 8765] [--console] [--test] [--force <exe>]
//   --port     port of the PhoneBox agent's local API (default 8765; the installer passes the one in use)
//   --console  prints the state every second
//   --test     prints, for 5 seconds, the frames per second of every process that draws, then exits
//   --force    testing aid: treats the process with that exe name as the game even when it is not in front
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PhoneBoxGamesTab;

static class Program
{
    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneBoxGamesTab");
    static readonly string AppDir = AppContext.BaseDirectory;
    static StreamWriter? _log;
    static bool _console;

    static int Main(string[] args)
    {
        int port = 8765;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--port" && i + 1 < args.Length) port = int.Parse(args[++i]);
            else if (args[i] is "--console") _console = true;
            else if (args[i] is "--force" && i + 1 < args.Length) Tracker.Forced = args[++i].Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
        }
        Directory.CreateDirectory(DataDir);

        if (args.Contains("--test")) return Test();

        using var mutex = new Mutex(true, @"Local\PhoneBoxGamesTab", out bool first);
        if (!first) { Console.Error.WriteLine("A collector is already running for this Windows user."); return 2; }

        try { _log = new StreamWriter(new FileStream(Path.Combine(DataDir, "collector.log"), FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true }; } catch { }
        Log($"start: agent port {port}, user {Environment.UserName}");

        string? error = Etw.Start();
        if (error != null) Log("ETW: " + error);

        var tracker = new Tracker(port);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { tracker.Stop(); Etw.Stop(); };
        tracker.Run(error);
        return 0;
    }

    public static void Log(string s)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {s}";
        try { _log?.WriteLine(line); } catch { }
        if (_console) Console.WriteLine(line);
    }
    public static bool ConsoleOn => _console;

    static int Test()
    {
        var error = Etw.Start("PhoneBoxGamesTab-test-" + Environment.UserName);
        if (error != null) { Console.WriteLine("ETW failed: " + error); return 1; }
        for (int s = 0; s < 5; s++)
        {
            Thread.Sleep(1000);
            var st = Etw.Drain();
            Console.WriteLine($"--- {s + 1}s");
            foreach (var kv in st.OrderByDescending(k => k.Value.Fps).Take(8))
            {
                string name; try { name = Process.GetProcessById((int)kv.Key).ProcessName; } catch { name = "?"; }
                Console.WriteLine($"{kv.Value.Fps,6:0} fps  pid {kv.Key,-6} {name}  ({kv.Value.Source})");
            }
        }
        Etw.Stop();
        return 0;
    }

    public static string DataPath(string name) => Path.Combine(DataDir, name);
    public static string AppPath(string name) => Path.Combine(AppDir, name);
}

// ------------------------------------------------------------------------------------------------ real-time ETW
static unsafe class Etw
{
    static readonly Guid DXGI = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    static readonly Guid D3D9 = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    static readonly Guid DXGKRNL = new("802EC45A-1E99-4B83-9920-87C98277BA9D");

    static string _name = "";
    static ulong _session, _trace;
    static Thread? _thread;
    static IntPtr _namePtr;

    sealed class PidFrames
    {
        public List<long>[] Cur = [new(), new(), new()];
        public long[] Last = new long[3];
    }
    static readonly Dictionary<uint, PidFrames> _frames = [];
    static readonly object _lock = new();
    static long _lastDrain = Stopwatch.GetTimestamp();

    public record struct Stat(double Fps, string Source, List<double> FrameMs);

    public static string? Start(string? name = null)
    {
        _name = name ?? "PhoneBoxGamesTab-" + Environment.UserName;
        StopByName(_name); // a session left behind by a collector that crashed
        int size = sizeof(EVENT_TRACE_PROPERTIES) + 1024;
        var props = (EVENT_TRACE_PROPERTIES*)NativeMemory.AllocZeroed((nuint)size);
        try
        {
            props->Wnode.BufferSize = (uint)size;
            props->Wnode.Flags = 0x00020000; // WNODE_FLAG_TRACED_GUID
            props->Wnode.ClientContext = 1;    // QPC timestamps
            props->LogFileMode = 0x00000100;   // EVENT_TRACE_REAL_TIME_MODE
            props->BufferSize = 64;
            props->MinimumBuffers = 8;
            props->MaximumBuffers = 64;
            props->FlushTimer = 1;
            props->LoggerNameOffset = (uint)sizeof(EVENT_TRACE_PROPERTIES);
            uint r = StartTraceW(out _session, _name, props);
            if (r != 0) return $"StartTrace {r}" + (r == 5 ? " (access denied: add this Windows user to the 'Performance Log Users' group)" : "");
        }
        finally { NativeMemory.Free(props); }

        Enable(DXGI, ulong.MaxValue);
        Enable(D3D9, ulong.MaxValue);
        Enable(DXGKRNL, 0x8000000); // "Present" keyword

        _namePtr = Marshal.StringToHGlobalUni(_name);
        var lf = new EVENT_TRACE_LOGFILEW
        {
            LoggerName = _namePtr,
            ProcessTraceMode = 0x00000100 | 0x10000000 | 0x00001000, // REAL_TIME | EVENT_RECORD | RAW_TIMESTAMP
            EventRecordCallback = (IntPtr)(delegate* unmanaged<byte*, void>)&OnEvent,
        };
        _trace = OpenTraceW(&lf);
        if (_trace == ulong.MaxValue) return $"OpenTrace {Marshal.GetLastWin32Error()}";
        _thread = new Thread(() =>
        {
            ulong h = _trace;
            uint r = ProcessTrace(&h, 1, IntPtr.Zero, IntPtr.Zero);
            if (r != 0 && r != 1223) Program.Log($"ProcessTrace ended: {r}");
        }) { IsBackground = true, Name = "etw" };
        _thread.Start();
        return null;
    }

    static void Enable(Guid g, ulong keywords)
    {
        uint r = EnableTraceEx2(_session, &g, 1, 4, keywords, 0, 0, IntPtr.Zero);
        if (r != 0) Program.Log($"EnableTraceEx2 {g}: {r}");
    }

    public static void Stop()
    {
        if (_trace != 0 && _trace != ulong.MaxValue) { CloseTrace(_trace); _trace = 0; }
        if (_name != "") StopByName(_name);
    }

    static void StopByName(string name)
    {
        int size = sizeof(EVENT_TRACE_PROPERTIES) + 1024;
        var props = (EVENT_TRACE_PROPERTIES*)NativeMemory.AllocZeroed((nuint)size);
        props->Wnode.BufferSize = (uint)size;
        props->LoggerNameOffset = (uint)sizeof(EVENT_TRACE_PROPERTIES);
        ControlTraceW(0, name, props, 1); // EVENT_TRACE_CONTROL_STOP
        NativeMemory.Free(props);
    }

    [UnmanagedCallersOnly]
    static void OnEvent(byte* rec)
    {
        // EVENT_RECORD.EventHeader: ProcessId @12, TimeStamp @16, ProviderId @24, EventDescriptor.Id @40
        ushort id = *(ushort*)(rec + 40);
        int src;
        if (id == 42 && *(Guid*)(rec + 24) == DXGI) src = 0;          // DXGI Present_Start
        else if (id == 1 && *(Guid*)(rec + 24) == D3D9) src = 1;      // D3D9 Present_Start
        else if (id == 184 && *(Guid*)(rec + 24) == DXGKRNL) src = 2; // DxgKrnl Present (Vulkan / OpenGL go through here too)
        else return;
        uint pid = *(uint*)(rec + 12);
        long ts = *(long*)(rec + 16);
        lock (_lock)
        {
            if (!_frames.TryGetValue(pid, out var f)) _frames[pid] = f = new PidFrames();
            f.Cur[src].Add(ts);
        }
    }

    /// <summary>Frames per second of every process since the last call, with the frame times in ms.</summary>
    public static Dictionary<uint, Stat> Drain()
    {
        long now = Stopwatch.GetTimestamp();
        double secs = Math.Max(0.2, (now - _lastDrain) / (double)Stopwatch.Frequency);
        _lastDrain = now;
        var res = new Dictionary<uint, Stat>();
        double freq = Stopwatch.Frequency;
        lock (_lock)
        {
            var gone = new List<uint>();
            foreach (var (pid, f) in _frames)
            {
                int best = -1, n = 0;
                for (int s = 0; s < 3; s++) if (f.Cur[s].Count > n) { n = f.Cur[s].Count; best = s; }
                // DXGI/D3D9 mark the application's own frames; DxgKrnl only decides when no API shows up (Vulkan/OpenGL)
                if (best == 2) for (int s = 0; s < 2; s++) if (f.Cur[s].Count * 2 >= n) { best = s; n = f.Cur[s].Count; break; }
                if (best < 0) { gone.Add(pid); continue; }
                var list = f.Cur[best];
                var ms = new List<double>(list.Count);
                long prev = f.Last[best];
                foreach (var ts in list)
                {
                    if (prev != 0 && ts > prev) { double d = (ts - prev) * 1000.0 / freq; if (d < 2000) ms.Add(d); }
                    prev = ts;
                }
                // ETW delivers in batches (~1 s): FPS comes from the intervals between frames, not from how many arrived now
                double sum = ms.Sum();
                double fps = ms.Count >= 2 && sum > 0 ? ms.Count * 1000.0 / sum : n / secs;
                res[pid] = new Stat(fps, best switch { 0 => "DXGI", 1 => "D3D9", _ => "DxgKrnl" }, ms);
                for (int s = 0; s < 3; s++) { if (f.Cur[s].Count > 0) f.Last[s] = f.Cur[s][^1]; f.Cur[s].Clear(); }
            }
            foreach (var p in gone) _frames.Remove(p);
        }
        return res;
    }

    // ---- P/Invoke
    [StructLayout(LayoutKind.Sequential)]
    struct WNODE_HEADER { public uint BufferSize, ProviderId; public ulong HistoricalContext; public long TimeStamp; public Guid Guid; public uint ClientContext, Flags; }
    [StructLayout(LayoutKind.Sequential)]
    struct EVENT_TRACE_PROPERTIES
    {
        public WNODE_HEADER Wnode;
        public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
        public int AgeLimit;
        public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
        public IntPtr LoggerThreadId;
        public uint LogFileNameOffset, LoggerNameOffset;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct EVENT_TRACE_HEADER { public ushort Size, FieldTypeFlags; public uint Version, ThreadId, ProcessId; public long TimeStamp; public Guid Guid; public ulong ProcessorTime; }
    [StructLayout(LayoutKind.Sequential)]
    struct EVENT_TRACE { public EVENT_TRACE_HEADER Header; public uint InstanceId, ParentInstanceId; public Guid ParentGuid; public IntPtr MofData; public uint MofLength, ClientContext; }
    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEMTIME { public ushort a, b, c, d, e, f, g, h; }
    [StructLayout(LayoutKind.Sequential)]
    struct TIME_ZONE_INFORMATION { public int Bias; public fixed char StandardName[32]; public SYSTEMTIME StandardDate; public int StandardBias; public fixed char DaylightName[32]; public SYSTEMTIME DaylightDate; public int DaylightBias; }
    [StructLayout(LayoutKind.Sequential)]
    struct TRACE_LOGFILE_HEADER
    {
        public uint BufferSize, Version, ProviderVersion, NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution, MaximumFileSize, LogFileMode, BuffersWritten;
        public Guid LogInstanceGuid;
        public IntPtr LoggerName, LogFileName;
        public TIME_ZONE_INFORMATION TimeZone;
        public long BootTime, PerfFreq, StartTime;
        public uint ReservedFlags, BuffersLost;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct EVENT_TRACE_LOGFILEW
    {
        public IntPtr LogFileName, LoggerName;
        public long CurrentTime;
        public uint BuffersRead, ProcessTraceMode;
        public EVENT_TRACE CurrentEvent;
        public TRACE_LOGFILE_HEADER LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize, Filled, EventsLost;
        public IntPtr EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint StartTraceW(out ulong handle, string name, EVENT_TRACE_PROPERTIES* props);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint ControlTraceW(ulong handle, string name, EVENT_TRACE_PROPERTIES* props, uint code);
    [DllImport("advapi32.dll")] static extern uint EnableTraceEx2(ulong handle, Guid* provider, uint code, byte level, ulong any, ulong all, uint timeout, IntPtr parms);
    [DllImport("advapi32.dll", SetLastError = true)] static extern ulong OpenTraceW(EVENT_TRACE_LOGFILEW* logfile);
    [DllImport("advapi32.dll")] static extern uint ProcessTrace(ulong* handles, uint count, IntPtr start, IntPtr end);
    [DllImport("advapi32.dll")] static extern uint CloseTrace(ulong handle);

    public static void CheckLayout()
    {
        if (sizeof(EVENT_TRACE_PROPERTIES) != 120 || sizeof(EVENT_TRACE_LOGFILEW) != 448)
            throw new Exception($"unexpected ETW struct layout: {sizeof(EVENT_TRACE_PROPERTIES)} / {sizeof(EVENT_TRACE_LOGFILEW)}");
    }
}

// ------------------------------------------------------------------------------------------------ game and session
sealed class GameInfo
{
    public int Pid;
    public string Exe = "", Path = "", Name = "", Store = "PC";
    public string? AppId, Img, Icon;
    public long? SteamMin;
    public DateTime Start;
    public Process? Proc;
}

sealed record Session(string n, string exe, string? appid, string store, long start, long end, int sec, double avg, double low1, string? img);

sealed class Tracker(int port)
{
    readonly HttpClient _http = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(3) };
    readonly Dictionary<int, (string path, bool lib, string exe)> _procCache = [];
    readonly HashSet<string> _ignore = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _include = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _folders = []; // folders.txt: everything inside counts as a game (lower case, ending in \)
    List<Session> _hist = [];
    GameInfo? _game;
    // session statistics
    readonly List<int> _fpsHist = [];
    readonly Queue<(long t, List<double> ms)> _recent = new();
    readonly long[] _ftBuckets = new long[2001]; // 0.1 ms each, up to 200 ms
    long _frames; double _frameSecs; double _maxFps; double _minFps = double.MaxValue;
    TimeSpan _lastCpu; DateTime _lastCpuAt;
    double _cpuPct; double _ramGb;
    DateTime _lastIdlePush = DateTime.MinValue, _lastPlayingPush = DateTime.MinValue, _lastSave = DateTime.MinValue;
    int _idleCount;
    string? _etwError;
    public static string? Forced;
    double _holdFps; bool _held;

    static readonly string[] BuiltInIgnore =
    [
        "explorer", "dwm", "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc", "discord", "teams", "ms-teams",
        "olk", "outlook", "code", "devenv", "claude", "wallpaper32", "wallpaper64", "wallpaperservice32", "webwallpaper32", "ui32",
        "vlc", "mpc-hc64", "mpc-be64", "potplayermini64", "spotify", "deezer", "obs64", "steamwebhelper", "steam", "epicgameslauncher",
        "epicwebhelper", "battle.net", "eadesktop", "eabackgroundservice", "upc", "galaxyclient", "xboxpcapp", "gamingservices",
        "powerpnt", "mstsc", "msrdc", "applicationframehost", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
        "textinputhost", "lockapp", "logioptionsplus_agent", "nvidia app", "nvcontainer", "phoneboxagent", "phoneboxgamestab", "taskbarhero",
        "virtualdesktop.streamer", "virtualdesktop.service", "ovrserver_x64", "oculusclient", "vrserver", "vrcompositor", "vrmonitor",
        "perplexity", "whatsapp", "whatsapp.root", "telegram", "windowsterminal", "powershell", "cmd", "bluestacksservices", "video.ui", "photos",
    ];

    // usual game folders on any drive; Microsoft Store apps (WindowsApps) only count when the Store catalog says ProductKind = Game
    static readonly string[] GameFolders =
    [
        @"\steamapps\common\", @"\epic games\", @"\xboxgames\", @"\gog galaxy\games\", @"\gog games\", @"\ubisoft game launcher\games\",
        @"\ea games\", @"\riot games\", @"\battle.net\", @"\games\", @"\jogos\",
    ];

    public void Stop()
    {
        try { if (_game != null) EndSession(DateTime.Now); } catch { }
        try { _http.DeleteAsync("api/push/playing").Wait(1500); } catch { }
    }

    public void Run(string? etwError)
    {
        _etwError = etwError;
        Etw.CheckLayout();
        LoadLists();
        LoadHistory();
        var next = DateTime.Now;
        while (true)
        {
            next = next.AddSeconds(1);
            var wait = next - DateTime.Now;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait); else next = DateTime.Now;
            try { Tick(); }
            catch (Exception ex) { Program.Log("error: " + ex); }
        }
    }

    /// <summary>The list files sit next to the bin folder; the Portuguese names (incluir / ignorar / pastas) work too.</summary>
    static IEnumerable<string> ReadList(params string[] names)
    {
        foreach (var dir in new[] { Program.AppPath(".."), Program.AppPath(".") })
            foreach (var name in names)
            {
                var p = Path.GetFullPath(Path.Combine(dir, name));
                if (!File.Exists(p)) continue;
                foreach (var line in File.ReadAllLines(p))
                {
                    var t = line.Split('#')[0].Trim().Trim('"');
                    if (t.Length > 0) yield return t;
                }
            }
    }

    void LoadLists()
    {
        foreach (var s in BuiltInIgnore) _ignore.Add(s);
        static string Exe(string t) => t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t[..^4] : t;
        foreach (var t in ReadList("ignore.txt", "ignorar.txt")) _ignore.Add(Exe(t));
        foreach (var t in ReadList("include.txt", "incluir.txt")) _include.Add(Exe(t));
        foreach (var s in _include) _ignore.Remove(s);
        foreach (var t in ReadList("folders.txt", "pastas.txt"))
        {
            if (t.Length < 2) continue;
            var f = t.ToLowerInvariant().Replace('/', '\\');
            _folders.Add(f.EndsWith('\\') ? f : f + "\\");
        }
        Program.Log($"lists: {_ignore.Count} ignored, {_include.Count} included, extra game folders: {string.Join(", ", _folders)}");
    }

    // ---- history (per Windows user, in %LOCALAPPDATA%\PhoneBoxGamesTab\history.json)
    void LoadHistory()
    {
        try
        {
            var p = Program.DataPath("history.json");
            if (File.Exists(p)) _hist = JsonSerializer.Deserialize<List<Session>>(File.ReadAllText(p)) ?? [];
            // the session that was running when the collector stopped
            var cur = Program.DataPath("current-session.json");
            if (File.Exists(cur))
            {
                var s = JsonSerializer.Deserialize<Session>(File.ReadAllText(cur));
                if (s != null && s.sec >= 60) _hist.Add(s);
                File.Delete(cur);
            }
            // programs added to the ignore list later leave the history too
            int before = _hist.Count;
            _hist.RemoveAll(s => _ignore.Contains(s.exe));
            if (before != _hist.Count) Program.Log($"history: removed {before - _hist.Count} sessions of ignored programs");
            SaveHistory();
        }
        catch (Exception ex) { Program.Log("history: " + ex.Message); }
    }

    void SaveHistory()
    {
        if (_hist.Count > 1000) _hist = _hist.Skip(_hist.Count - 1000).ToList();
        File.WriteAllText(Program.DataPath("history.json"), JsonSerializer.Serialize(_hist));
    }

    // ---- main loop
    void Tick()
    {
        var stats = Etw.Drain();
        var now = DateTime.Now;
        int fg = ForegroundPid(out IntPtr hwnd);

        if (_game != null && (_game.Proc == null || _game.Proc.HasExited)) EndSession(now);

        // --force <exe>: locks on the busiest process with that name, in front or not
        if (Forced != null && _game == null)
        {
            foreach (var kv in stats.OrderByDescending(k => k.Value.Fps))
            {
                var (p, _, e) = Inspect((int)kv.Key);
                if (e.Equals(Forced, StringComparison.OrdinalIgnoreCase) && kv.Value.Fps >= 5) { StartSession((int)kv.Key, p, e, now); break; }
            }
        }

        // a new game in front?
        if (fg > 0 && fg != Environment.ProcessId && (_game == null || _game.Pid != fg) && stats.TryGetValue((uint)fg, out var fst) && fst.Fps >= 5)
        {
            var (path, lib, exe) = Inspect(fg);
            // Store package: the catalog decides (null = still asking; tried again next second).
            // With a game already open, only a library / include.txt game replaces it (any fullscreen window is not enough).
            bool? storeGame = null; bool packaged = false;
            if (!_include.Contains(exe) && PackageFamily(fg) is { } pfn) { packaged = true; storeGame = IsStoreGame(pfn); }
            bool candidate = packaged ? storeGame == true : (lib || _include.Contains(exe) || (_game == null && IsFullscreen(hwnd)));
            if (path != "" && !_ignore.Contains(exe) && candidate)
            {
                if (_game != null) EndSession(now);
                StartSession(fg, path, exe, now);
            }
        }

        if (_game != null)
        {
            stats.TryGetValue((uint)_game.Pid, out var st);
            double fps = st.FrameMs == null ? 0 : st.Fps;
            // an ETW batch that came late is not a drop to 0: repeat the last value once
            if (fps == 0 && _holdFps > 0 && !_held) { fps = _holdFps; _held = true; } else { _held = false; _holdFps = fps; }
            bool front = fg == _game.Pid;
            _fpsHist.Add((int)Math.Round(fps));
            if (_fpsHist.Count > 120) _fpsHist.RemoveAt(0);
            if (st.FrameMs != null && st.FrameMs.Count > 0)
            {
                _recent.Enqueue((now.Ticks, st.FrameMs));
                // session averages only count time in front with the game drawing
                if (front && fps >= 1)
                {
                    _frames += st.FrameMs.Count; _frameSecs += st.FrameMs.Sum() / 1000.0;
                    foreach (var m in st.FrameMs) _ftBuckets[Math.Min(2000, (int)(m * 10))]++;
                    if (fps > _maxFps) _maxFps = fps;
                    if (_frameSecs > 10 && fps < _minFps) _minFps = fps;
                }
            }
            while (_recent.Count > 0 && now.Ticks - _recent.Peek().t > TimeSpan.TicksPerSecond * 60) _recent.Dequeue();
            UpdateProcess();

            double ft = st.FrameMs is { Count: > 0 } ? st.FrameMs.Average() : 0;
            var recentMs = _recent.SelectMany(r => r.ms).ToList();
            double low1 = LowPct(recentMs, 0.01), low01 = LowPct(recentMs, 0.001);
            double avg = _frameSecs > 0 ? _frames / _frameSecs : 0;
            int sec = (int)(now - _game.Start).TotalSeconds;
            Push("game", new
            {
                on = true,
                t = Ms(now),
                g = new { n = _game.Name, exe = _game.Exe, store = _game.Store, appid = _game.AppId, img = _game.Img, icon = _game.Icon, start = Ms(_game.Start), steamMin = _game.SteamMin },
                front,
                fps = Math.Round(fps, 0),
                ft = Math.Round(ft, 1),
                low1 = Math.Round(low1, 0),
                low01 = Math.Round(low01, 0),
                avg = Math.Round(avg, 0),
                sLow1 = Math.Round(Low1Buckets(), 0),
                max = Math.Round(_maxFps, 0),
                min = _minFps == double.MaxValue ? 0 : Math.Round(_minFps, 0),
                api = st.Source,
                hist = _fpsHist,
                cpu = Math.Round(_cpuPct, 0),
                ram = Math.Round(_ramGb, 2),
                sec,
                today = TodaySec(now) + sec,
                week = WeekTop(now, _game.Name, sec),
                recent = Recent(),
            });
            if ((now - _lastPlayingPush).TotalSeconds >= 5) { Push("playing", true, 15); _lastPlayingPush = now; }
            if ((now - _lastSave).TotalSeconds >= 60) { SaveCurrent(now); _lastSave = now; }
            if (Program.ConsoleOn) Program.Log($"{_game.Name}: {fps:0} fps ({st.Source}) ft {ft:0.0} ms, 1% {low1:0}, avg {avg:0}, {(front ? "front" : "background")}, cpu {_cpuPct:0}%");
        }
        else if ((now - _lastIdlePush).TotalSeconds >= 30 || _idleCount == 0)
        {
            _lastIdlePush = now; _idleCount++;
            Push("game", new
            {
                on = false,
                t = Ms(now),
                err = _etwError,
                today = TodaySec(now),
                week = WeekTop(now, null, 0),
                recent = Recent(),
                last = _hist.Count > 0 ? _hist[^1] : null,
            });
        }
    }

    static long Ms(DateTime d) => new DateTimeOffset(d).ToUnixTimeMilliseconds();

    /// <summary>"1% low" = average FPS of the slowest 1% of frames.</summary>
    static double LowPct(List<double> ms, double pct)
    {
        if (ms.Count < 20) return 0;
        var sorted = ms.OrderByDescending(x => x).ToList();
        int n = Math.Max(1, (int)(sorted.Count * pct));
        double avgMs = sorted.Take(n).Average();
        return avgMs > 0 ? 1000 / avgMs : 0;
    }
    double Low1Buckets()
    {
        long total = _ftBuckets.Sum(); if (total < 100) return 0;
        long want = Math.Max(1, total / 100), got = 0; double sum = 0;
        for (int i = 2000; i >= 0 && got < want; i--)
        {
            long take = Math.Min(_ftBuckets[i], want - got);
            sum += take * (i + 0.5) / 10.0; got += take;
        }
        return sum > 0 ? 1000 / (sum / got) : 0;
    }

    int TodaySec(DateTime now)
    {
        var midnight = Ms(now.Date);
        return _hist.Where(s => s.end > midnight).Sum(s => (int)((s.end - Math.Max(s.start, midnight)) / 1000));
    }

    object[] WeekTop(DateTime now, string? current, int currentSec)
    {
        var since = Ms(now.Date.AddDays(-6));
        var d = _hist.Where(s => s.end > since).GroupBy(s => s.n).ToDictionary(g => g.Key, g => g.Sum(s => s.sec));
        if (current != null) d[current] = d.GetValueOrDefault(current) + currentSec;
        return d.OrderByDescending(kv => kv.Value).Take(5).Select(kv => (object)new { n = kv.Key, sec = kv.Value }).ToArray();
    }

    object[] Recent() => _hist.AsEnumerable().Reverse().Take(6).Select(s => (object)new { s.n, s.start, s.sec, avg = Math.Round(s.avg), low1 = Math.Round(s.low1), s.img }).ToArray();

    // ---- session
    void StartSession(int pid, string path, string exe, DateTime now)
    {
        var g = new GameInfo { Pid = pid, Path = path, Exe = exe, Start = now };
        try { g.Proc = Process.GetProcessById(pid); } catch { }
        Identify(g);
        _game = g;
        _fpsHist.Clear(); _recent.Clear(); Array.Clear(_ftBuckets);
        _frames = 0; _frameSecs = 0; _maxFps = 0; _minFps = double.MaxValue; _lastCpu = TimeSpan.Zero; _cpuPct = 0; _ramGb = 0;
        _lastPlayingPush = DateTime.MinValue; _lastSave = now;
        Program.Log($"game: {g.Name} ({g.Store}{(g.AppId != null ? " " + g.AppId : "")}) pid {pid} {path}");
    }

    Session Snapshot(DateTime end)
    {
        var g = _game!;
        return new Session(g.Name, g.Exe, g.AppId, g.Store, Ms(g.Start), Ms(end), (int)(end - g.Start).TotalSeconds,
            Math.Round(_frameSecs > 0 ? _frames / _frameSecs : 0, 1), Math.Round(Low1Buckets(), 1), g.Img);
    }

    void SaveCurrent(DateTime now)
    {
        try { File.WriteAllText(Program.DataPath("current-session.json"), JsonSerializer.Serialize(Snapshot(now))); } catch { }
    }

    void EndSession(DateTime now)
    {
        if (_game == null) return;
        var s = Snapshot(now);
        Program.Log($"end: {s.n}, {s.sec / 60} min, avg {s.avg:0} fps, 1% low {s.low1:0}");
        if (s.sec >= 60) { _hist.Add(s); SaveHistory(); }
        try { File.Delete(Program.DataPath("current-session.json")); } catch { }
        _game = null; _idleCount = 0;
        try { _http.DeleteAsync("api/push/playing").Wait(1500); } catch { }
    }

    void UpdateProcess()
    {
        var p = _game?.Proc; if (p == null) return;
        try
        {
            p.Refresh();
            var cpu = p.TotalProcessorTime; var at = DateTime.UtcNow;
            if (_lastCpu != TimeSpan.Zero)
                _cpuPct = (cpu - _lastCpu).TotalMilliseconds / Math.Max(1, (at - _lastCpuAt).TotalMilliseconds) / Environment.ProcessorCount * 100;
            _lastCpu = cpu; _lastCpuAt = at;
            _ramGb = p.WorkingSet64 / 1073741824.0;
        }
        catch { }
    }

    void Push(string key, object body, int ttl = 0)
    {
        try
        {
            var c = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8);
            c.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            var r = _http.PostAsync("api/push/" + key + (ttl > 0 ? "?ttl=" + ttl : ""), c).Result;
            if (!r.IsSuccessStatusCode) Program.Log($"push {key}: {(int)r.StatusCode} {r.Content.ReadAsStringAsync().Result}");
        }
        catch (Exception ex) { if (Program.ConsoleOn) Program.Log("push: " + ex.Message); }
    }

    // ---- process / window
    (string path, bool lib, string exe) Inspect(int pid)
    {
        if (_procCache.TryGetValue(pid, out var c)) return c;
        string path = ImagePath(pid);
        string low = path.ToLowerInvariant();
        bool lib = path != "" && (GameFolders.Any(low.Contains) || _folders.Any(f => low.StartsWith(f, StringComparison.Ordinal)));
        var res = (path, lib, Path.GetFileNameWithoutExtension(path));
        if (_procCache.Count > 500) _procCache.Clear();
        _procCache[pid] = res;
        return res;
    }

    static string ImagePath(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (h == IntPtr.Zero) return "";
        try
        {
            var sb = new StringBuilder(1024); int n = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref n) ? sb.ToString() : "";
        }
        finally { CloseHandle(h); }
    }

    static int ForegroundPid(out IntPtr hwnd)
    {
        hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out int pid);
        return pid;
    }

    static bool IsFullscreen(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out RECT r)) return false;
        var mon = MonitorFromWindow(hwnd, 2);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(mon, ref mi)) return false;
        return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
    }

    // ---- identifying the game
    static readonly Dictionary<string, (string appid, string name)?> _steamCache = new(StringComparer.OrdinalIgnoreCase);

    void Identify(GameInfo g)
    {
        string low = g.Path.ToLowerInvariant();
        int i = low.IndexOf(@"\steamapps\common\", StringComparison.Ordinal);
        if (i >= 0)
        {
            string lib = g.Path[..i];
            string dir = g.Path[(i + @"\steamapps\common\".Length)..].Split('\\')[0];
            var s = SteamApp(lib, dir);
            if (s != null)
            {
                g.Store = "Steam"; g.AppId = s.Value.appid; g.Name = s.Value.name;
                g.Img = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{g.AppId}/header.jpg";
                g.SteamMin = SteamPlaytime(g.AppId);
            }
            else g.Name = dir;
        }
        if (g.Name == "" && low.Contains(@"\epic games\")) { g.Store = "Epic"; g.Name = EpicName(g.Path) ?? ""; }
        if (g.Name == "" && low.Contains(@"\xboxgames\"))
        {
            g.Store = "Xbox";
            var m = Regex.Match(g.Path, @"\\XboxGames\\([^\\]+)", RegexOptions.IgnoreCase);
            if (m.Success) g.Name = m.Groups[1].Value;
        }
        if (g.Name == "")
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(g.Path);
                g.Name = new[] { vi.ProductName, vi.FileDescription }.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "";
            }
            catch { }
            if (g.Name == "") g.Name = g.Exe;
        }
        if (g.Store == "PC" && low.Contains(@"\gog")) g.Store = "GOG";
        g.Icon = IconPng(g.Path);
        // Xbox / Game Pass games (MSIX packages): official name and art from the public Microsoft Store catalog
        if (g.Store != "Steam" && PackageFamily(g.Pid) is { } pfn)
        {
            g.Store = "Xbox";
            _ = Task.Run(() => StoreArt(g, pfn));
        }
    }

    static readonly Dictionary<string, (string? name, string? img, string? kind)> _storeCache = [];
    static readonly HashSet<string> _storeBusy = [];
    static readonly HttpClient _web = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>true/false from the Store catalog; null while the (background) lookup has not finished.</summary>
    static bool? IsStoreGame(string pfn)
    {
        lock (_storeCache)
        {
            if (_storeCache.TryGetValue(pfn, out var c)) return c.kind == "Game";
            if (!_storeBusy.Add(pfn)) return null;
        }
        _ = Task.Run(() => { try { Lookup(pfn); } finally { lock (_storeCache) _storeBusy.Remove(pfn); } });
        return null;
    }

    static void StoreArt(GameInfo g, string pfn)
    {
        var res = Lookup(pfn);
        if (res.img != null) g.Img = res.img;
        if (!string.IsNullOrWhiteSpace(res.name)) g.Name = res.name!;
    }

    /// <summary>The Windows display locale ("pt-BR", "en-US"...), for the Store's market and language.</summary>
    static (string market, string lang) Locale()
    {
        var loc = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Control Panel\International", "LocaleName", null) as string ?? "en-US";
        var parts = loc.Split('-');
        return (parts.Length > 1 ? parts[^1] : "US", loc.ToLowerInvariant());
    }

    static (string? name, string? img, string? kind) Lookup(string pfn)
    {
        (string? name, string? img, string? kind) res;
        lock (_storeCache) if (_storeCache.TryGetValue(pfn, out res)) return res;
        res = (null, null, null);
        try
        {
            // only the package family name is sent; nothing about the user
            var (market, lang) = Locale();
            var url = "https://displaycatalog.mp.microsoft.com/v7.0/products/lookup?alternateId=PackageFamilyName&value="
                + Uri.EscapeDataString(pfn) + $"&market={market}&languages={lang},en-us&fieldsTemplate=details";
            using var doc = JsonDocument.Parse(_web.GetStringAsync(url).Result);
            var prods = doc.RootElement.GetProperty("Products");
            if (prods.GetArrayLength() > 0)
            {
                res.kind = prods[0].TryGetProperty("ProductKind", out var pk) ? pk.GetString() : null;
                var lp = prods[0].GetProperty("LocalizedProperties")[0];
                res.name = lp.GetProperty("ProductTitle").GetString();
                var imgs = lp.GetProperty("Images").EnumerateArray().ToList();
                foreach (var purpose in new[] { "TitledHeroArt", "SuperHeroArt", "BoxArt", "Poster" })
                {
                    var im = imgs.FirstOrDefault(i => i.GetProperty("ImagePurpose").GetString() == purpose);
                    if (im.ValueKind != JsonValueKind.Object) continue;
                    var u = im.GetProperty("Uri").GetString() ?? "";
                    res.img = (u.StartsWith("//") ? "https:" + u : u) + "?w=480";
                    break;
                }
            }
            else res.kind = "Unknown"; // not in the catalog (system app, sideloaded): not treated as a game
            Program.Log($"Store: {pfn} -> {res.name} ({res.kind}) {(res.img != null ? "with art" : "no art")}");
        }
        catch (Exception ex) { Program.Log($"Store {pfn}: {ex.Message}"); return res; } // not cached: tried again later
        lock (_storeCache) _storeCache[pfn] = res;
        return res;
    }

    static string? PackageFamily(int pid)
    {
        IntPtr h = OpenProcess(0x1000, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            int len = 0;
            if (GetPackageFamilyName(h, ref len, null) != 122 || len <= 0) return null; // 122 = buffer too small, so it is a package
            var sb = new StringBuilder(len);
            return GetPackageFamilyName(h, ref len, sb) == 0 ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    static (string appid, string name)? SteamApp(string lib, string installdir)
    {
        string key = lib + "|" + installdir;
        if (_steamCache.TryGetValue(key, out var c)) return c;
        (string, string)? res = null;
        try
        {
            foreach (var f in Directory.GetFiles(Path.Combine(lib, "steamapps"), "appmanifest_*.acf"))
            {
                var txt = File.ReadAllText(f);
                var dir = Regex.Match(txt, "\"installdir\"\\s+\"([^\"]*)\"").Groups[1].Value;
                if (!dir.Equals(installdir, StringComparison.OrdinalIgnoreCase)) continue;
                res = (Regex.Match(txt, "\"appid\"\\s+\"(\\d+)\"").Groups[1].Value, Regex.Match(txt, "\"name\"\\s+\"([^\"]*)\"").Groups[1].Value);
                break;
            }
        }
        catch { }
        _steamCache[key] = res;
        return res;
    }

    /// <summary>Total minutes Steam has recorded for the game (localconfig.vdf of the signed-in Steam account).</summary>
    static long? SteamPlaytime(string? appid)
    {
        if (appid == null) return null;
        try
        {
            string steam = (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string)?.Replace('/', '\\') ?? @"C:\Program Files (x86)\Steam";
            var user = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam\ActiveProcess", "ActiveUser", 0) is int u && u != 0 ? u.ToString() : null;
            var files = user != null ? [Path.Combine(steam, "userdata", user, "config", "localconfig.vdf")]
                : Directory.GetFiles(Path.Combine(steam, "userdata"), "localconfig.vdf", SearchOption.AllDirectories).OrderByDescending(f => new FileInfo(f).Length).ToArray();
            foreach (var f in files)
            {
                if (!File.Exists(f)) continue;
                var txt = File.ReadAllText(f);
                foreach (Match m in Regex.Matches(txt, "\"" + appid + "\"\\s*\\{((?:[^{}]|\\{[^{}]*\\})*)\\}"))
                {
                    var p = Regex.Match(m.Groups[1].Value, "\"Playtime\"\\s+\"(\\d+)\"");
                    if (p.Success) return long.Parse(p.Groups[1].Value);
                }
            }
        }
        catch { }
        return null;
    }

    static string? EpicName(string path)
    {
        try
        {
            foreach (var f in Directory.GetFiles(@"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests", "*.item"))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var loc = doc.RootElement.GetProperty("InstallLocation").GetString();
                if (!string.IsNullOrEmpty(loc) && path.StartsWith(loc.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    return doc.RootElement.GetProperty("DisplayName").GetString();
            }
        }
        catch { }
        return null;
    }

    static string? IconPng(string path)
    {
        try
        {
            var icons = new IntPtr[1]; var ids = new int[1];
            Bitmap? bmp = null;
            if (PrivateExtractIconsW(path, 0, 128, 128, icons, ids, 1, 0) > 0 && icons[0] != IntPtr.Zero)
            {
                using var ico = Icon.FromHandle(icons[0]);
                bmp = ico.ToBitmap();
                DestroyIcon(icons[0]);
            }
            else
            {
                using var ico = Icon.ExtractAssociatedIcon(path);
                if (ico != null) bmp = ico.ToBitmap();
            }
            if (bmp == null) return null;
            using (bmp)
            using (var small = new Bitmap(64, 64))
            {
                using (var gr = Graphics.FromImage(small)) { gr.InterpolationMode = InterpolationMode.HighQualityBicubic; gr.DrawImage(bmp, 0, 0, 64, 64); }
                using var ms = new MemoryStream();
                small.Save(ms, ImageFormat.Png);
                return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
            }
        }
        catch { return null; }
    }

    // ---- Win32
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfoW(IntPtr mon, ref MONITORINFO mi);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint PrivateExtractIconsW(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint n, uint flags);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern int GetPackageFamilyName(IntPtr h, ref int len, StringBuilder? name);
}
