using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;
using WF = System.Windows.Forms;

class Prof
{
    public string Name, Uuid;
    public DateTime? Saved;
}

static class Core
{
    public static readonly string AppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
    public static readonly string Sessions = Path.Combine(AppDir, "claude-code-sessions");
    public static readonly string Root = AppDomain.CurrentDomain.BaseDirectory;
    public static readonly string Profiles = Path.Combine(Root, "profiles");
    static readonly string CurFile = Path.Combine(Root, "current.txt");
    static readonly string[] AuthFiles = { @"Network\Cookies", @"Network\Cookies-journal" };
    static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 1000 };
    static readonly object SyncLock = new object();
    public static DateTime? LastSync;
    const string Aumid = @"shell:AppsFolder\Claude_pzs8sxrjxfjjc!Claude";

    static string ConfigPath { get { return Path.Combine(AppDir, "config.json"); } }
    static Dictionary<string, object> ReadJson(string p) { return (Dictionary<string, object>)Js.DeserializeObject(File.ReadAllText(p, Utf8)); }
    static void WriteJson(string p, object o) { File.WriteAllText(p, Js.Serialize(o), Utf8); }
    static bool IsAuthKey(string k) { return k.StartsWith("oauth:") || k == "lastKnownAccountUuid"; }

    public static string GetCurrent() { return File.Exists(CurFile) ? File.ReadAllText(CurFile, Utf8).Trim() : null; }
    static void SetCurrent(string n) { File.WriteAllText(CurFile, n, Utf8); }
    static string Dir(string n) { return Path.Combine(Profiles, n); }

    static bool IsAppClaude(Process p)
    {
        try { return p.MainModule.FileName.IndexOf(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase) >= 0; }
        catch { return true; }
    }

    public static void StopClaude()
    {
        foreach (var p in Process.GetProcessesByName("claude")) if (IsAppClaude(p)) try { p.Kill(); } catch { }
        for (int i = 0; i < 60 && Process.GetProcessesByName("claude").Any(IsAppClaude); i++) Thread.Sleep(250);
        Thread.Sleep(800);
    }

    public static void StartClaude() { Process.Start("explorer.exe", Aumid); }

    public static List<Prof> List()
    {
        Directory.CreateDirectory(Profiles);
        var r = new List<Prof>();
        foreach (var d in Directory.GetDirectories(Profiles).OrderBy(x => x))
        {
            var p = new Prof { Name = Path.GetFileName(d) };
            var a = Path.Combine(d, "auth.json");
            if (File.Exists(a) && HasSession(Path.Combine(d, AuthFiles[0])))
            {
                p.Saved = File.GetLastWriteTime(a);
                object u;
                if (ReadJson(a).TryGetValue("lastKnownAccountUuid", out u)) p.Uuid = u as string;
            }
            r.Add(p);
        }
        return r;
    }

    public class Usage { public int Fh, Sd; public DateTime T; public DateTime? FhReset, SdReset; public bool Exact; }

    static string LiveDir { get { return Path.Combine(Root, "usage"); } }
    static DateTime FromEpoch(object s) { return DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(s)).LocalDateTime; }

    // 状态栏模式：Claude Code 通过 stdin 传入官方 rate_limits 字段，按当前登录账号记下来，并输出状态栏文字
    public static void StatusLine()
    {
        var input = (Dictionary<string, object>)Js.DeserializeObject(new StreamReader(Console.OpenStandardInput(), Utf8).ReadToEnd());
        object rl, u;
        if (!input.TryGetValue("rate_limits", out rl) || !ReadJson(ConfigPath).TryGetValue("lastKnownAccountUuid", out u)) { Out("额度：等待首条回复"); return; }
        var r = (Dictionary<string, object>)rl;
        var rec = new Dictionary<string, object> { { "t", DateTimeOffset.Now.ToUnixTimeSeconds() } };
        var text = new List<string>();
        foreach (var w in new[] { new[] { "five_hour", "fh", "5h" }, new[] { "seven_day", "sd", "周" } })
        {
            object o;
            if (!r.TryGetValue(w[0], out o)) continue;
            var d = (Dictionary<string, object>)o;
            var pct = (int)Math.Round(Convert.ToDouble(d["used_percentage"]));
            rec[w[1]] = pct;
            rec[w[1] + "r"] = d["resets_at"];
            text.Add(w[2] + " " + pct + "%（" + FromEpoch(d["resets_at"]).ToString("ddd HH:mm") + " 重置）");
        }
        Directory.CreateDirectory(LiveDir);
        WriteJson(Path.Combine(LiveDir, (string)u + ".json"), rec);
        Out(string.Join("  ·  ", text));
    }

    static void Out(string s) { using (var w = new StreamWriter(Console.OpenStandardOutput(), Utf8)) w.Write(s); }

    static Usage GetLive(string uuid)
    {
        var f = Path.Combine(LiveDir, uuid + ".json");
        if (!File.Exists(f)) return null;
        var j = ReadJson(f);
        var r = new Usage { T = FromEpoch(j["t"]) };
        if (j.ContainsKey("fh")) { r.Fh = Convert.ToInt32(j["fh"]); r.FhReset = FromEpoch(j["fhr"]); }
        if (j.ContainsKey("sd")) { r.Sd = Convert.ToInt32(j["sd"]); r.SdReset = FromEpoch(j["sdr"]); }
        r.Exact = true;
        return r;
    }

    // 优先用状态栏记下的精确额度；更旧时用桌面端本地用量记录估算
    public static Usage GetUsage(string uuid)
    {
        if (uuid == null) return null;
        var live = GetLive(uuid);
        var est = GetLocalUsage(uuid);
        return live != null && (est == null || live.T >= est.T.AddMinutes(-30)) ? live : est;
    }

    // 桌面端在 plan-usage-history.json 里按组织记录用量百分比（fh=5小时，sd=7天）
    static Usage GetLocalUsage(string uuid)
    {
        string org = null;
        try
        {
            var p = (Dictionary<string, object>)ReadJson(Path.Combine(AppDir, "claude_desktop_config.json"))["preferences"];
            var s = (Dictionary<string, object>)((Dictionary<string, object>)p["epitaxyPrefs"])["dframe-code-sections"];
            foreach (var k in s.Keys) if (k.StartsWith(uuid + "/")) org = k.Substring(uuid.Length + 1);
        }
        catch (KeyNotFoundException) { }
        var f = Path.Combine(AppDir, "plan-usage-history.json");
        if (org == null || !File.Exists(f)) return null;
        var list = new List<Usage>();
        foreach (var o in (System.Collections.IEnumerable)ReadJson(f)["samples"])
        {
            var smp = (Dictionary<string, object>)o;
            if ((smp["org"] as string) != org) continue;
            var u = (Dictionary<string, object>)smp["u"];
            list.Add(new Usage { Fh = Convert.ToInt32(u["fh"]), Sd = Convert.ToInt32(u["sd"]), T = DateTimeOffset.FromUnixTimeMilliseconds(Convert.ToInt64(smp["t"])).LocalDateTime });
        }
        if (list.Count == 0) return null;
        list.Sort((a, b) => a.T.CompareTo(b.T));
        // 5 小时窗口：从窗口内第一次有用量算起；每周：按最近一次用量回落的时间每 7 天循环
        DateTime? start = null, weekDrop = null;
        Usage prev = null;
        foreach (var s in list)
        {
            if (prev != null && (s.Fh < prev.Fh || (s.T - prev.T).TotalHours >= 5)) start = null;
            if (s.Fh == 0) start = null; else if (start == null) start = s.T;
            if (prev != null && s.Sd < prev.Sd) weekDrop = s.T;
            prev = s;
        }
        var last = list[list.Count - 1];
        if (start != null) last.FhReset = start.Value.AddHours(5);
        if (weekDrop != null)
        {
            var r = weekDrop.Value;
            while (r <= last.T) r = r.AddDays(7);
            last.SdReset = r;
        }
        return last;
    }

    public static void EnsureCurrentDir()
    {
        // 以 Claude 实际登录的账号为准校正「当前」
        object u;
        if (ReadJson(ConfigPath).TryGetValue("lastKnownAccountUuid", out u))
        {
            var m = List().FirstOrDefault(p => p.Uuid == (u as string));
            if (m != null) SetCurrent(m.Name);
        }
        var c = GetCurrent();
        if (!string.IsNullOrEmpty(c)) Directory.CreateDirectory(Dir(c));
    }

    public static Action<string> Status = delegate { };

    // Chromium 最多 30 秒才把 Cookie 写盘；登录 Cookie 名在 SQLite 文件里是明文
    static bool HasSession(string cookies)
    {
        if (!File.Exists(cookies)) return false;
        var b = File.ReadAllBytes(cookies);
        var k = Encoding.ASCII.GetBytes("sessionKey");
        for (int i = 0; i <= b.Length - k.Length; i++)
        {
            int j = 0;
            while (j < k.Length && b[i + j] == k[j]) j++;
            if (j == k.Length) return true;
        }
        return false;
    }

    // 当前账号还没有可用快照（刚登录）时，先等 Claude 把登录信息写盘再关闭它
    static void WaitFlushIfNew(string cur)
    {
        if (string.IsNullOrEmpty(cur) || HasSession(Path.Combine(Dir(cur), AuthFiles[0]))) return;
        for (int i = 35; i > 0; i--) { Status("等待 Claude 写入「" + cur + "」的登录信息… " + i + " 秒"); Thread.Sleep(1000); }
    }

    static bool SaveProfile(string n)
    {
        if (!HasSession(Path.Combine(AppDir, AuthFiles[0]))) return false;
        var d = Dir(n);
        Directory.CreateDirectory(Path.Combine(d, "Network"));
        var auth = new Dictionary<string, object>();
        foreach (var kv in ReadJson(ConfigPath)) if (IsAuthKey(kv.Key)) auth[kv.Key] = kv.Value;
        WriteJson(Path.Combine(d, "auth.json"), auth);
        foreach (var f in AuthFiles)
        {
            var s = Path.Combine(AppDir, f);
            if (File.Exists(s)) File.Copy(s, Path.Combine(d, f), true);
        }
        SetCurrent(n);
        return true;
    }

    static void ClearAuth()
    {
        var c = ReadJson(ConfigPath);
        foreach (var k in c.Keys.Where(IsAuthKey).ToList()) c.Remove(k);
        WriteJson(ConfigPath, c);
        foreach (var f in AuthFiles) { var s = Path.Combine(AppDir, f); if (File.Exists(s)) File.Delete(s); }
    }

    static void RestoreProfile(string n)
    {
        var a = Path.Combine(Dir(n), "auth.json");
        ClearAuth();
        var c = ReadJson(ConfigPath);
        foreach (var kv in ReadJson(a)) c[kv.Key] = kv.Value;
        WriteJson(ConfigPath, c);
        foreach (var f in AuthFiles) { var s = Path.Combine(Dir(n), f); if (File.Exists(s)) File.Copy(s, Path.Combine(AppDir, f), true); }
        SetCurrent(n);
    }

    public static void Switch(string n)
    {
        if (!HasSession(Path.Combine(Dir(n), AuthFiles[0]))) throw new Exception("「" + n + "」还没有保存完整的登录状态，请先切到它并点「保存登录」");
        var cur = GetCurrent();
        WaitFlushIfNew(cur);
        Status("正在切换到「" + n + "」…");
        StopClaude();
        if (!string.IsNullOrEmpty(cur)) SaveProfile(cur);
        Sync();
        RestoreProfile(n);
        SetQuota(n, "activeSince", DateTime.Now.ToString("o"));
        StartClaude();
    }

    // ---- 额度窗口：每周重置为用户设定的固定时间；5 小时窗口从窗口内第一条消息开始 ----
    static string QuotaPath(string n) { return Path.Combine(Dir(n), "quota.txt"); }
    public static Dictionary<string, string> Quota(string n) { return LoadKv(QuotaPath(n)); }
    public static void SetQuota(string n, string k, string v)
    {
        var q = Quota(n);
        q[k] = v;
        File.WriteAllLines(QuotaPath(n), q.Select(kv => kv.Key + "=" + kv.Value), Utf8);
    }
    static DateTime? Time(Dictionary<string, string> q, string k)
    {
        DateTime t;
        return q.ContainsKey(k) && DateTime.TryParse(q[k], null, System.Globalization.DateTimeStyles.RoundtripKind, out t) ? t : (DateTime?)null;
    }

    static readonly string[] WeekNames = { "周日", "周一", "周二", "周三", "周四", "周五", "周六" };

    // weekly=2 11:00 → 下一次重置时间
    public static DateTime? NextWeekly(string n)
    {
        var q = Quota(n);
        if (!q.ContainsKey("weekly")) return null;
        var p = q["weekly"].Split(' ');
        var dow = int.Parse(p[0]);
        var tm = TimeSpan.Parse(p[1]);
        var d = DateTime.Today.AddDays((dow - (int)DateTime.Today.DayOfWeek + 7) % 7) + tm;
        if (d <= DateTime.Now) d = d.AddDays(7);
        return d;
    }

    public static string WeeklyText(string n)
    {
        var q = Quota(n);
        if (!q.ContainsKey("weekly")) return "";
        var p = q["weekly"].Split(' ');
        return WeekNames[int.Parse(p[0])] + " " + p[1];
    }

    // 解析“周二 11:00”
    public static bool SetWeekly(string n, string s)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s ?? "", @"周\s*([日天一二三四五六])\s*(\d{1,2})\s*[:：]\s*(\d{2})");
        if (!m.Success) return false;
        var dow = "日一二三四五六".IndexOf(m.Groups[1].Value == "天" ? "日" : m.Groups[1].Value);
        int h = int.Parse(m.Groups[2].Value), mi = int.Parse(m.Groups[3].Value);
        if (h > 23 || mi > 59) return false;
        SetQuota(n, "weekly", dow + " " + h.ToString("00") + ":" + mi.ToString("00"));
        return true;
    }

    // 当前 5 小时窗口的开始时间（null 表示还没开始）
    public static DateTime? WindowStart(string n)
    {
        var s = Time(Quota(n), "fhStart");
        return s.HasValue && DateTime.Now < s.Value.AddHours(5) ? s : null;
    }

    // 当前账号：上一窗口结束后（且切到该账号之后）的第一条消息即为新窗口起点
    public static void UpdateWindow(string n)
    {
        var q = Quota(n);
        var s = Time(q, "fhStart");
        if (s.HasValue && DateTime.Now < s.Value.AddHours(5)) return;
        var b = s.HasValue ? s.Value.AddHours(5) : DateTime.Now.AddHours(-5);
        var act = Time(q, "activeSince");
        if (act.HasValue && act.Value > b) b = act.Value;
        var first = FirstActivity(b);
        if (first.HasValue) SetQuota(n, "fhStart", first.Value.ToString("o"));
    }

    static readonly System.Text.RegularExpressions.Regex TsRe = new System.Text.RegularExpressions.Regex("\"timestamp\":\"([^\"]+)\"");
    static DateTime? FirstActivity(DateTime after)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        if (!Directory.Exists(root)) return null;
        DateTime? min = null;
        foreach (var f in new DirectoryInfo(root).EnumerateFiles("*.jsonl", SearchOption.AllDirectories))
        {
            if (f.LastWriteTime < after) continue;
            using (var sr = new StreamReader(new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Utf8))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    var m = TsRe.Match(line);
                    DateTime t;
                    if (!m.Success || !DateTime.TryParse(m.Groups[1].Value, null, System.Globalization.DateTimeStyles.RoundtripKind, out t)) continue;
                    t = t.ToLocalTime();
                    if (t >= after && (!min.HasValue || t < min.Value)) min = t;
                }
            }
        }
        return min;
    }

    public static void Add(string n)
    {
        var cur = GetCurrent();
        WaitFlushIfNew(cur);
        Status("正在准备登录新账号…");
        StopClaude();
        if (!string.IsNullOrEmpty(cur)) SaveProfile(cur);
        Sync();
        ClearAuth();
        Directory.CreateDirectory(Dir(n));
        SetCurrent(n);
        SetQuota(n, "activeSince", DateTime.Now.ToString("o"));
        StartClaude();
    }

    public static void SaveCurrent()
    {
        var cur = GetCurrent();
        WaitFlushIfNew(cur);
        Status("正在保存「" + cur + "」的登录状态…");
        StopClaude();
        var ok = SaveProfile(cur);
        StartClaude();
        if (!ok) throw new Exception("没有检测到「" + cur + "」的登录信息，请先在 Claude 里完成登录，再点「保存登录」。");
    }

    public static void Rename(string a, string b)
    {
        Directory.Move(Dir(a), Dir(b));
        if (GetCurrent() == a) SetCurrent(b);
    }

    public static void Delete(string n) { Directory.Delete(Dir(n), true); }

    static List<string> LeafDirs()
    {
        var r = new List<string>();
        if (!Directory.Exists(Sessions)) return r;
        foreach (var a in Directory.GetDirectories(Sessions)) r.AddRange(Directory.GetDirectories(a));
        return r;
    }

    public static int SessionCount()
    {
        var c = 0;
        foreach (var d in LeafDirs()) c = Math.Max(c, Directory.GetFiles(d, "local_*.json").Length);
        return c;
    }

    public static int DirCount() { return LeafDirs().Count; }

    // 所有账号/组织目录的会话索引保持一致：同名取最新，删除标记全局生效
    public static int Sync()
    {
        lock (SyncLock)
        {
            var dirs = LeafDirs();
            LastSync = DateTime.Now;
            if (dirs.Count < 2) return 0;
            var files = new List<FileInfo>();
            foreach (var d in dirs)
                foreach (var f in new DirectoryInfo(d).GetFiles())
                    if ((f.Name.StartsWith("local_") && f.Name.EndsWith(".json")) || f.Name.StartsWith("deleted_") || f.Name == "archived-sessions.idx")
                        files.Add(f);
            var deleted = new HashSet<string>(files.Where(f => f.Name.StartsWith("deleted_")).Select(f => f.Name.Substring(8)));
            int n = 0;
            foreach (var g in files.GroupBy(f => f.Name))
            {
                var src = g.OrderByDescending(f => f.LastWriteTimeUtc).First();
                bool isLocal = g.Key.StartsWith("local_");
                string id = isLocal ? g.Key.Substring(6, g.Key.Length - 11) : null;
                foreach (var d in dirs)
                {
                    var t = Path.Combine(d, g.Key);
                    try
                    {
                        if (isLocal && deleted.Contains(id))
                        {
                            if (File.Exists(t)) { File.Delete(t); n++; }
                            continue;
                        }
                        if (string.Equals(t, src.FullName, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!File.Exists(t) || File.GetLastWriteTimeUtc(t) < src.LastWriteTimeUtc)
                        {
                            File.Copy(src.FullName, t, true);
                            File.SetLastWriteTimeUtc(t, src.LastWriteTimeUtc);
                            n++;
                        }
                    }
                    catch (IOException) { } // 文件正被 Claude 写入，下一轮再同步
                }
            }
            return n;
        }
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "ClaudeAccountManager";
    public static bool GetAutoStart()
    {
        using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(RunName) != null;
    }
    public static void SetAutoStart(bool on)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (on) k.SetValue(RunName, "\"" + Assembly.GetExecutingAssembly().Location + "\" --tray");
            else k.DeleteValue(RunName, false);
        }
    }

    static string SettingsPath { get { return Path.Combine(Root, "settings.txt"); } }
    static Dictionary<string, string> LoadKv(string path)
    {
        var r = new Dictionary<string, string>();
        if (File.Exists(path))
            foreach (var l in File.ReadAllLines(path, Utf8))
            {
                var i = l.IndexOf('=');
                if (i > 0) r[l.Substring(0, i)] = l.Substring(i + 1);
            }
        return r;
    }
    public static Dictionary<string, string> LoadSettings()
    {
        var r = new Dictionary<string, string> { { "realtime", "1" }, { "tray", "1" } };
        foreach (var kv in LoadKv(SettingsPath)) r[kv.Key] = kv.Value;
        return r;
    }
    public static void SaveSettings(Dictionary<string, string> s)
    {
        File.WriteAllLines(SettingsPath, s.Select(kv => kv.Key + "=" + kv.Value), Utf8);
    }
}

