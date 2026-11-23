// Claude usage widget for the Windows taskbar. Build: build.cmd
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Automation;
using System.Windows.Forms;
using Microsoft.Win32;

class Widget : Form
{
    const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    static readonly string[] TokenUrls = { "https://platform.claude.com/v1/oauth/token", "https://console.anthropic.com/v1/oauth/token" };
    const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    const int RefreshMs = 5 * 60000; // the usage endpoint answers 429 if polled too often

    static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static readonly string CredPath = Path.Combine(Home, ".claude", ".credentials.json");
    static readonly string LockPath = Path.Combine(Home, ".claude.lock");
    static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

    double p5 = -1, pW = -1;
    DateTime? r5, rW;
    DateTime updated, lastTry;
    string error;
    float scale;
    Rectangle lastRect;
    bool lastLight, dirty = true, busy;
    readonly ToolTip tip = new ToolTip { InitialDelay = 300, AutoPopDelay = 30000 };
    readonly ContextMenuStrip menu = new ContextMenuStrip();

    Widget()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;

        coin = Math.Max(0, Array.IndexOf(Coins, Setting("Coin", "BTC")));
        var coinMenu = new ToolStripMenuItem("Coin");
        for (int i = 0; i < Coins.Length; i++)
        {
            int k = i;
            coinMenu.DropDownItems.Add(Coins[i] + "/USDT", null, (s, e) => SetCoin(k));
        }
        coinMenu.DropDownOpening += (s, e) =>
        {
            for (int i = 0; i < Coins.Length; i++) ((ToolStripMenuItem)coinMenu.DropDownItems[i]).Checked = i == coin;
        };
        menu.Items.Add(coinMenu);

        var locMenu = new ToolStripMenuItem("Location");
        foreach (var c in Cities)
        {
            var cc = c;
            locMenu.DropDownItems.Add((string)c[0], null, (s, e) => SetLocation((string)cc[0], (double)cc[1], (double)cc[2]));
        }
        locMenu.DropDownItems.Add(new ToolStripSeparator());
        var other = new ToolStripMenuItem("Other city...", null, (s, e) => AskCity());
        locMenu.DropDownItems.Add(other);
        locMenu.DropDownOpening += (s, e) =>
        {
            bool preset = false;
            for (int i = 0; i < Cities.Length; i++)
            {
                bool on = (string)Cities[i][0] == city;
                ((ToolStripMenuItem)locMenu.DropDownItems[i]).Checked = on;
                preset |= on;
            }
            other.Checked = !preset;
            other.Text = preset ? "Other city..." : "Other city... (" + city + ")";
        };
        menu.Items.Add(locMenu);