// ---- Skill 同步：调用 ai-config 仓库的 sync_ai_config.py，汇总三端每个 Skill 的状态 ----
class SkillRow
{
    public string Name;
    public bool Managed;
    public Dictionary<string, string> State = new Dictionary<string, string>();          // ok/changed/pending/local/excluded/absent
    public Dictionary<string, List<string>> Changes = new Dictionary<string, List<string>>();
    public Dictionary<string, string> Errors = new Dictionary<string, string>();
    public List<string> Adapt = new List<string>();
}

static class SkillSync
{
    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public static readonly string Repo = Path.Combine(Home, ".codex");
    public static readonly string[] Clients = { "codex", "claude", "pi" };
    public static readonly Dictionary<string, string> Homes = new Dictionary<string, string> {
        { "codex", Repo }, { "claude", Path.Combine(Home, ".claude") }, { "pi", Path.Combine(Home, ".pi", "agent") } };
    static readonly JavaScriptSerializer Js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

    public static Dictionary<string, object> Py(IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo("python", "-X utf8 scripts/sync_ai_config.py " + string.Join(" ", args.Select(a => "\"" + a + "\"")))
        {
            WorkingDirectory = Repo, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
        };
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        using (var p = Process.Start(psi))
        {
            var err = p.StandardError.ReadToEndAsync();
            var o = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            var line = o.Split('\n').LastOrDefault(l => l.TrimStart().StartsWith("{"));
            if (line == null) throw new Exception("同步脚本没有返回结果：" + (err.Result + o).Trim());
            return (Dictionary<string, object>)Js.DeserializeObject(line);
        }
    }

    static IEnumerable<Dictionary<string, object>> Items(Dictionary<string, object> d, string k)
    {
        object v;
        return d.TryGetValue(k, out v) && v is object[] ? ((object[])v).Cast<Dictionary<string, object>>() : Enumerable.Empty<Dictionary<string, object>>();
    }
    static IEnumerable<string> Strings(Dictionary<string, object> d, string k)
    {
        object v;
        return d.TryGetValue(k, out v) && v is object[] ? ((object[])v).Cast<string>() : Enumerable.Empty<string>();
    }
    static string SkillOf(string path) { var p = path.Split('/'); return p.Length > 2 && p[0] == "skills" ? p[1] : null; }
    static bool Noise(string path) { return path.Contains("/__pycache__/") || path.EndsWith(".pyc") || path.Contains(".bak") || path.Contains("/.trash"); }

    static HashSet<string> Excluded(string client)
    {
        var r = new HashSet<string>();
        var cfg = (Dictionary<string, object>)Js.DeserializeObject(File.ReadAllText(Path.Combine(Repo, @"sync\clients.json"), Encoding.UTF8));
        object c;
        if (client != "codex" && cfg.TryGetValue(client, out c))
            foreach (var e in Strings((Dictionary<string, object>)c, "exclude")) { var s = e.StartsWith("skills/") ? e.Substring(7) : null; if (s != null) r.Add(s); }
        return r;
    }