        var showMenu = new ToolStripMenuItem("Show");
        foreach (var name in new[] { "Claude usage", "Crypto", "Weather", "System" })
        {
            var n = name;
            var item = new ToolStripMenuItem(n, null, (s, e) => ToggleBlock(n));
            showMenu.DropDownItems.Add(item);
        }
        showMenu.DropDownOpening += (s, e) =>
        {
            ((ToolStripMenuItem)showMenu.DropDownItems[0]).Checked = showClaude;
            ((ToolStripMenuItem)showMenu.DropDownItems[1]).Checked = showCrypto;
            ((ToolStripMenuItem)showMenu.DropDownItems[2]).Checked = showWeather;
            ((ToolStripMenuItem)showMenu.DropDownItems[3]).Checked = showSystem;
        };
        menu.Items.Add(showMenu);
        var compactItem = new ToolStripMenuItem("Compact (double-click)", null, (s, e) => ToggleCompact());
        menu.Opening += (s, e) => compactItem.Checked = compact;
        menu.Items.Add(compactItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh", null, (s, e) => { Fetch(); FetchPrice(); FetchWeather(); });
        menu.Items.Add("Exit", null, (s, e) => Application.Exit());
        MouseUp += (s, e) =>
        {
            if (e.Button == MouseButtons.Right) { Log("menu opened"); SetForegroundWindow(Handle); menu.Show(Cursor.Position); }
            else if (e.Button == MouseButtons.Left) { Fetch(); FetchPrice(); }
        };
        MouseDoubleClick += (s, e) => { if (e.Button == MouseButtons.Left) ToggleCompact(); };
        var pricePoll = new System.Windows.Forms.Timer { Interval = PriceMs };
        pricePoll.Tick += (s, e) => FetchPrice();
        pricePoll.Start();

        var tick = new System.Windows.Forms.Timer { Interval = 500 };
        tick.Tick += (s, e) => Tick();
        tick.Start();
        var poll = new System.Windows.Forms.Timer { Interval = RefreshMs };
        poll.Tick += (s, e) => Fetch();
        poll.Start();
        scanTimer = new System.Threading.Timer(ScanButtons);
        scanTimer.Change(0, Timeout.Infinite);
        LoadUsage();
        Log("start pid=" + Process.GetCurrentProcess().Id + " exe=" + Application.ExecutablePath + " scale=" + scale +
            " coin=" + Coins[coin] + " city=" + city + " compact=" + compact +
            " show=" + showClaude + "/" + showCrypto + "/" + showWeather + "/" + showSystem);
        Load += (s, e) =>
        {
            Tick();
            if (DateTime.Now - updated > TimeSpan.FromMilliseconds(RefreshMs)) Fetch();
            FetchPrice();
            FetchWeather();
            StartSystemSampler();
        };
        var weatherPoll = new System.Windows.Forms.Timer { Interval = WeatherMs };
        weatherPoll.Tick += (s, e) => FetchWeather();
        weatherPoll.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x80000 | 0x80 | 0x8 | 0x08000000; // LAYERED | TOOLWINDOW | TOPMOST | NOACTIVATE
            return cp;
        }
    }
    protected override bool ShowWithoutActivation { get { return true; } }

    // ---------- data ----------

    void Fetch()
    {
        if (busy || DateTime.Now - lastTry < TimeSpan.FromSeconds(30)) return;
        busy = true; lastTry = DateTime.Now;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            double a = -1, b = -1; DateTime? ra = null, rb = null; string err = null;
            try
            {
                var o = ReadCreds();
                string tok = (string)o["accessToken"];
                if (Convert.ToInt64(o["expiresAt"]) - NowMs() < 300000) tok = RefreshToken(tok);
                string body;
                try { body = Http(UsageUrl, tok, null); }
                catch (WebException ex)
                {
                    var r = ex.Response as HttpWebResponse;
                    if (r == null || (int)r.StatusCode != 401) throw;
                    body = Http(UsageUrl, RefreshToken(tok), null);
                }
                var d = (Dictionary<string, object>)Json.DeserializeObject(body);
                Parse(d, "five_hour", out a, out ra);
                Parse(d, "seven_day", out b, out rb);
            }
            catch (Exception ex) { err = Describe(ex); }
            BeginInvoke((Action)(() =>
            {
                error = err;
                if (err == null) { p5 = a; pW = b; r5 = ra; rW = rb; updated = DateTime.Now; SaveUsage(); }
                busy = false; dirty = true; Tick();
            }));
        });
    }

    // ---------- crypto price (Binance public API, no key needed) ----------

    static readonly string[] Coins = { "BTC", "ETH", "SOL", "BNB", "XRP", "DOGE", "TON" };
    static readonly int[] CoinColors = { 0xF7931A, 0x627EEA, 0x1C1C28, 0xF3BA2F, 0x2B3138, 0xC2A633, 0x0098EA };
    static readonly string[] PriceHosts = { "https://api.binance.com", "https://data-api.binance.vision" };
    const string SettingsKey = @"HKEY_CURRENT_USER\Software\ClaudeUsageWidget";
    const int PriceMs = 30000;
    static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    int coin;
    double price = -1, change, high, low;
    string priceError;
    bool priceBusy;

    void SetCoin(int k)
    {
        coin = k;
        SetSetting("Coin", Coins[k]);
        price = -1; priceError = null; dirty = true;
        FetchPrice();
    }

    void FetchPrice()
    {
        if (priceBusy) return;
        priceBusy = true;
        int c = coin;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            double p = -1, ch = 0, hi = 0, lo = 0; string err = null;
            foreach (var host in PriceHosts)
            {
                try
                {
                    var d = (Dictionary<string, object>)Json.DeserializeObject(
                        Http(host + "/api/v3/ticker/24hr?symbol=" + Coins[c] + "USDT", null, null));
                    p = Num(d["lastPrice"]); ch = Num(d["priceChangePercent"]); hi = Num(d["highPrice"]); lo = Num(d["lowPrice"]);
                    err = null;
                    break;
                }
                catch (Exception ex) { err = ex.Message; }
            }
            BeginInvoke((Action)(() =>
            {
                priceBusy = false;
                if (c != coin) { FetchPrice(); return; } // coin was switched meanwhile
                priceError = err;
                if (err == null) { price = p; change = ch; high = hi; low = lo; }
                dirty = true; Tick();
            }));
        });
    }

    static double Num(object o) { return double.Parse((string)o, Inv); }

    static string CompactPrice(double p)
    {
        if (p < 0) return "--";
        if (p >= 10000) return (p / 1000).ToString(p >= 100000 ? "0" : "0.0", Inv) + "k";
        if (p >= 100) return p.ToString("0", Inv);
        if (p >= 1) return p.ToString("0.00", Inv);
        return p.ToString("0.0000", Inv);
    }

    static string FullPrice(double p) { return p.ToString(p >= 1 ? "#,0.00" : "0.000000", Inv); }

    string ChangeText() { return (change >= 0 ? "▲" : "▼") + Math.Abs(change).ToString("0.0", Inv) + "%"; }

    // ---------- weather (Open-Meteo, no key needed) ----------

    const int WeatherMs = 10 * 60000;
    static readonly object[][] Cities = {
        new object[] { "Khmelnytskyi", 49.4229, 26.9871 }, new object[] { "Kyiv", 50.4501, 30.5234 },
        new object[] { "Lviv", 49.8397, 24.0297 }, new object[] { "Odesa", 46.4825, 30.7233 },
        new object[] { "Kharkiv", 49.9935, 36.2304 }, new object[] { "Dnipro", 48.4647, 35.0462 },
        new object[] { "Vinnytsia", 49.2331, 28.4682 }, new object[] { "Ternopil", 49.5535, 25.5948 } };

    string city = Setting("City", "Khmelnytskyi");
    double lat = double.Parse(Setting("Lat", "49.4229"), Inv), lon = double.Parse(Setting("Lon", "26.9871"), Inv);

    string WeatherUrl()
    {
        return "https://api.open-meteo.com/v1/forecast?latitude=" + lat.ToString(Inv) + "&longitude=" + lon.ToString(Inv) +
               "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,is_day&wind_speed_unit=ms&timezone=auto";
    }

    void SetLocation(string name, double la, double lo)
    {
        city = name; lat = la; lon = lo;
        SetSetting("City", name); SetSetting("Lat", la.ToString(Inv)); SetSetting("Lon", lo.ToString(Inv));
        temp = double.NaN; weatherError = null; dirty = true;
        FetchWeather();
    }

    // "Other city..." dialog: looks the name up with the Open-Meteo geocoder and takes the best match.
    void AskCity()
    {
        int u = (int)(10 * scale);
        using (var f = new Form { Text = "Weather location", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen,
                                  TopMost = true, MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, ClientSize = new Size(32 * u, 8 * u) })
        {
            var tb = new TextBox { Left = u, Top = u, Width = 30 * u, Text = city.Split(',')[0] };
            var ok = new Button { Text = "Find", Left = 22 * u, Top = 4 * u + u / 2, Width = 9 * u, Height = 3 * u, DialogResult = DialogResult.OK };
            f.Controls.Add(tb); f.Controls.Add(ok); f.AcceptButton = ok;
            if (f.ShowDialog() != DialogResult.OK || tb.Text.Trim() == "") return;
            try
            {
                object res = null;
                foreach (var lang in new[] { "en", "uk" }) // "uk" finds names typed in Cyrillic
                {
                    var d = (Dictionary<string, object>)Json.DeserializeObject(Http("https://geocoding-api.open-meteo.com/v1/search?count=1&language=" +
                        lang + "&name=" + Uri.EscapeDataString(tb.Text.Trim()), null, null));
                    if (d.TryGetValue("results", out res) && res is object[] && ((object[])res).Length > 0) break;
                    res = null;
                }
                if (res == null)
                {
                    MessageBox.Show("City not found: " + tb.Text, "Weather location");
                    return;
                }
                var r0 = (Dictionary<string, object>)((object[])res)[0];
                string name = (string)r0["name"] + (r0.ContainsKey("country") ? ", " + r0["country"] : "");
                SetLocation(name, Convert.ToDouble(r0["latitude"]), Convert.ToDouble(r0["longitude"]));
            }
            catch (Exception ex) { MessageBox.Show("Search failed: " + ex.Message, "Weather location"); }
        }
    }

    double temp = double.NaN, feels, humidity, wind;
    int wcode, isDay = 1;
    string weatherError;
    bool weatherBusy;
    DateTime weatherUpdated;

    void FetchWeather()
    {
        if (weatherBusy) return;
        weatherBusy = true;
        string url = WeatherUrl();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            double t = double.NaN, f = 0, h = 0, w = 0; int c = 0, day = 1; string err = null;
            try
            {
                var d = (Dictionary<string, object>)Json.DeserializeObject(Http(url, null, null));
                var cur = (Dictionary<string, object>)d["current"];
                t = Convert.ToDouble(cur["temperature_2m"]);
                f = Convert.ToDouble(cur["apparent_temperature"]);
                h = Convert.ToDouble(cur["relative_humidity_2m"]);
                w = Convert.ToDouble(cur["wind_speed_10m"]);
                c = Convert.ToInt32(cur["weather_code"]);
                day = Convert.ToInt32(cur["is_day"]);
            }
            catch (Exception ex) { err = ex.Message; }
            BeginInvoke((Action)(() =>
            {
                weatherBusy = false;
                if (url != WeatherUrl()) { FetchWeather(); return; } // location was changed meanwhile
                weatherError = err;
                if (err == null) { temp = t; feels = f; humidity = h; wind = w; wcode = c; isDay = day; weatherUpdated = DateTime.Now; }
                dirty = true; Tick();
            }));
        });
    }

    // WMO weather codes -> name
    static string WeatherName(int c)
    {
        if (c == 0) return "Clear";
        if (c <= 2) return "Partly cloudy";
        if (c == 3) return "Overcast";
        if (c == 45 || c == 48) return "Fog";
        if (c >= 51 && c <= 57) return "Drizzle";
        if ((c >= 61 && c <= 67) || (c >= 80 && c <= 82)) return "Rain";
        if ((c >= 71 && c <= 77) || c == 85 || c == 86) return "Snow";
        if (c >= 95) return "Thunderstorm";
        return "Code " + c;
    }

    static string Deg(double t) { return Math.Round(t).ToString("0", Inv) + "°"; }

    string WeatherTooltip()
    {
        if (double.IsNaN(temp)) return city + ": " + (weatherError != null ? "error: " + weatherError : "loading...");
        return city + ": " + Deg(temp) + ", " + WeatherName(wcode) +
               "\nFeels like " + Deg(feels) + "  -  humidity " + Math.Round(humidity) + "%  -  wind " + wind.ToString("0.0", Inv) + " m/s" +
               "\nUpdated " + weatherUpdated.ToString("HH:mm") +
               (weatherError != null ? "\nLast update failed: " + weatherError : "");
    }

    // Minimal weather icon in a unit box (coordinates -1..1, scaled by r).
    void DrawWeather(Graphics g, float cx, float cy, float r, bool light)
    {
        Func<float, float, PointF> P = (x, y) => new PointF(cx + x * r, cy + y * r);
        Action<Brush, float, float, float> circle = (b, x, y, s) => g.FillEllipse(b, cx + (x - s) * r, cy + (y - s) * r, 2 * s * r, 2 * s * r);
        Color sunC = Color.FromArgb(255, 193, 7), moonC = Color.FromArgb(235, 215, 140);
        Color cloudC = light ? Color.FromArgb(135, 145, 158) : Color.FromArgb(214, 220, 228);

        Action<float, float, float> sunOrMoon = (x, y, s) =>
        {
            if (isDay == 1)
            {
                using (var b = new SolidBrush(sunC)) circle(b, x, y, s);
                using (var pen = new Pen(sunC, 0.13f * r) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    for (int i = 0; i < 8; i++)
                    {
                        float c = (float)Math.Cos(i * Math.PI / 4), sn = (float)Math.Sin(i * Math.PI / 4);
                        g.DrawLine(pen, P(x + c * s * 1.5f, y + sn * s * 1.5f), P(x + c * s * 2f, y + sn * s * 2f));
                    }
            }
            else
            {
                var a = new GraphicsPath(); a.AddEllipse(cx + (x - s * 1.4f) * r, cy + (y - s * 1.4f) * r, 2.8f * s * r, 2.8f * s * r);
                var cut = new GraphicsPath(); cut.AddEllipse(cx + (x - s * 0.7f) * r, cy + (y - s * 1.9f) * r, 2.8f * s * r, 2.8f * s * r);
                using (var reg = new Region(a)) using (var b = new SolidBrush(moonC)) { reg.Exclude(cut); g.FillRegion(b, reg); }
                a.Dispose(); cut.Dispose();
            }
        };
        Action<float> cloud = y =>
        {
            using (var b = new SolidBrush(cloudC))
            {
                circle(b, -0.4f, y + 0.1f, 0.32f);
                circle(b, 0.02f, y - 0.1f, 0.42f);
                circle(b, 0.42f, y + 0.12f, 0.3f);
                g.FillRectangle(b, cx - 0.4f * r, cy + (y + 0.1f) * r, 0.82f * r, 0.32f * r);
            }
        };

        switch (WeatherName(wcode))
        {
            case "Clear": sunOrMoon(0, 0, 0.42f); break;
            case "Partly cloudy": sunOrMoon(0.32f, -0.32f, 0.3f); cloud(0.25f); break;
            case "Overcast": cloud(0.05f); break;
            case "Fog":
                cloud(-0.2f);
                using (var pen = new Pen(cloudC, 0.12f * r) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(pen, P(-0.6f, 0.45f), P(0.5f, 0.45f));
                    g.DrawLine(pen, P(-0.4f, 0.7f), P(0.6f, 0.7f));
                }
                break;
            case "Drizzle":
            case "Rain":
                cloud(-0.25f);
                using (var pen = new Pen(Color.FromArgb(77, 166, 255), 0.13f * r) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    for (int i = -1; i <= 1; i++) g.DrawLine(pen, P(i * 0.35f + 0.08f, 0.38f), P(i * 0.35f - 0.06f, 0.72f));
                break;
            case "Snow":
                cloud(-0.25f);
                using (var b = new SolidBrush(light ? Color.FromArgb(90, 150, 220) : Color.White))
                    for (int i = -1; i <= 1; i++) circle(b, i * 0.35f, i == 0 ? 0.7f : 0.5f, 0.09f);
                break;
            case "Thunderstorm":
                cloud(-0.3f);
                using (var b = new SolidBrush(sunC))
                    g.FillPolygon(b, new[] { P(0.08f, 0.2f), P(-0.16f, 0.58f), P(0.0f, 0.58f), P(-0.1f, 0.9f), P(0.22f, 0.46f), P(0.05f, 0.46f), P(0.16f, 0.2f) });
                break;
            default: cloud(0.05f); break;
        }
    }

    // ---------- system load (local performance counters, no admin needed) ----------

    volatile float cpu = -1, ram = -1, gpu = -1;
    double ramUsedGb, ramTotalGb;
    static readonly string CpuName = ((Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", "") as string) ?? "").Trim();

    void StartSystemSampler()
    {
        var th = new Thread(() =>
        {
            PerformanceCounter cpuCounter = null;
            Dictionary<string, CounterSample> gpuPrev = null;
            while (true)
            {
                try
                {
                    if (cpuCounter == null) cpuCounter = new PerformanceCounter("Processor Information", "% Processor Utility", "_Total", true);
                    float c = Math.Min(100, cpuCounter.NextValue()); // "% Processor Utility" matches Task Manager
                    var ms = new MEMSTAT();
                    GlobalMemoryStatusEx(ms);
                    float g = ReadGpu(ref gpuPrev);
                    BeginInvoke((Action)(() =>
                    {
                        cpu = c; gpu = g; ram = ms.dwMemoryLoad;
                        ramTotalGb = ms.ullTotalPhys / 1073741824.0;
                        ramUsedGb = (ms.ullTotalPhys - ms.ullAvailPhys) / 1073741824.0;
                        if (showSystem) dirty = true;
                    }));
                }
                catch { }
                Thread.Sleep(2000);
            }
        }) { IsBackground = true };
        th.Start();
    }

    // Like Task Manager: sum the engines of each type per adapter, then take the busiest type.
    // One ReadCategory() per sample instead of hundreds of separate counters (one per process engine).
    static float ReadGpu(ref Dictionary<string, CounterSample> prev)
    {
        InstanceDataCollection data;
        try { data = new PerformanceCounterCategory("GPU Engine").ReadCategory()["Utilization Percentage"]; } catch { return -1; }
        if (data == null) return -1;

        var now = new Dictionary<string, CounterSample>();
        var sums = new Dictionary<string, float>();
        foreach (InstanceData inst in data.Values)
        {
            string n = inst.InstanceName;
            now[n] = inst.Sample;
            CounterSample old;
            if (prev == null || !prev.TryGetValue(n, out old)) continue;
            float v = CounterSample.Calculate(old, inst.Sample);
            int l = n.IndexOf("luid_"), e = n.IndexOf("_eng_"), t = n.IndexOf("engtype_");
            string key = (l >= 0 && e > l ? n.Substring(l, e - l) : "") + (t >= 0 ? n.Substring(t) : "");
            float cur;
            sums.TryGetValue(key, out cur);
            sums[key] = cur + v;
        }
        bool first = prev == null;
        prev = now;
        if (first) return -1;
        float max = 0;
        foreach (var v in sums.Values) max = Math.Max(max, v);
        return Math.Min(100, max);
    }

    string SystemTooltip()
    {
        if (cpu < 0) return "System: loading...";
        return "CPU: " + Math.Round(cpu) + "%" + (CpuName != "" ? "  (" + CpuName + ")" : "") +
               "\nRAM: " + ramUsedGb.ToString("0.0", Inv) + " / " + ramTotalGb.ToString("0.0", Inv) + " GB  (" + Math.Round(ram) + "%)" +
               "\nGPU: " + (gpu < 0 ? "n/a" : Math.Round(gpu) + "%");
    }

    // Minimal chip icon: outlined square, filled core, two pins per side.
    static void DrawChip(Graphics g, float cx, float cy, float r, Color c)
    {
        using (var pen = new Pen(c, 0.14f * r) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var b = new SolidBrush(c))
        {
            g.DrawRectangle(pen, cx - 0.55f * r, cy - 0.55f * r, 1.1f * r, 1.1f * r);
            g.FillRectangle(b, cx - 0.25f * r, cy - 0.25f * r, 0.5f * r, 0.5f * r);
            foreach (float o in new[] { -0.25f, 0.25f })
            {
                g.DrawLine(pen, cx + o * r, cy - 0.62f * r, cx + o * r, cy - 0.9f * r);
                g.DrawLine(pen, cx + o * r, cy + 0.62f * r, cx + o * r, cy + 0.9f * r);
                g.DrawLine(pen, cx - 0.62f * r, cy + o * r, cx - 0.9f * r, cy + o * r);
                g.DrawLine(pen, cx + 0.62f * r, cy + o * r, cx + 0.9f * r, cy + o * r);
            }
        }
    }

    static string Describe(Exception ex)
    {
        var we = ex as WebException;
        var r = we != null ? we.Response as HttpWebResponse : null;
        int code = r != null ? (int)r.StatusCode : 0;
        if (code == 429) return "rate limited by Anthropic (429), will retry in 5 min";
        if (code == 401 || code == 403 || ex is FileNotFoundException || ex is DirectoryNotFoundException)
            return "not signed in to Claude Code (" + ex.Message + ")";
        return ex.Message;
    }

    // Last good usage numbers are kept in the registry, so a restart does not need a new request.
    void SaveUsage()
    {
        SetSetting("Usage", string.Join("|", new[] {
            p5.ToString(Inv), pW.ToString(Inv),
            r5.HasValue ? r5.Value.Ticks.ToString() : "0", rW.HasValue ? rW.Value.Ticks.ToString() : "0",
            updated.Ticks.ToString() }));
    }

    void LoadUsage()
    {
        try
        {
            var v = Setting("Usage", "").Split('|');
            if (v.Length != 5) return;
            p5 = double.Parse(v[0], Inv); pW = double.Parse(v[1], Inv);
            long t5 = long.Parse(v[2]), tW = long.Parse(v[3]);
            r5 = t5 > 0 ? new DateTime(t5) : (DateTime?)null;
            rW = tW > 0 ? new DateTime(tW) : (DateTime?)null;
            updated = new DateTime(long.Parse(v[4]));
            // a window that has already reset means its old number is stale
            if (r5.HasValue && r5.Value < DateTime.Now) p5 = 0;
            if (rW.HasValue && rW.Value < DateTime.Now) pW = 0;
        }
        catch { }
    }

    static void Parse(Dictionary<string, object> d, string key, out double pct, out DateTime? reset)
    {
        pct = -1; reset = null;
        object v;
        if (!d.TryGetValue(key, out v) || !(v is Dictionary<string, object>)) return;
        var o = (Dictionary<string, object>)v;
        if (o.TryGetValue("utilization", out v) && v != null) pct = Convert.ToDouble(v);
        if (o.TryGetValue("resets_at", out v) && v != null) reset = DateTime.Parse((string)v).ToLocalTime();
    }

    static Dictionary<string, object> ReadCreds() { return ReadCreds(File.ReadAllText(CredPath)); }
    static Dictionary<string, object> ReadCreds(string text)
    {
        var root = (Dictionary<string, object>)Json.DeserializeObject(text);
        return (Dictionary<string, object>)root["claudeAiOauth"];
    }

    // Refreshes the OAuth token the same way Claude Code does, under the same lock directory.
    static string RefreshToken(string failedToken)
    {
        AcquireLock();
        try
        {
            var root = (Dictionary<string, object>)Json.DeserializeObject(File.ReadAllText(CredPath));
            var o = (Dictionary<string, object>)root["claudeAiOauth"];
            // Claude Code may have refreshed it already.
            if ((string)o["accessToken"] != failedToken && Convert.ToInt64(o["expiresAt"]) - NowMs() > 300000)
                return (string)o["accessToken"];

            string req = Json.Serialize(new Dictionary<string, object> {
                { "grant_type", "refresh_token" }, { "refresh_token", o["refreshToken"] }, { "client_id", ClientId } });
            string resp = null; Exception last = null;
            foreach (var url in TokenUrls)
            {
                try { resp = Http(url, null, req); break; } catch (Exception ex) { last = ex; }
            }
            if (resp == null) throw new Exception("token refresh failed: " + last.Message);

            var t = (Dictionary<string, object>)Json.DeserializeObject(resp);
            o["accessToken"] = t["access_token"];
            if (t.ContainsKey("refresh_token")) o["refreshToken"] = t["refresh_token"];
            o["expiresAt"] = NowMs() + Convert.ToInt64(t["expires_in"]) * 1000;
            string tmp = CredPath + ".tmp";
            File.WriteAllText(tmp, Json.Serialize(root), new UTF8Encoding(false));
            File.Copy(tmp, CredPath, true);
            File.Delete(tmp);
            return (string)o["accessToken"];
        }
        finally { try { Directory.Delete(LockPath); } catch { } }
    }

    static void AcquireLock()
    {
        for (int i = 0; i < 100; i++)
        {
            if (CreateDirectory(LockPath, IntPtr.Zero)) return;
            try { if (DateTime.Now - Directory.GetLastWriteTime(LockPath) > TimeSpan.FromSeconds(30)) Directory.Delete(LockPath); } catch { }
            Thread.Sleep(200);
        }
        throw new Exception("credentials are locked");
    }

    static string Http(string url, string token, string body)
    {
        var req = (HttpWebRequest)WebRequest.Create(url);
        req.UserAgent = "claude-usage-widget";
        req.Timeout = 15000;
        if (token != null)
        {
            req.Headers["Authorization"] = "Bearer " + token;
            req.Headers["anthropic-beta"] = "oauth-2025-04-20";
        }
        if (body != null)
        {
            req.Method = "POST";
            req.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
        }
        using (var r = req.GetResponse())
        using (var sr = new StreamReader(r.GetResponseStream())) return sr.ReadToEnd();
    }

    static long NowMs() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }

    // ---------- drawing ----------

    void Tick()
    {
        if (IsFullscreen()) { if (Visible) Hide(); return; }
        if (!Visible) Show();

        RECT tb, tray;
        IntPtr task = FindWindow("Shell_TrayWnd", null);
        if (task == IntPtr.Zero || !GetWindowRect(task, out tb)) return;
        IntPtr notify = FindWindowEx(task, IntPtr.Zero, "TrayNotifyWnd", null);
        int trayLeft = notify != IntPtr.Zero && GetWindowRect(notify, out tray) ? tray.L : tb.R - (int)(300 * scale);

        bool light = IsLightTaskbar();
        int h = tb.B - tb.T;
        var size = Measure(h);
        var rect = new Rectangle(FindSpot(tb.L, trayLeft, size.Width), tb.T, size.Width, h);
        if (dirty || rect != lastRect || light != lastLight)
        {
            Render(rect, light);
            lastRect = rect; lastLight = light; dirty = false;
            tip.SetToolTip(this, TooltipText());
        }
        SetWindowPos(Handle, (IntPtr)(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10); // TOPMOST, NOSIZE|NOMOVE|NOACTIVATE
    }

    // ---------- placement: find a free gap between taskbar buttons ----------

    volatile int[] buttons; // [left0, right0, left1, right1, ...] of taskbar buttons, from UI Automation
    System.Threading.Timer scanTimer;

    void ScanButtons(object _)
    {
        try
        {
            var root = AutomationElement.FromHandle(FindWindow("Shell_TrayWnd", null));
            var cond = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
            var list = new List<int>();
            foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, cond))
            {
                var r = e.Current.BoundingRectangle;
                if (r.IsEmpty || r.Width <= 0 || e.Current.ClassName.StartsWith("SystemTray")) continue;
                list.Add((int)r.Left); list.Add((int)r.Right);
            }
            buttons = list.ToArray();
        }
        catch { }
        scanTimer.Change(1000, Timeout.Infinite);
    }

    // Next to the tray if it fits there, otherwise the leftmost free gap that fits.
    int FindSpot(int barLeft, int trayLeft, int w)
    {
        int m = (int)(6 * scale), fallback = trayLeft - w - m;
        var b = buttons;
        if (b == null) return fallback;

        var spans = new List<KeyValuePair<int, int>>();
        for (int i = 0; i < b.Length; i += 2)
            if (b[i + 1] <= trayLeft + 1) spans.Add(new KeyValuePair<int, int>(b[i], b[i + 1]));
        spans.Sort((p, q) => p.Key.CompareTo(q.Key));

        var gaps = new List<KeyValuePair<int, int>>();
        int cur = barLeft;
        foreach (var s in spans)
        {
            if (s.Key > cur) gaps.Add(new KeyValuePair<int, int>(cur, s.Key));
            cur = Math.Max(cur, s.Value);
        }
        gaps.Add(new KeyValuePair<int, int>(cur, trayLeft));

        var last = gaps[gaps.Count - 1];
        if (last.Value - last.Key >= w + 2 * m) return last.Value - w - m;
        foreach (var g in gaps)
            if (g.Value - g.Key >= w + 2 * m) return g.Key + m;
        return fallback;
    }

    Font Regular() { return new Font("Segoe UI", 12 * scale, FontStyle.Regular, GraphicsUnit.Pixel); }
    Font Bold() { return new Font("Segoe UI", 12 * scale, FontStyle.Bold, GraphicsUnit.Pixel); }

    string Pct(double p) { return p < 0 ? "--" : Math.Round(p) + "%"; }

    // Layout: [logo] 5h NN%  |  W NN%
    Size Measure(int h)
    {
        using (var bmp = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(bmp))
        {
            float w = Draw(g, null, h, false);
            return new Size((int)Math.Ceiling(w), h);
        }
    }

    void Render(Rectangle r, bool light)
    {
        using (var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(1, 0, 0, 0)); // almost invisible, but catches the mouse
                Draw(g, g, r.Height, light);
            }
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen);
            IntPtr hbmp = bmp.GetHbitmap(Color.FromArgb(0)), old = SelectObject(mem, hbmp);
            var sz = new SIZE { cx = r.Width, cy = r.Height };
            var src = new POINT();
            var dst = new POINT { x = r.X, y = r.Y };
            var blend = new BLEND { Op = 0, Flags = 0, Alpha = 255, Format = 1 };
            UpdateLayeredWindow(Handle, screen, ref dst, ref sz, mem, ref src, 0, ref blend, 2);
            SelectObject(mem, old); DeleteObject(hbmp); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // ---------- view settings ----------

    bool showClaude = Setting("ShowClaude", "1") == "1";
    bool showCrypto = Setting("ShowCrypto", "1") == "1";
    bool showWeather = Setting("ShowWeather", "1") == "1";
    bool showSystem = Setting("ShowSystem", "1") == "1";
    bool compact = Setting("Compact", "0") == "1";

    // Settings live in settings.ini next to the exe, not in the registry: apps launched from a packaged
    // (MSIX) app get a private copy of HKCU, so registry settings could silently differ between launches.
    static readonly string SettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini");
    static Dictionary<string, string> settings;

    static Dictionary<string, string> Settings()
    {
        if (settings != null) return settings;
        settings = new Dictionary<string, string>();
        if (File.Exists(SettingsPath))
        {
            foreach (var line in File.ReadAllLines(SettingsPath))
            {
                int i = line.IndexOf('=');
                if (i > 0) settings[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
            }
        }
        else
        {
            // one-time migration from the old registry settings
            foreach (var k in new[] { "Coin", "City", "Lat", "Lon", "Compact", "ShowClaude", "ShowCrypto", "ShowWeather", "ShowSystem", "Usage" })
            {
                var v = Registry.GetValue(SettingsKey, k, null) as string;
                if (v != null) settings[k] = v;
            }
            SaveSettings();
            Log("created settings.ini from registry: " + settings.Count + " values");
        }
        return settings;
    }

    static void SaveSettings()
    {
        var lines = new List<string>();
        foreach (var kv in settings) lines.Add(kv.Key + "=" + kv.Value);
        string tmp = SettingsPath + ".tmp";
        File.WriteAllLines(tmp, lines.ToArray());
        File.Copy(tmp, SettingsPath, true);
        File.Delete(tmp);
    }

    static string Setting(string name, string def)
    {
        string v;
        return Settings().TryGetValue(name, out v) ? v : def;
    }

    static void SetSetting(string name, string value)
    {
        Settings()[name] = value;
        try { SaveSettings(); if (name != "Usage") Log("saved " + name + "=" + value); }
        catch (Exception ex) { Log("ERROR saving settings: " + ex.Message); }
    }

    // Small log next to the exe, to see what the widget read and saved.
    static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "widget.log");
    static void Log(string s)
    {
        try
        {
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 200000) File.Delete(LogPath);
            File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + s + Environment.NewLine);
        }
        catch { }
    }

    void ToggleBlock(string name)
    {
        bool c = showClaude, k = showCrypto, w = showWeather, s = showSystem;
        if (name == "Claude usage") c = !c; else if (name == "Crypto") k = !k; else if (name == "Weather") w = !w; else s = !s;
        if (!c && !k && !w && !s) return; // keep at least one block
        showClaude = c; showCrypto = k; showWeather = w; showSystem = s;
        SetSetting("ShowClaude", c ? "1" : "0"); SetSetting("ShowCrypto", k ? "1" : "0");
        SetSetting("ShowWeather", w ? "1" : "0"); SetSetting("ShowSystem", s ? "1" : "0");
        dirty = true; Tick();
    }

    void ToggleCompact()
    {
        compact = !compact;
        SetSetting("Compact", compact ? "1" : "0");
        dirty = true; Tick();
    }

    // Measures (draw == null) or draws the content; returns total width.
    // Full:    [logo] 5h 9% | W 4% | [coin] BTC 73.6k ▼0.6% | [weather] KHM 3°
    //          ... | [chip] CPU 23%  RAM 61%  GPU 8%
    // Compact: [logo] 9%/4%   [coin] 73.6k ▼0.6%   [weather] 3°   [chip] 23%/61%/8%

    // First 3 letters of the city name: Khmelnytskyi -> KHM
    string CityCode()
    {
        string n = city.Split(',')[0].Trim();
        return (n.Length > 3 ? n.Substring(0, 3) : n).ToUpperInvariant();
    }
    float Draw(Graphics m, Graphics draw, int h, bool light)
    {
        Color fg = light ? Color.FromArgb(25, 25, 25) : Color.White;
        Color dim = light ? Color.FromArgb(95, 95, 95) : Color.FromArgb(175, 175, 175);
        var fmt = StringFormat.GenericTypographic;
        float x = 8 * scale, cy = h / 2f, gap = 10 * scale;
        bool first = true;
        using (Font reg = Regular(), bold = Bold())
        using (var sep = new Pen(Color.FromArgb(light ? 60 : 80, dim), Math.Max(1, scale)))
        {
            if (draw != null)
            {
                draw.SmoothingMode = SmoothingMode.AntiAlias;
                draw.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            }
            Action separator = () =>
            {
                x += gap;
                if (!compact && draw != null) draw.DrawLine(sep, x, cy - 8 * scale, x, cy + 8 * scale);
                x += gap;
            };

            if (showClaude)
            {
                first = false;
                if (draw != null) DrawLogo(draw, x + 7 * scale, cy, 7 * scale);
                x += 14 * scale + 6 * scale;
                if (compact)
                {
                    x = Txt(m, draw, Pct(p5), bold, Level(p5, fg, light), x, cy, fmt);
                    x = Txt(m, draw, "/", reg, dim, x + 2 * scale, cy, fmt) + 2 * scale;
                    x = Txt(m, draw, Pct(pW), reg, Level(pW, fg, light), x, cy, fmt);
                }
                else
                {
                    x = Txt(m, draw, "5h", reg, dim, x, cy, fmt) + 5 * scale;
                    x = Txt(m, draw, Pct(p5), bold, Level(p5, fg, light), x, cy, fmt);
                    separator();
                    x = Txt(m, draw, "W", reg, dim, x, cy, fmt) + 5 * scale;
                    x = Txt(m, draw, Pct(pW), bold, Level(pW, fg, light), x, cy, fmt);
                }
            }

            if (showCrypto)
            {
                if (!first) separator();
                first = false;
                if (draw != null) DrawCoin(draw, x + 7 * scale, cy, 7 * scale);
                x += 14 * scale + 6 * scale;
                if (!compact) x = Txt(m, draw, Coins[coin], reg, dim, x, cy, fmt) + 5 * scale;
                x = Txt(m, draw, CompactPrice(price), bold, fg, x, cy, fmt);
                if (price >= 0)
                {
                    Color up = light ? Color.FromArgb(20, 140, 60) : Color.FromArgb(80, 200, 120);
                    Color down = light ? Color.FromArgb(205, 40, 40) : Color.FromArgb(255, 95, 95);
                    x = Txt(m, draw, ChangeText(), reg, change >= 0 ? up : down, x + 5 * scale, cy, fmt);
                }
            }

            if (showWeather)
            {
                if (!first) separator();
                if (draw != null) DrawWeather(draw, x + 9 * scale, cy, 9 * scale, light);
                x += 18 * scale + 5 * scale;
                if (!compact) x = Txt(m, draw, CityCode(), reg, dim, x, cy, fmt) + 5 * scale;
                x = Txt(m, draw, double.IsNaN(temp) ? "--" : Deg(temp), bold, fg, x, cy, fmt);
                first = false;
            }

            if (showSystem)
            {
                if (!first) separator();
                if (draw != null) DrawChip(draw, x + 8 * scale, cy, 8 * scale, light ? Color.FromArgb(40, 120, 200) : Color.FromArgb(110, 185, 255));
                x += 16 * scale + 6 * scale;
                string[] labels = { "CPU", "RAM", "GPU" };
                float[] vals = { cpu, ram, gpu };
                for (int i = 0; i < 3; i++)
                {
                    if (compact)
                    {
                        if (i > 0) x = Txt(m, draw, "/", reg, dim, x + 2 * scale, cy, fmt) + 2 * scale;
                        x = Txt(m, draw, vals[i] < 0 ? "--" : Math.Round(vals[i]) + "%", i == 0 ? bold : reg, Level(vals[i], fg, light), x, cy, fmt);
                    }
                    else
                    {
                        if (i > 0) x += 8 * scale;
                        x = Txt(m, draw, labels[i], reg, dim, x, cy, fmt) + 5 * scale;
                        x = Txt(m, draw, vals[i] < 0 ? "--" : Math.Round(vals[i]) + "%", bold, Level(vals[i], fg, light), x, cy, fmt);
                    }
                }
            }
        }
        return x + 8 * scale;
    }

    static float Txt(Graphics m, Graphics draw, string s, Font f, Color c, float x, float cy, StringFormat fmt)
    {
        var sz = m.MeasureString(s, f, PointF.Empty, fmt);
        if (draw != null) using (var b = new SolidBrush(c)) draw.DrawString(s, f, b, x, cy - sz.Height / 2f, fmt);
        return x + sz.Width;
    }

    static Color Level(double p, Color normal, bool light)
    {
        if (p >= 95) return light ? Color.FromArgb(205, 40, 40) : Color.FromArgb(255, 95, 95);
        if (p >= 80) return light ? Color.FromArgb(200, 95, 0) : Color.FromArgb(240, 140, 60);
        return normal;
    }

    static void DrawLogo(Graphics g, float cx, float cy, float r)
    {
        using (var pen = new Pen(Color.FromArgb(217, 119, 87), r * 0.28f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            for (int i = 0; i < 12; i++)
            {
                double a = i * Math.PI / 6;
                float len = (i % 2 == 0) ? r : r * 0.72f;
                g.DrawLine(pen, cx + (float)Math.Cos(a) * r * 0.2f, cy + (float)Math.Sin(a) * r * 0.2f,
                                cx + (float)Math.Cos(a) * len, cy + (float)Math.Sin(a) * len);
            }
    }

    // Simplified coin logos, drawn in a unit circle (coordinates -1..1, scaled by r).
    void DrawCoin(Graphics g, float cx, float cy, float r)
    {
        using (var b = new SolidBrush(Color.FromArgb(255, Color.FromArgb(CoinColors[coin]))))
            g.FillEllipse(b, cx - r, cy - r, 2 * r, 2 * r);
        Func<float, float, PointF> P = (x, y) => new PointF(cx + x * r, cy + y * r);
        var white = Brushes.White;
        switch (Coins[coin])
        {
            case "BTC": // tilted bitcoin sign
            case "DOGE": // D with a bar
                var state = g.Save();
                g.TranslateTransform(cx, cy);
                if (coin == 0) g.RotateTransform(14);
                using (var f = new Font("Segoe UI", r * 1.4f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(coin == 0 ? "₿" : "Ð", f, white, new RectangleF(-r, -r + r * 0.08f, 2 * r, 2 * r), sf);
                g.Restore(state);
                break;
            case "ETH": // octahedron: lit left halves, dimmer right halves
                using (var dim = new SolidBrush(Color.FromArgb(185, 255, 255, 255)))
                {
                    g.FillPolygon(white, new[] { P(0, -0.66f), P(0, 0.22f), P(-0.4f, 0.02f) });
                    g.FillPolygon(dim, new[] { P(0, -0.66f), P(0.4f, 0.02f), P(0, 0.22f) });
                    g.FillPolygon(white, new[] { P(0, 0.34f), P(-0.4f, 0.11f), P(0, 0.68f) });
                    g.FillPolygon(dim, new[] { P(0, 0.34f), P(0.4f, 0.11f), P(0, 0.68f) });
                }
                break;
            case "SOL": // three slanted bars, purple-to-green gradient
                using (var grad = new LinearGradientBrush(P(-0.5f, 0.5f), P(0.5f, -0.5f), Color.FromArgb(153, 69, 255), Color.FromArgb(20, 241, 149)))
                {
                    float h = 0.1f, s = 0.2f;
                    for (int i = -1; i <= 1; i++)
                    {
                        float y = i * 0.33f;
                        g.FillPolygon(grad, i == 0
                            ? new[] { P(-0.48f, y - h), P(0.48f - s, y - h), P(0.48f, y + h), P(-0.48f + s, y + h) }
                            : new[] { P(-0.48f + s, y - h), P(0.48f, y - h), P(0.48f - s, y + h), P(-0.48f, y + h) });
                    }
                }
                break;
            case "BNB": // center diamond, side diamonds, top and bottom chevrons
                Action<float, float, float> diamond = (x, y, d) =>
                    g.FillPolygon(white, new[] { P(x, y - d), P(x + d, y), P(x, y + d), P(x - d, y) });
                diamond(0, 0, 0.17f);
                diamond(-0.48f, 0, 0.12f);
                diamond(0.48f, 0, 0.12f);
                using (var pen = new Pen(Color.White, 0.15f * r) { LineJoin = LineJoin.Miter })
                {
                    g.DrawLines(pen, new[] { P(-0.3f, -0.2f), P(0, -0.5f), P(0.3f, -0.2f) });
                    g.DrawLines(pen, new[] { P(-0.3f, 0.2f), P(0, 0.5f), P(0.3f, 0.2f) });
                }
                break;
            case "XRP": // two opposed curved V shapes
                using (var pen = new Pen(Color.White, 0.15f * r) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawCurve(pen, new[] { P(-0.48f, -0.46f), P(0, -0.1f), P(0.48f, -0.46f) }, 0.6f);
                    g.DrawCurve(pen, new[] { P(-0.48f, 0.46f), P(0, 0.1f), P(0.48f, 0.46f) }, 0.6f);
                }
                break;
            case "TON": // inverted gem with a center line
                using (var pen = new Pen(Color.White, 0.13f * r) { LineJoin = LineJoin.Round })
                {
                    g.DrawPolygon(pen, new[] { P(-0.46f, -0.36f), P(0.46f, -0.36f), P(0, 0.52f) });
                    g.DrawLine(pen, P(0, -0.36f), P(0, 0.52f));
                }
                break;
        }
    }

    string TooltipText()
    {
        var parts = new List<string>();
        if (showClaude) parts.Add(ClaudeTooltip());
        if (showCrypto) parts.Add(PriceTooltip());
        if (showWeather) parts.Add(WeatherTooltip());
        if (showSystem) parts.Add(SystemTooltip());
        return string.Join("\n\n", parts);
    }

    string PriceTooltip()
    {
        string pair = Coins[coin] + "/USDT";
        if (price < 0) return pair + ": " + (priceError != null ? "error: " + priceError : "loading...");
        return pair + ": " + FullPrice(price) + "  (24h " + ChangeText() + ")" +
               "\n24h high " + FullPrice(high) + "  -  low " + FullPrice(low) +
               (priceError != null ? "\nLast update failed: " + priceError : "");
    }

    string ClaudeTooltip()
    {
        if (error != null && p5 < 0) return "Error: " + error;
        return (error != null ? "Last update failed: " + error + "\n" : "") +
               "Updated " + updated.ToString("HH:mm") + "\n" +
               "5-hour: " + Pct(p5) + (r5.HasValue ? "  -  resets " + When(r5.Value) : "") +
               "\nWeekly: " + Pct(pW) + (rW.HasValue ? "  -  resets " + When(rW.Value) : "");
    }

    static string When(DateTime t) { return t.Date == DateTime.Today ? "at " + t.ToString("HH:mm") : t.ToString("ddd d MMM, HH:mm", System.Globalization.CultureInfo.InvariantCulture); }

    static bool IsLightTaskbar()
    {
        var v = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0);
        return v is int && (int)v == 1;
    }

    static bool IsFullscreen()
    {
        IntPtr f = GetForegroundWindow();
        if (f == IntPtr.Zero) return false;
        var cls = new StringBuilder(64);
        GetClassName(f, cls, 64);
        string c = cls.ToString();
        if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return false;
        RECT r;
        GetWindowRect(f, out r);
        var s = Screen.PrimaryScreen.Bounds;
        return r.L <= s.Left && r.T <= s.Top && r.R >= s.Right && r.B >= s.Bottom;
    }

    [STAThread]
    static void Main()
    {
        bool fresh;
        using (new Mutex(true, "ClaudeUsageWidget", out fresh))
        {
            if (!fresh) { Log("another instance is already running, exiting"); return; }
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => Log("ERROR " + e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("FATAL " + e.ExceptionObject);
            SetProcessDPIAware();
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            Application.EnableVisualStyles();
            Application.Run(new Widget());
        }
    }

    // ---------- Win32 ----------
    struct RECT { public int L, T, R, B; }
    struct POINT { public int x, y; }
    struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }

    [DllImport("user32")] static extern IntPtr FindWindow(string cls, string name);
    [DllImport("user32")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string name);
    [DllImport("user32")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32")] static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32")] static extern bool SetProcessDPIAware();
    [DllImport("user32")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dstDC, ref POINT dst, ref SIZE size, IntPtr srcDC, ref POINT src, int key, ref BLEND blend, int flags);
    [DllImport("gdi32")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32")] static extern bool DeleteObject(IntPtr o);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool CreateDirectory(string path, IntPtr sec);
    [StructLayout(LayoutKind.Sequential)]
    class MEMSTAT
    {
        public uint dwLength = 64, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32")] static extern bool GlobalMemoryStatusEx([In, Out] MEMSTAT m);
}