    // inspect 某端：遇到某个 Skill 报错就把它剔除后重试，其余照常检查
    static void Inspect(string client, List<string> skills, Dictionary<string, SkillRow> rows)
    {
        var left = new List<string>(skills);
        while (left.Count > 0)
        {
            var args = new List<string> { "inspect", "--client", client };
            foreach (var s in left) { args.Add("--path"); args.Add("skills/" + s); }
            var r = Py(args);
            object err;
            if (r.TryGetValue("error", out err))
            {
                var bad = left.FirstOrDefault(s => ((string)err).Contains("skills/" + s + "/") || ((string)err).EndsWith("skills/" + s));
                if (bad == null) { foreach (var s in left) rows[s].Errors[client] = (string)err; return; }
                rows[bad].Errors[client] = (string)err;
                left.Remove(bad);
                continue;
            }
            foreach (var ch in Items(r, "changes"))
            {
                var from = (string)ch["from"];
                var s = SkillOf(from);
                if (s == null || Noise(from) || !rows.ContainsKey(s)) continue;
                if (!rows[s].Changes.ContainsKey(client)) rows[s].Changes[client] = new List<string>();
                rows[s].Changes[client].Add(from);
            }
            return;
        }
    }

    public static string DryRunError;

    public static List<SkillRow> Scan()
    {
        var manifest = (Dictionary<string, object>)Js.DeserializeObject(File.ReadAllText(Path.Combine(Repo, @"sync\manifest.json"), Encoding.UTF8));
        var managed = new HashSet<string>(Items(manifest, "entries").Select(e => SkillOf((string)e["path"])).Where(s => s != null));
        var present = new Dictionary<string, HashSet<string>>();
        var excluded = new Dictionary<string, HashSet<string>>();
        foreach (var c in Clients)
        {
            var dir = Path.Combine(Homes[c], "skills");
            present[c] = new HashSet<string>(Directory.Exists(dir) ? Directory.GetDirectories(dir).Select(Path.GetFileName).Where(n => !n.StartsWith(".")) : new string[0]);
            excluded[c] = Excluded(c);
        }
        var rows = new Dictionary<string, SkillRow>();
        foreach (var n in managed.Concat(present.Values.SelectMany(x => x)).Distinct().OrderBy(x => x))
            rows[n] = new SkillRow { Name = n, Managed = managed.Contains(n) };

        Dictionary<string, object> dry = null;
        var threads = new List<Thread>();
        threads.Add(new Thread(() => { try { dry = Py(new[] { "install", "--dry-run" }); } catch (Exception e) { DryRunError = e.Message; } }));
        foreach (var c0 in Clients)
        {
            var c = c0;
            var list = rows.Values.Where(r => r.Managed && present[c].Contains(r.Name) && !excluded[c].Contains(r.Name)).Select(r => r.Name).ToList();
            threads.Add(new Thread(() => { try { Inspect(c, list, rows); } catch (Exception e) { foreach (var s in list) rows[s].Errors[c] = e.Message; } }));
        }
        DryRunError = null;
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        var pending = Clients.ToDictionary(c => c, c => new HashSet<string>());
        if (dry != null)
        {
            object e;
            if (dry.TryGetValue("error", out e)) DryRunError = (string)e;
            object cl;
            if (dry.TryGetValue("clients", out cl))
                foreach (var kv in (Dictionary<string, object>)cl)
                    foreach (var p in Strings((Dictionary<string, object>)kv.Value, "copied").Concat(Strings((Dictionary<string, object>)kv.Value, "deleted")))
                    { var s = SkillOf(p); if (s != null && pending.ContainsKey(kv.Key)) pending[kv.Key].Add(s); }
            object na;
            if (dry.TryGetValue("needs_adaptation", out na))
                foreach (var kv in (Dictionary<string, object>)na)
                    foreach (var h in ((object[])kv.Value).Cast<Dictionary<string, object>>())
                    {
                        var s = SkillOf((string)h["path"]);
                        if (s != null && rows.ContainsKey(s))
                            rows[s].Adapt.Add(kv.Key + "  " + h["path"] + ":" + h["line"] + "  " + string.Join(", ", ((object[])h["terms"]).Cast<string>()));
                    }
        }

        foreach (var r in rows.Values)
            foreach (var c in Clients)
            {
                string st;
                if (excluded[c].Contains(r.Name)) st = "excluded";
                else if (!r.Managed) st = present[c].Contains(r.Name) ? "local" : "absent";
                else if (r.Errors.ContainsKey(c)) st = "error";
                else if (r.Changes.ContainsKey(c)) st = "changed";
                else if (pending[c].Contains(r.Name) || !present[c].Contains(r.Name)) st = "pending";
                else st = "ok";
                r.State[c] = st;
            }
        return rows.Values.ToList();
    }

    // 从某端导入改动：导入唯一源码、生成快照、更新三端、提交并推送
    public static string Publish(string client, IEnumerable<string> paths, string skill)
    {
        var args = new List<string> { "publish", "--client", client, "--push", "--message", "sync(" + skill + "): 从 " + client + " 导入改动" };
        foreach (var p in paths) { args.Add("--path"); args.Add(p); }
        var r = Py(args);
        object e;
        if (r.TryGetValue("error", out e)) throw new Exception((string)e);
        object c;
        return r.TryGetValue("commit", out c) ? "已提交 " + ((string)c).Substring(0, 7) + " 并推送" : "无需提交";
    }

    // 纳入一个仅存在于某端的新 Skill：先 inspect 出具体文件（去掉缓存/备份），再发布
    public static string Adopt(string client, string skill)
    {
        var r = Py(new[] { "inspect", "--client", client, "--path", "skills/" + skill });
        object e;
        if (r.TryGetValue("error", out e)) throw new Exception((string)e);
        var files = Items(r, "changes").Select(ch => (string)ch["from"]).Where(p => !Noise(p)).ToList();
        if (files.Count == 0) throw new Exception("没有可导入的文件");
        return Publish(client, files, skill);
    }

    public static string Install()
    {
        var r = Py(new[] { "install" });
        object e;
        if (r.TryGetValue("error", out e)) throw new Exception((string)e);
        var cl = (Dictionary<string, object>)r["clients"];
        return string.Join("，", cl.Select(kv => kv.Key + " " + Strings((Dictionary<string, object>)kv.Value, "copied").Count() + " 个文件"));
    }
}

static class Ui
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int s);

    public static void Round(Window w)
    {
        w.SourceInitialized += (s, e) =>
        {
            int v = 2;
            DwmSetWindowAttribute(new WindowInteropHelper(w).Handle, 33, ref v, 4);
        };
    }

    public static T Load<T>(string res)
    {
        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(res)) return (T)XamlReader.Load(s);
    }

    public static Style S(string k) { return (Style)Application.Current.FindResource(k); }

    public static SolidColorBrush B(string hex) { return (SolidColorBrush)new BrushConverter().ConvertFromString(hex); }

    // input=true 返回输入文字；否则返回 "" 表示确认；取消返回 null
    public static string Ask(Window owner, string title, string msg, bool input, string def, string ok, bool danger)
    {
        var w = Load<Window>("dialog.xaml");
        Round(w);
        w.Owner = owner;
        ((TextBlock)w.FindName("T")).Text = title;
        ((TextBlock)w.FindName("M")).Text = msg;
        var tb = (TextBox)w.FindName("In");
        var okb = (Button)w.FindName("Ok");
        okb.Content = ok;
        if (danger) okb.Background = B("#C2412D");
        if (input) { tb.Text = def ?? ""; tb.SelectAll(); w.Loaded += (s, e) => tb.Focus(); }
        else tb.Visibility = Visibility.Collapsed;
        okb.Click += (s, e) => { w.DialogResult = true; };
        return w.ShowDialog() == true ? (input ? tb.Text.Trim() : "") : null;
    }
}

class MainWin
{
    readonly Window w;
    readonly StackPanel list;
    readonly TextBlock txtCurrent, txtSessions, txtDirs, txtLastSync, busyText;
    readonly Grid busy;
    readonly CheckBox chkRealtime, chkAuto, chkTray;
    readonly ObservableCollection<string> logs = new ObservableCollection<string>();
    readonly Dictionary<string, string> settings = Core.LoadSettings();
    readonly WF.NotifyIcon tray = new WF.NotifyIcon();
    FileSystemWatcher fsw;
    Timer debounce;
    bool exiting;
    static readonly string[] Palette = { "#D97757", "#5B8DEF", "#3FA27A", "#9B6BDB", "#E0A23B", "#E05D87" };

    T F<T>(string n) { return (T)w.FindName(n); }

    public MainWin(bool hidden)
    {
        w = Ui.Load<Window>("main.xaml");
        Ui.Round(w);
        list = F<StackPanel>("List");
        txtCurrent = F<TextBlock>("TxtCurrent");
        txtSessions = F<TextBlock>("TxtSessions");
        txtDirs = F<TextBlock>("TxtDirs");
        txtLastSync = F<TextBlock>("TxtLastSync");
        busy = F<Grid>("Busy");
        busyText = F<TextBlock>("BusyText");
        chkRealtime = F<CheckBox>("ChkRealtime");
        chkAuto = F<CheckBox>("ChkAuto");
        chkTray = F<CheckBox>("ChkTray");
        F<ItemsControl>("LogList").ItemsSource = logs;
        Core.Status = s => w.Dispatcher.BeginInvoke(new Action(() => busyText.Text = s));

        F<Button>("BtnMin").Click += (s, e) => w.WindowState = WindowState.Minimized;
        F<Button>("BtnClose").Click += (s, e) => w.Close();
        w.Closing += (s, e) => { if (!exiting && chkTray.IsChecked == true) { e.Cancel = true; w.Hide(); } };
        w.Closed += (s, e) => Exit();

        chkRealtime.IsChecked = settings["realtime"] == "1";
        chkTray.IsChecked = settings["tray"] == "1";
        chkAuto.IsChecked = Core.GetAutoStart();
        chkRealtime.Click += (s, e) => { settings["realtime"] = chkRealtime.IsChecked == true ? "1" : "0"; Core.SaveSettings(settings); ApplyWatcher(); };
        chkTray.Click += (s, e) => { settings["tray"] = chkTray.IsChecked == true ? "1" : "0"; Core.SaveSettings(settings); };
        chkAuto.Click += (s, e) => { Core.SetAutoStart(chkAuto.IsChecked == true); Log(chkAuto.IsChecked == true ? "已开启开机自启" : "已关闭开机自启"); };

        F<Button>("BtnSync").Click += (s, e) => Run("正在同步会话…", () => Log("手动同步完成，更新 " + Core.Sync() + " 个文件"));
        F<Button>("BtnFolder").Click += (s, e) => Process.Start("explorer.exe", "\"" + Core.Sessions + "\"");
        F<Button>("BtnAdd").Click += (s, e) => AddAccount();
        F<Button>("TabAccounts").Click += (s, e) => ShowPage(false);
        F<Button>("TabSkills").Click += (s, e) => ShowPage(true);
        F<Button>("BtnScan").Click += (s, e) => ScanSkills();
        F<Button>("BtnInstall").Click += (s, e) => Run("正在同步到三端…", () => { Log("同步到三端：" + SkillSync.Install()); skillRows = SkillSync.Scan(); skillAt = DateTime.Now; }, "已同步到三端");
        ShowPage(false);

        debounce = new Timer(_ =>
        {
            try { var n = Core.Sync(); if (n > 0) Log("实时同步 " + n + " 个文件"); } catch (Exception ex) { Log("同步出错：" + ex.Message); }
            w.Dispatcher.BeginInvoke(new Action(RefreshStats));
        });

        tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location);
        tray.Text = "Claude 账号管家";
        tray.Visible = true;
        tray.DoubleClick += (s, e) => ShowWin();

        Core.EnsureCurrentDir();
        Log("已就绪");
        Refresh();
        ApplyWatcher();
        var usageTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        usageTimer.Tick += (s, e) => { if (busy.Visibility != Visibility.Visible) Refresh(); };
        usageTimer.Start();
        if (!hidden) w.Show();
    }

    List<SkillRow> skillRows;
    DateTime skillAt;
    static readonly Dictionary<string, string> ClientName = new Dictionary<string, string> { { "codex", "Codex" }, { "claude", "Claude" }, { "pi", "Pi" } };

    void ShowPage(bool skills)
    {
        F<Grid>("PageAccounts").Visibility = skills ? Visibility.Collapsed : Visibility.Visible;
        F<Grid>("PageSkills").Visibility = skills ? Visibility.Visible : Visibility.Collapsed;
        var on = F<Button>(skills ? "TabSkills" : "TabAccounts");
        var off = F<Button>(skills ? "TabAccounts" : "TabSkills");
        on.Background = Brushes.White; on.Foreground = Ui.B("#1F1E1D");
        off.ClearValue(Control.BackgroundProperty); off.ClearValue(Control.ForegroundProperty);
        if (skills && skillRows == null) ScanSkills();
    }

    void ScanSkills()
    {
        Run("正在预检三端 Skill…（约 10 秒）", () => { skillRows = SkillSync.Scan(); skillAt = DateTime.Now; }, null);
    }

    static string Overall(SkillRow r)
    {
        if (r.Errors.Count > 0) return "error";
        if (r.Changes.Count > 0) return "changed";
        if (r.Adapt.Count > 0) return "adapt";
        if (r.State.Values.Contains("pending")) return "pending";
        if (!r.Managed) return "local";
        return "ok";
    }

    static readonly Dictionary<string, string[]> StatusLook = new Dictionary<string, string[]> {
        { "ok", new[] { "一致", "#3FA27A", "#E7F4EE" } }, { "changed", new[] { "有改动", "#C2593A", "#FBEDE7" } },
        { "adapt", new[] { "需适配", "#B7791F", "#FBF1DE" } }, { "pending", new[] { "待安装", "#3B6FD8", "#E6EEFC" } },
        { "local", new[] { "未纳入同步", "#7E57C2", "#F0EAF9" } }, { "error", new[] { "无法检查", "#C2412D", "#F9E3DF" } } };
    static readonly Dictionary<string, string[]> CellLook = new Dictionary<string, string[]> {
        { "ok", new[] { "✓", "#3FA27A" } }, { "changed", new[] { "改动", "#C2593A" } }, { "pending", new[] { "待安装", "#3B6FD8" } },
        { "local", new[] { "仅此端", "#7E57C2" } }, { "excluded", new[] { "不同步", "#A8A7A2" } }, { "absent", new[] { "—", "#C9C7C1" } },
        { "error", new[] { "出错", "#C2412D" } } };

    static Grid SkillGrid()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var wdt in new[] { 80, 80, 80, 110, 230 }) g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(wdt) });
        return g;
    }

    static void Put(Grid g, UIElement e, int col) { Grid.SetColumn(e, col); g.Children.Add(e); }

    void RenderSkills()
    {
        if (skillRows == null) return;
        F<TextBlock>("TxtSkillSub").Text = "Codex · Claude · Pi 三端  ·  上次预检 " + skillAt.ToString("HH:mm:ss") +
            (SkillSync.DryRunError != null ? "  ·  预检出错：" + SkillSync.DryRunError : "");
        var chips = F<WrapPanel>("SkillChips");
        chips.Children.Clear();
        foreach (var k in new[] { "ok", "changed", "adapt", "pending", "local", "error" })
        {
            var n = skillRows.Count(r => Overall(r) == k);
            if (n == 0) continue;
            var l = StatusLook[k];
            chips.Children.Add(new Border { Background = Ui.B(l[2]), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0),
                Child = new TextBlock { Text = l[0] + "  " + n, Foreground = Ui.B(l[1]), FontSize = 12, FontWeight = FontWeights.SemiBold } });
        }
        F<Button>("BtnInstall").IsEnabled = skillRows.Any(r => r.State.Values.Contains("pending"));

        var head = F<Grid>("SkillHeader");
        head.Children.Clear(); head.ColumnDefinitions.Clear();
        foreach (var cd in SkillGrid().ColumnDefinitions.ToList()) { var c = new ColumnDefinition { Width = cd.Width }; head.ColumnDefinitions.Add(c); }
        var titles = new[] { "Skill", "Codex", "Claude", "Pi", "状态", "操作" };
        for (int i = 0; i < titles.Length; i++) Put(head, new TextBlock { Text = titles[i], Foreground = Ui.B("#8A8984"), FontSize = 12 }, i);

        var list = F<StackPanel>("SkillList");
        list.Children.Clear();
        var rank = new[] { "error", "changed", "adapt", "pending", "ok", "local" };
        foreach (var r in skillRows.OrderBy(x => Array.IndexOf(rank, Overall(x))).ThenBy(x => x.Name))
        {
            var g = SkillGrid();
            g.Margin = new Thickness(18, 10, 18, 10);
            Put(g, new TextBlock { Text = r.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }, 0);
            for (int i = 0; i < SkillSync.Clients.Length; i++)
            {
                var c = SkillSync.Clients[i];
                var look = CellLook[r.State[c]];
                var t = new TextBlock { Text = look[0], Foreground = Ui.B(look[1]), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                if (r.Errors.ContainsKey(c)) t.ToolTip = r.Errors[c];
                if (r.Changes.ContainsKey(c)) t.ToolTip = string.Join("\n", r.Changes[c]);
                Put(g, t, i + 1);
            }
            var st = StatusLook[Overall(r)];
            Put(g, new Border { Background = Ui.B(st[2]), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 2, 8, 3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = st[0], Foreground = Ui.B(st[1]), FontSize = 12 } }, 4);

            var acts = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            var row = r;
            foreach (var kv in r.Changes)
            {
                var c = kv.Key; var files = kv.Value;
                acts.Children.Add(SmallBtn("导入 " + ClientName[c], () => ImportSkill(row.Name, c, files)));
            }
            if (!r.Managed)
            {
                var c = SkillSync.Clients.FirstOrDefault(x => r.State[x] == "local");
                if (c != null) acts.Children.Add(SmallBtn("纳入同步", () => AdoptSkill(row.Name, c)));
            }
            if (r.Adapt.Count > 0)
                acts.Children.Add(SmallBtn("查看适配", () => Ui.Ask(w, "「" + row.Name + "」需要适配", string.Join("\n", row.Adapt) +
                    "\n\n处理方法：在 ai-config 的 sync/clients.json 里给这个写法加术语（terms）或替换规则；如果本来就该保留原文，把文件加入 leftovers_allow。", false, null, "知道了", false)));
            if (r.Errors.Count > 0)
                acts.Children.Add(SmallBtn("查看原因", () => Ui.Ask(w, "「" + row.Name + "」无法检查", string.Join("\n", row.Errors.Select(x => ClientName[x.Key] + "：" + x.Value)), false, null, "知道了", false)));
            Put(g, acts, 5);

            list.Children.Add(new Border { BorderBrush = Ui.B("#F1EFEA"), BorderThickness = new Thickness(0, 0, 0, 1), Child = g });
        }
    }

    Button SmallBtn(string text, Action a)
    {
        var b = new Button { Content = text, Style = Ui.S("Small"), Margin = new Thickness(0, 2, 6, 2) };
        b.Click += (s, e) => a();
        return b;
    }

    void ImportSkill(string skill, string client, List<string> files)
    {
        var msg = "把 " + ClientName[client] + " 端「" + skill + "」的 " + files.Count + " 个改动文件导入共享源码，生成三端快照并安装到三端，然后提交推送到 ai-config。\n\n" +
            string.Join("\n", files.Take(8)) + (files.Count > 8 ? "\n……" : "");
        if (Ui.Ask(w, "导入改动", msg, false, null, "导入并同步", false) == null) return;
        Run("正在导入「" + skill + "」并同步三端…", () => { Log("「" + skill + "」" + SkillSync.Publish(client, files, skill)); skillRows = SkillSync.Scan(); skillAt = DateTime.Now; }, "「" + skill + "」已同步到三端");
    }

    void AdoptSkill(string skill, string client)
    {
        if (Ui.Ask(w, "纳入同步", "「" + skill + "」目前只在 " + ClientName[client] + " 端。纳入后会导入共享源码，安装到其他客户端，并提交推送到 ai-config。\n\n如果它只适用于 " + ClientName[client] + "，请改为在 sync/clients.json 中排除，不要纳入。", false, null, "纳入并同步", false) == null) return;
        Run("正在纳入「" + skill + "」…", () => { Log("「" + skill + "」" + SkillSync.Adopt(client, skill)); skillRows = SkillSync.Scan(); skillAt = DateTime.Now; }, "「" + skill + "」已纳入同步");
    }

    void ShowWin()
    {
        w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    void Exit()
    {
        if (exiting && !tray.Visible) return;
        exiting = true;
        if (fsw != null) fsw.Dispose();
        tray.Visible = false;
        tray.Dispose();
        Application.Current.Shutdown();
    }

    void Log(string s)
    {
        w.Dispatcher.BeginInvoke(new Action(() =>
        {
            logs.Insert(0, DateTime.Now.ToString("HH:mm:ss") + "   " + s);
            while (logs.Count > 200) logs.RemoveAt(logs.Count - 1);
        }));
    }

    void Toast(string s)
    {
        var t = F<Border>("Toast");
        F<TextBlock>("ToastText").Text = s;
        t.Visibility = Visibility.Visible;
        var tm = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        tm.Tick += (a, b) => { tm.Stop(); t.Visibility = Visibility.Collapsed; };
        tm.Start();
    }

    void Run(string msg, Action act) { Run(msg, act, null); }

    void Run(string msg, Action act, string done)
    {
        busyText.Text = msg;
        busy.Visibility = Visibility.Visible;
        new Thread(() =>
        {
            string err = null;
            try { act(); } catch (Exception ex) { err = ex.Message; }
            w.Dispatcher.BeginInvoke(new Action(() =>
            {
                busy.Visibility = Visibility.Collapsed;
                if (err != null) { Log("出错：" + err); if (w.IsVisible) Ui.Ask(w, "操作失败", err, false, null, "知道了", false); }
                else if (done != null) { Log(done); Toast("✓  " + done); }
                Refresh();
            }));
        }) { IsBackground = true }.Start();
    }

    void ApplyWatcher()
    {
        if (fsw != null) { fsw.Dispose(); fsw = null; }
        if (chkRealtime.IsChecked != true || !Directory.Exists(Core.Sessions)) return;
        fsw = new FileSystemWatcher(Core.Sessions) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite };
        FileSystemEventHandler h = (s, e) => debounce.Change(1500, Timeout.Infinite);
        fsw.Changed += h; fsw.Created += h; fsw.Deleted += h;
        fsw.Renamed += (s, e) => debounce.Change(1500, Timeout.Infinite);
        fsw.EnableRaisingEvents = true;
        debounce.Change(200, Timeout.Infinite);
        Log("实时同步已开启");
    }

    void RefreshStats()
    {
        txtSessions.Text = Core.SessionCount().ToString();
        txtDirs.Text = Core.DirCount().ToString();
        txtLastSync.Text = Core.LastSync.HasValue ? "上次同步  " + Core.LastSync.Value.ToString("MM-dd HH:mm:ss") : "尚未同步";
    }

    void Refresh()
    {
        var cur = Core.GetCurrent();
        var profs = Core.List();
        txtCurrent.Text = string.IsNullOrEmpty(cur) ? "还没有账号，先添加一个" : "当前使用：" + cur + "   ·   共 " + profs.Count + " 个账号";
        list.Children.Clear();
        int i = 0;
        foreach (var p in profs) list.Children.Add(Card(p, p.Name == cur, i++));
        RefreshStats();
        BuildTrayMenu(profs, cur);
        RenderSkills();
    }

    UIElement Card(Prof p, bool isCur, int idx)
    {
        var card = new Border { Style = Ui.S("Card"), Margin = new Thickness(0, 0, 0, 12), Padding = new Thickness(16, 14, 16, 14) };
        if (isCur) card.BorderBrush = Ui.B("#E9B7A3");
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var av = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(21), Background = Ui.B(Palette[idx % Palette.Length]), VerticalAlignment = VerticalAlignment.Center };
        av.Child = new TextBlock { Text = p.Name.Substring(0, 1).ToUpper(), Foreground = Brushes.White, FontSize = 17, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(av);

        var info = new StackPanel { Margin = new Thickness(14, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        line.Children.Add(new TextBlock { Text = p.Name, FontSize = 15, FontWeight = FontWeights.SemiBold });
        if (isCur)
        {
            var tag = new Border { Background = Ui.B("#FBEDE7"), CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 1, 7, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            tag.Child = new TextBlock { Text = "当前", Foreground = Ui.B("#C2593A"), FontSize = 11 };
            line.Children.Add(tag);
        }
        info.Children.Add(line);
        string sub;
        if (!p.Saved.HasValue) sub = isCur ? "登录状态尚未保存 · 切换账号时会自动保存" : "未保存登录状态";
        else sub = (p.Uuid != null ? "ID " + p.Uuid.Substring(0, 8) : "未登录") + "   ·   " + p.Saved.Value.ToString("MM-dd HH:mm") + " 保存";
        info.Children.Add(new TextBlock { Text = sub, Foreground = Ui.B("#8A8984"), FontSize = 12, Margin = new Thickness(0, 4, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
        var us = Core.GetUsage(p.Uuid);
        if (isCur) Core.UpdateWindow(p.Name);
        var now = DateTime.Now;
        var row = new WrapPanel();

        // 5 小时：从窗口内第一条消息起算
        int fhUsed; string fhText;
        var start = Core.WindowStart(p.Name);
        if (us != null && us.Exact && us.FhReset.HasValue && us.FhReset.Value > now) { fhUsed = us.Fh; fhText = ResetText(us.FhReset); }
        else if (start.HasValue) { fhUsed = us != null && us.T >= start.Value ? us.Fh : 0; fhText = ResetText(start.Value.AddHours(5)); }
        else { fhUsed = 0; fhText = "发第一条消息后开始计时"; }
        row.Children.Add(Meter("5 小时", fhUsed, fhText, null));

        // 每周：用户设定的固定时间
        int sdUsed; string sdText;
        var next = Core.NextWeekly(p.Name);
        if (next.HasValue) { sdUsed = us != null && us.T >= next.Value.AddDays(-7) ? us.Sd : 0; sdText = ResetText(next) + "  ✎"; }
        else { sdUsed = us != null ? us.Sd : 0; sdText = "设置每周重置时间  ✎"; }
        row.Children.Add(Meter("本周", sdUsed, sdText, () => EditWeekly(p.Name)));

        if (us != null)
        {
            var age = now - us.T;
            var ago = age.TotalMinutes < 1 ? "刚刚" : age.TotalHours < 1 ? (int)age.TotalMinutes + " 分钟前" : age.TotalDays < 1 ? (int)age.TotalHours + " 小时前" : (int)age.TotalDays + " 天前";
            row.Children.Add(new TextBlock { Text = "用量 " + ago + "更新", Foreground = Ui.B("#A8A7A2"), FontSize = 11, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 8, 0, 0) });
        }
        info.Children.Add(row);
        Grid.SetColumn(info, 1);
        g.Children.Add(info);

        var acts = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (isCur)
            acts.Children.Add(Btn(p.Saved.HasValue ? "更新登录" : "保存登录", p.Saved.HasValue ? "Ghost" : "Primary", () => Run("正在保存「" + p.Name + "」的登录状态…", Core.SaveCurrent, "「" + p.Name + "」的登录状态已保存")));
        else
        {
            var sw = Btn("切换", "Primary", () => Run("正在切换到「" + p.Name + "」…", () => Core.Switch(p.Name), "已切换到「" + p.Name + "」"));
            sw.IsEnabled = p.Saved.HasValue;
            acts.Children.Add(sw);
        }
        acts.Children.Add(Btn("重命名", "Ghost", () => RenameAccount(p.Name)));
        var del = Btn("删除", "Ghost", () => DeleteAccount(p.Name));
        del.IsEnabled = !isCur;
        acts.Children.Add(del);
        Grid.SetColumn(acts, 2);
        g.Children.Add(acts);

        card.Child = g;
        return card;
    }

    static string ResetText(DateTime? r)
    {
        var d = r.Value.Date == DateTime.Today ? "今天 " : r.Value.Date == DateTime.Today.AddDays(1) ? "明天 " : "周" + "日一二三四五六".Substring((int)r.Value.DayOfWeek, 1) + " ";
        return d + r.Value.ToString("HH:mm") + " 重置";
    }

    void EditWeekly(string n)
    {
        var cur = Core.WeeklyText(n);
        var s = Ui.Ask(w, "每周重置时间", "「" + n + "」的每周额度在每周哪天几点重置？格式如：周二 11:00", true, cur == "" ? "周二 11:00" : cur, "保存", false);
        if (s == null) return;
        if (!Core.SetWeekly(n, s)) { Ui.Ask(w, "格式不对", "请按「周二 11:00」这样的格式填写。", false, null, "知道了", false); return; }
        Log("「" + n + "」每周重置时间设为 " + Core.WeeklyText(n));
        Refresh();
    }

    UIElement Meter(string label, int used, string reset, Action onClick)
    {
        var left = Math.Max(0, Math.Min(100, used));
        var sp = new StackPanel { Width = 140, Margin = new Thickness(0, 8, 14, 0) };
        var top = new DockPanel();
        var val = new TextBlock { Text = "已用 " + left + "%", FontSize = 11, Foreground = Ui.B("#5E5D59"), FontWeight = FontWeights.SemiBold };
        DockPanel.SetDock(val, Dock.Right);
        top.Children.Add(val);
        top.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Ui.B("#8A8984") });
        sp.Children.Add(top);
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(left, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - left, GridUnitType.Star) });
        g.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = Ui.B(left < 50 ? "#3FA27A" : left < 80 ? "#E0A23B" : "#C2412D") });
        sp.Children.Add(new Border { Height = 6, CornerRadius = new CornerRadius(3), Background = Ui.B("#EFEDE8"), Margin = new Thickness(0, 4, 0, 0), Child = g, ClipToBounds = true });
        var rt = new TextBlock { Text = reset, FontSize = 11, Foreground = Ui.B(onClick != null ? "#C2593A" : "#A8A7A2"), Margin = new Thickness(0, 3, 0, 0) };
        if (onClick != null)
        {
            rt.Cursor = System.Windows.Input.Cursors.Hand;
            rt.ToolTip = "点击修改每周重置时间";
            rt.MouseLeftButtonUp += (s, e) => onClick();
        }
        sp.Children.Add(rt);
        return sp;
    }

    Button Btn(string text, string style, Action a)
    {
        var b = new Button { Content = text, Style = Ui.S(style), Margin = new Thickness(6, 0, 0, 0) };
        b.Click += (s, e) => a();
        return b;
    }

    bool ValidName(string n)
    {
        if (string.IsNullOrEmpty(n)) return false;
        if (n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { Ui.Ask(w, "名称无效", "名称里不能有 \\ / : * ? \" < > | 等字符。", false, null, "知道了", false); return false; }
        if (Core.List().Any(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase))) { Ui.Ask(w, "名称重复", "已经有叫「" + n + "」的账号了。", false, null, "知道了", false); return false; }
        return true;
    }

    void AddAccount()
    {
        var n = Ui.Ask(w, "添加账号", "会先保存当前账号的登录状态，然后关闭并重新打开 Claude 到登录页，你在那里登录新账号即可。不要使用 Claude 里的「退出登录」。", true, "", "继续", false);
        if (!ValidName(n)) return;
        Run("正在准备登录新账号…", () => Core.Add(n), "已添加「" + n + "」，请在 Claude 中登录");
    }

    void RenameAccount(string old)
    {
        var n = Ui.Ask(w, "重命名", "给「" + old + "」起个新名字。", true, old, "保存", false);
        if (n == old || !ValidName(n)) return;
        Core.Rename(old, n);
        Log("「" + old + "」已改名为「" + n + "」");
        Refresh();
    }

    void DeleteAccount(string n)
    {
        if (Ui.Ask(w, "删除账号", "将删除本工具保存的「" + n + "」登录状态，会话记录不受影响。之后要用这个账号需重新添加并登录。", false, null, "删除", true) == null) return;
        Core.Delete(n);
        Log("已删除「" + n + "」");
        Refresh();
    }

    void BuildTrayMenu(List<Prof> profs, string cur)
    {
        var m = new WF.ContextMenuStrip();
        m.Items.Add("打开主界面", null, (s, e) => ShowWin());
        m.Items.Add(new WF.ToolStripSeparator());
        foreach (var p in profs)
        {
            var name = p.Name;
            var it = new WF.ToolStripMenuItem("切换到 " + name) { Checked = name == cur, Enabled = name != cur && p.Saved.HasValue };
            it.Click += (s, e) => Run("正在切换到「" + name + "」…", () => Core.Switch(name), "已切换到「" + name + "」");
            m.Items.Add(it);
        }
        m.Items.Add(new WF.ToolStripSeparator());
        m.Items.Add("立即同步", null, (s, e) => Run("正在同步会话…", () => Log("手动同步完成，更新 " + Core.Sync() + " 个文件")));
        m.Items.Add("退出", null, (s, e) => Exit());
        var old = tray.ContextMenuStrip;
        tray.ContextMenuStrip = m;
        if (old != null) old.Dispose();
    }
}

static class Program
{
    [DllImport("kernel32.dll")] static extern bool IsProcessInJob(IntPtr process, IntPtr job, out bool result);

    [STAThread]
    static void Main(string[] args)
    {
        // 从 Claude 里启动时会继承它的作业对象，Claude 被关闭时本程序也会被连带结束；
        // 通过 WMI 重新创建一个独立进程（--detached 防止循环）
        if (args.Contains("--statusline")) { Core.StatusLine(); return; }
        bool inJob;
        if (!args.Contains("--detached") && IsProcessInJob(Process.GetCurrentProcess().Handle, IntPtr.Zero, out inJob) && inJob)
        {
            var cmd = "\"" + Assembly.GetExecutingAssembly().Location + "\" --detached" + (args.Contains("--tray") ? " --tray" : "");
            var r = new System.Management.ManagementClass("Win32_Process").InvokeMethod("Create", new object[] { cmd });
            if (Convert.ToInt32(r) == 0) return;
        }
        bool created;
        var mtx = new Mutex(true, "ClaudeAccountManager_Single", out created);
        if (!created) return;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources = Ui.Load<ResourceDictionary>("theme.xaml");
        new MainWin(args.Contains("--tray"));
        app.Run();
        GC.KeepAlive(mtx);
    }
}
