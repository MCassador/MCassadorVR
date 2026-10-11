// MCassador VR - one window for every VR mod: shows each game, checks GitHub for the newest release, installs, updates (only the changed files when the
// release publishes SHA256.json) and starts the game. .NET Framework 4.8 (built into Windows 10/11): a single small .exe, nothing else to install.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace MCassadorVR
{
    enum Kind { Cemu3DWorld, Galaxy2, Galaxy1, CemuZelda, Soon }

    // PT / EN: the first time from Windows' own language, then what was chosen (State.Lang)
    static class AppInfo
    {
        public const string Version = "1.0";
        public const string Repo = "MCassador/MCassadorVR";
    }

    static class L
    {
        public static bool En;
        public static string T(string pt, string en) { return En ? en : pt; }
    }

    sealed class Game
    {
        public string Id, Name, Platform, Blurb, Repo, Folder;
        public Kind Kind;
        public Color C1, C2;
        public Release Latest;
        public string Error;
        public string CoverUrl;   // the game's box art (GameTDB, the same art Dolphin shows), downloaded at run time and cached
        public Image Cover;
    }

    sealed class Asset { public string Name, Url; public long Size; }
    sealed class Release { public string Tag, Title, Notes; public List<Asset> Assets = new List<Asset>(); }

    // ------------------------------------------------------------------ state (%APPDATA%\MCassador VR\estado.json)
    sealed class Installed { public string Tag; public string Dir; }
    sealed class State
    {
        public string BaseDir;
        public string Lang;
        public Dictionary<string, Installed> Games = new Dictionary<string, Installed>();
        static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MCassador VR", "estado.json");
        public static State Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var s = new JavaScriptSerializer().Deserialize<State>(File.ReadAllText(FilePath, Encoding.UTF8));
                    if (s != null) { if (s.Games == null) s.Games = new Dictionary<string, Installed>(); return s; }
                }
            }
            catch { }
            return new State { BaseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "MCassador VR") };
        }
        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this), Encoding.UTF8);
        }
    }

    // ------------------------------------------------------------------ GitHub
    static class GitHub
    {
        static readonly HttpClient http;
        static GitHub()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("MCassadorVR/1.0");
        }
        public static async Task<Release> LatestAsync(string repo)
        {
            var json = await http.GetStringAsync("https://api.github.com/repos/" + repo + "/releases/latest");
            var d = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            var r = new Release { Tag = (string)d["tag_name"], Title = d["name"] as string, Notes = d["body"] as string ?? "" };
            foreach (Dictionary<string, object> a in (object[])d["assets"])
                r.Assets.Add(new Asset { Name = (string)a["name"], Url = (string)a["browser_download_url"], Size = Convert.ToInt64(a["size"]) });
            return r;
        }
        public static async Task<string> TextAsync(string url) { return await http.GetStringAsync(url); }
        public static async Task<byte[]> BytesAsync(string url) { return await http.GetByteArrayAsync(url); }
        // one file of the repository at a tag: raw.githubusercontent.com, then the API's raw contents; a few tries each (the CDN sometimes answers 503)
        public static async Task<byte[]> RepoFileAsync(string repo, string tag, string path)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    if (attempt % 2 == 0) return await http.GetByteArrayAsync(Raw(repo, tag, path));
                    var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + repo + "/contents/" +
                        string.Join("/", path.Split('/').Select(Uri.EscapeDataString)) + "?ref=" + Uri.EscapeDataString(tag));
                    req.Headers.Accept.ParseAdd("application/vnd.github.raw");
                    using (var resp = await http.SendAsync(req)) { resp.EnsureSuccessStatusCode(); return await resp.Content.ReadAsByteArrayAsync(); }
                }
                catch (Exception e) { last = e; await Task.Delay(800 * (attempt + 1)); }
            }
            throw new Exception(L.T("não consegui baixar ", "could not download ") + path + " (" + last.Message + ")");
        }
        public static string Raw(string repo, string tag, string path)
        {
            return "https://raw.githubusercontent.com/" + repo + "/" + tag + "/" + string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
        }
        public static async Task DownloadAsync(string url, string file, Action<long, long> progress)
        {
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1, done = 0;
                using (var src = await resp.Content.ReadAsStreamAsync())
                using (var dst = File.Create(file))
                {
                    var buf = new byte[1 << 16]; int n; var tick = Environment.TickCount;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n); done += n;
                        if (Environment.TickCount - tick > 100) { tick = Environment.TickCount; progress(done, total); }
                    }
                    progress(done, total);
                }
            }
        }
    }

    // ------------------------------------------------------------------ install / update / play per kind
    static class Ops
    {
        public static string Sha(byte[] b) { using (var h = SHA256.Create()) return string.Concat(h.ComputeHash(b).Select(x => x.ToString("x2"))); }
        public static string ShaFile(string f) { using (var h = SHA256.Create()) using (var s = File.OpenRead(f)) return string.Concat(h.ComputeHash(s).Select(x => x.ToString("x2"))); }

        // overwrite-extract (ZipFile.ExtractToDirectory does not overwrite on .NET Framework); optional strip of one top folder
        public static void Extract(string zip, string dir, Action<int, int> progress)
        {
            using (var z = ZipFile.OpenRead(zip))
            {
                var files = z.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                var root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
                foreach (var folder in z.Entries.Where(e => string.IsNullOrEmpty(e.Name)))
                {
                    var target = Path.GetFullPath(Path.Combine(dir, folder.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) Directory.CreateDirectory(target);
                }
                for (int i = 0; i < files.Count; i++)
                {
                    var e = files[i];
                    var target = Path.GetFullPath(Path.Combine(dir, e.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // no path escaping the folder
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
                    e.ExtractToFile(target, true);
                    if (i % 20 == 0 || i == files.Count - 1) progress(i + 1, files.Count);
                }
            }
        }

        public static string ModDir(Game g, string dir)
        {
            switch (g.Kind)
            {
                case Kind.Cemu3DWorld: return Path.Combine(dir, "Cemu_2.6", "Mario3DWorld-VR");
                case Kind.Galaxy2: return Path.Combine(dir, "SuperMarioGalaxy2-VR-completo");
                case Kind.CemuZelda: return File.Exists(Path.Combine(dir, "Cemu.exe")) ? dir : Path.Combine(dir, "Cemu_2.6");   // a located Cemu folder, or the package's
                default: return dir;
            }
        }

        // only the files whose SHA-256 differs from the release's SHA256.json are downloaded (raw files at the release tag)
        public static async Task<int> IncrementalAsync(Game g, Release r, string modDir, Action<string, double> status)
        {
            status(L.T("Verificando arquivos...", "Checking files..."), 0);
            var manifest = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(Encoding.UTF8.GetString(await GitHub.RepoFileAsync(g.Repo, r.Tag, "SHA256.json")));
            var changed = new List<KeyValuePair<string, string>>();
            foreach (var kv in manifest)
            {
                var local = Path.Combine(modDir, kv.Key.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(local) || !string.Equals(ShaFile(local), (string)kv.Value, StringComparison.OrdinalIgnoreCase))
                    changed.Add(new KeyValuePair<string, string>(kv.Key, (string)kv.Value));
            }
            changed.Add(new KeyValuePair<string, string>("SHA256.json", null));
            for (int i = 0; i < changed.Count; i++)
            {
                var path = changed[i].Key; var want = changed[i].Value;
                status(L.T("Baixando ", "Downloading ") + path + " (" + (i + 1) + L.T(" de ", " of ") + changed.Count + ")", (double)i / changed.Count);
                var data = await GitHub.RepoFileAsync(g.Repo, r.Tag, path);
                if (want != null && !string.Equals(Sha(data), want, StringComparison.OrdinalIgnoreCase))
                {
                    // text files may have been hashed with Windows line ends
                    var crlf = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data).Replace("\r\n", "\n").Replace("\n", "\r\n"));
                    if (string.Equals(Sha(crlf), want, StringComparison.OrdinalIgnoreCase)) data = crlf;
                    else throw new Exception(L.T("O arquivo ", "The downloaded file ") + path + L.T(" baixado não confere com a lista da versão.", " does not match the release's file list."));
                }
                var local = Path.Combine(modDir, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(local));
                if (File.Exists(local)) File.SetAttributes(local, FileAttributes.Normal);
                try { File.WriteAllBytes(local, data); }
                catch (IOException)
                {
                    // a DLL in use (the game open): the loaded one can be renamed, the new one takes its place
                    File.Move(local, local + ".antigo-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
                    File.WriteAllBytes(local, data);
                }
            }
            return changed.Count - 1;
        }

        public static Task<int> RunAndWait(string file, string workDir)
        {
            var tcs = new TaskCompletionSource<int>();
            var p = new Process { StartInfo = new ProcessStartInfo("cmd.exe", "/c \"\"" + file + "\"\"") { WorkingDirectory = workDir, UseShellExecute = true }, EnableRaisingEvents = true };
            p.Exited += (s, e) => tcs.TrySetResult(p.ExitCode);
            p.Start();
            return tcs.Task;
        }
    }

    // ------------------------------------------------------------------ drawing helpers
    static class G
    {
        public static float S = 1f;
        public static int P(float v) { return (int)Math.Round(v * S); }
        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
        public static PointF[] Star(float cx, float cy, float ro, float ri, int n = 5, float rot = -90)
        {
            var pts = new PointF[n * 2];
            for (int i = 0; i < n * 2; i++)
            {
                var a = (rot + i * 180f / n) * Math.PI / 180; var r = (i % 2 == 0) ? ro : ri;
                pts[i] = new PointF(cx + (float)(Math.Cos(a) * r), cy + (float)(Math.Sin(a) * r));
            }
            return pts;
        }
        public static readonly Color Bg = Color.FromArgb(14, 16, 22), Card = Color.FromArgb(24, 27, 36), CardLine = Color.FromArgb(40, 45, 58),
            Text = Color.FromArgb(236, 238, 244), Dim = Color.FromArgb(150, 156, 172), Green = Color.FromArgb(46, 204, 113), Blue = Color.FromArgb(64, 132, 255),
            Amber = Color.FromArgb(255, 184, 76), Red = Color.FromArgb(235, 87, 87);
    }

    sealed class FlatBtn : Control
    {
        public Color Fill = G.Blue; bool hover;
        public FlatBtn(string text, Color fill)
        {
            Text = text; Fill = fill; Cursor = Cursors.Hand; ForeColor = Color.White;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent; Font = new Font("Segoe UI Semibold", 10f);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var c = !Enabled ? Color.FromArgb(60, 64, 76) : hover ? ControlPaint.Light(Fill, 0.25f) : Fill;
            using (var p = G.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Height / 2f - 1)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, Enabled ? ForeColor : Color.FromArgb(150, 154, 166), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    // ------------------------------------------------------------------ one game card
    sealed class GameCard : Control
    {
        public readonly Game Game; readonly MainForm main;
        public string Status = L.T("Procurando a versão mais nova...", "Looking for the newest version..."), Detail = "";
        public Color StatusColor = G.Dim;
        public double Progress = -1;   // < 0 = no bar
        public readonly FlatBtn Primary, Notes, Folder, More;
        public GameCard(Game game, MainForm m)
        {
            Game = game; main = m;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = G.Bg;
            Primary = new FlatBtn("...", G.Blue); Notes = new FlatBtn(L.T("Novidades", "What's new"), Color.FromArgb(52, 58, 74));
            Folder = new FlatBtn(L.T("Pasta", "Folder"), Color.FromArgb(52, 58, 74)); More = new FlatBtn("•••", Color.FromArgb(52, 58, 74));
            foreach (var b in new[] { Primary, Notes, Folder, More }) Controls.Add(b);
            Primary.Click += (s, e) => main.OnPrimary(this);
            Notes.Click += (s, e) => main.ShowNotes(Game);
            Folder.Click += (s, e) => main.OpenFolder(Game);
            More.Click += (s, e) => main.ShowMore(this, More);
            Layout += (s, e) => PlaceButtons();
        }
        void PlaceButtons()
        {
            int h = G.P(36), y = Height - G.P(20) - h, x = Width - G.P(22);
            foreach (var b in new[] { More, Folder, Notes, Primary })
            {
                int w = b == Primary ? G.P(150) : b == More ? G.P(52) : G.P(112);
                x -= w; b.SetBounds(x, y, w, h); x -= G.P(10);
            }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
            using (var p = G.Round(r, G.P(16))) using (var b = new SolidBrush(G.Card)) using (var pen = new Pen(G.CardLine)) { g.FillPath(b, p); g.DrawPath(pen, p); }
            // cover: the game's colours, a symbol and its short name (drawn, no game art)
            // the box art standing up, as in Cemu's / Dolphin's game list; the drawn colours and symbol while it loads or offline
            float ch = Height - G.P(24);
            var cover = new RectangleF(G.P(12), G.P(12), ch * 160f / 224f, ch);
            using (var p = G.Round(cover, G.P(10)))
            using (var b = new LinearGradientBrush(cover, Game.C1, Game.C2, 35f))
            {
                var old = g.Clip; g.SetClip(p, CombineMode.Intersect);
                if (Game.Cover != null)
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    float s = Math.Max(cover.Width / Game.Cover.Width, cover.Height / Game.Cover.Height), w = Game.Cover.Width * s, h = Game.Cover.Height * s;
                    g.DrawImage(Game.Cover, cover.X + (cover.Width - w) / 2, cover.Y + (cover.Height - h) / 2, w, h);
                }
                else { g.FillPath(b, p); DrawSymbol(g, cover); }
                g.Clip = old;
                using (var pen = new Pen(Color.FromArgb(60, 255, 255, 255))) g.DrawPath(pen, p);
            }
            float x = cover.Right + G.P(24), y = G.P(22);
            using (var f = new Font("Segoe UI Semibold", 17f)) TextRenderer.DrawText(g, Game.Name, f, new Point((int)x, (int)y), G.Text);
            using (var f = new Font("Segoe UI", 9.5f)) TextRenderer.DrawText(g, Game.Platform, f, new Point((int)x + 1, (int)y + G.P(36)), G.Amber);
            using (var f = new Font("Segoe UI", 10f))
                TextRenderer.DrawText(g, Game.Blurb, f, new Rectangle((int)x, (int)y + G.P(60), Width - (int)x - G.P(24), G.P(42)), G.Dim, TextFormatFlags.WordBreak);
            using (var f = new Font("Segoe UI Semibold", 10f)) TextRenderer.DrawText(g, Status, f, new Point((int)x, Height - G.P(86)), StatusColor);
            if (Progress >= 0)
            {
                var bar = new RectangleF(x, Height - G.P(60), Width - x - G.P(24), G.P(8));
                using (var p = G.Round(bar, bar.Height / 2)) using (var b = new SolidBrush(Color.FromArgb(44, 49, 62))) g.FillPath(b, p);
                var fill = new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, (float)(bar.Width * Math.Min(1, Progress))), bar.Height);
                using (var p = G.Round(fill, bar.Height / 2)) using (var b = new LinearGradientBrush(bar, Game.C1, ControlPaint.Light(Game.C2), 0f)) g.FillPath(b, p);
                using (var f = new Font("Segoe UI", 9f)) TextRenderer.DrawText(g, Detail, f, new Point((int)x, Height - G.P(48)), G.Dim);
            }
            else if (Detail.Length > 0)
                using (var f = new Font("Segoe UI", 9f)) TextRenderer.DrawText(g, Detail, f, new Point((int)x, Height - G.P(62)), G.Dim);
        }
        void DrawSymbol(Graphics g, RectangleF c)
        {
            float cx = c.X + c.Width * 0.5f, cy = c.Y + c.Height * 0.42f, R = c.Width * 0.38f;
            using (var w = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
            using (var w2 = new SolidBrush(Color.FromArgb(150, 255, 255, 255)))
            {
                switch (Game.Kind)
                {
                    case Kind.Cemu3DWorld:   // a bell with a cat ear on each side
                        g.FillEllipse(w, cx - R * 0.75f, cy - R * 0.8f, R * 1.5f, R * 1.5f);
                        g.FillPolygon(w2, new[] { new PointF(cx - R * 0.62f, cy - R * 0.45f), new PointF(cx - R * 0.5f, cy - R * 1.05f), new PointF(cx - R * 0.1f, cy - R * 0.7f) });
                        g.FillPolygon(w2, new[] { new PointF(cx + R * 0.62f, cy - R * 0.45f), new PointF(cx + R * 0.5f, cy - R * 1.05f), new PointF(cx + R * 0.1f, cy - R * 0.7f) });
                        g.FillEllipse(w2, cx - R * 0.12f, cy + R * 0.35f, R * 0.24f, R * 0.24f);
                        break;
                    case Kind.Galaxy1:
                        g.FillPolygon(w2, G.Star(cx, cy, R, R * 0.42f));
                        break;
                    case Kind.Galaxy2:
                        g.FillPolygon(w2, G.Star(cx - R * 0.35f, cy + R * 0.1f, R * 0.75f, R * 0.32f));
                        g.FillPolygon(w, G.Star(cx + R * 0.55f, cy - R * 0.45f, R * 0.5f, R * 0.21f));
                        break;
                    case Kind.CemuZelda:
                    case Kind.Soon:          // three triangles
                        float t = R * 0.62f, hgt = t * 0.866f;
                        Action<float, float> tri = (ax, ay) => g.FillPolygon(w2, new[] { new PointF(ax, ay - hgt), new PointF(ax - t / 2, ay), new PointF(ax + t / 2, ay) });
                        tri(cx, cy - hgt * 0.1f); tri(cx - t / 2, cy + hgt * 0.9f); tri(cx + t / 2, cy + hgt * 0.9f);
                        break;
                }
            }
            using (var f = new Font("Segoe UI Black", 9f)) TextRenderer.DrawText(g, ShortName(), f, new Rectangle((int)c.X, (int)c.Bottom - G.P(30), (int)c.Width, G.P(24)), Color.White, TextFormatFlags.HorizontalCenter);
        }
        string ShortName()
        {
            switch (Game.Kind) { case Kind.Cemu3DWorld: return "3D WORLD VR"; case Kind.Galaxy1: return "GALAXY VR"; case Kind.Galaxy2: return "GALAXY 2 VR"; default: return "ZELDA VR"; }
        }
    }

    // ------------------------------------------------------------------ the window
    sealed class MainForm : Form
    {
        readonly State state = State.Load();
        readonly List<GameCard> cards = new List<GameCard>();
        readonly FlowLayoutPanel list;
        readonly FlatBtn baseBtn;
        readonly FlatBtn appUpdateBtn;
        Release appLatest;
        bool busy;

        static readonly Game[] Catalog =
        {
            new Game { Id = "sm3dw", Name = "Super Mario 3D World VR", Platform = L.T("Wii U · Cemu 2.6 (incluído)", "Wii U · Cemu 2.6 (included)"), Repo = "MCassador/SuperMario3DWorld-VR",
                Folder = "Super Mario 3D World VR", Kind = Kind.Cemu3DWorld, CoverUrl = "https://art.gametdb.com/wiiu/coverHQ/EN/ARDP01.jpg", C1 = Color.FromArgb(231, 76, 60), C2 = Color.FromArgb(243, 156, 18),
                Blurb = L.T("Primeira pessoa com luvas e braços, câmera 200 em arco, tocar na tela com o controle, menu no óculos.", "First person with gloves and arms, arc camera 200, touch the screen with the controller, in-headset menu.") },
            new Game { Id = "smg2", Name = "Super Mario Galaxy 2 VR", Platform = L.T("Wii · Dolphin VR (incluído)", "Wii · Dolphin VR (included)"), Repo = "MCassador/SuperMarioGalaxy2-VR",
                Folder = "Super Mario Galaxy 2 VR", Kind = Kind.Galaxy2, CoverUrl = "https://art.gametdb.com/wii/cover/US/SB4E01.png", C1 = Color.FromArgb(22, 160, 133), C2 = Color.FromArgb(41, 128, 185),
                Blurb = L.T("Primeira pessoa com corpo, braços e luvas, três câmeras, Yoshi e ponteiro livre, menu no óculos.", "First person with body, arms and gloves, three cameras, Yoshi and free pointer, in-headset menu.") },
            new Game { Id = "smg1", Name = "Super Mario Galaxy VR", Platform = L.T("Wii · Dolphin VR (baixa junto se faltar)", "Wii · Dolphin VR (downloaded too if missing)"), Repo = "MCassador/SuperMarioGalaxy-VR",
                Folder = "Super Mario Galaxy 2 VR", Kind = Kind.Galaxy1, CoverUrl = "https://art.gametdb.com/wii/cover/US/RMGE01.png", C1 = Color.FromArgb(108, 52, 131), C2 = Color.FromArgb(52, 73, 194),
                Blurb = L.T("Primeira pessoa, ponteiro livre que acerta de qualquer direção, transformações e câmeras em VR.", "First person, a free pointer that hits from any direction, power-ups and cameras in VR.") },
            new Game { Id = "zelda", Name = "Zelda: Breath of the Wild VR", Platform = L.T("Wii U · Cemu 2.6 (incluído)", "Wii U · Cemu 2.6 (included)"), Repo = "MCassador/BotwVR",
                Folder = "Zelda BotW VR", Kind = Kind.CemuZelda, CoverUrl = "https://art.gametdb.com/wiiu/coverHQ/EN/ALZP01.jpg", C1 = Color.FromArgb(160, 120, 40), C2 = Color.FromArgb(46, 125, 50),
                Blurb = L.T("Roleta 3D de armas com os ícones do jogo, menu em português, sem névoa e sem partículas. Instala o Cemu e os graphic packs sozinho.", "3D weapon wheel with the game's icons, Portuguese menu, no fog and no particles. Sets up Cemu and the graphic packs by itself.") },
        };

        public MainForm()
        {
            Text = "MCassador VR"; BackColor = G.Bg; ForeColor = G.Text; StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true;
            G.S = DeviceDpi / 96f;
            // tall enough for the four cards when the screen allows it; otherwise the list scrolls
            var work = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(G.P(1040), work.Width - 40), Math.Min(G.P(990), work.Height - 60)); MinimumSize = new Size(G.P(900), G.P(500));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var header = new Panel { Dock = DockStyle.Top, Height = G.P(104), BackColor = G.Bg };
            header.Paint += (s, e) =>
            {
                var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                using (var b = new LinearGradientBrush(new Rectangle(G.P(28), G.P(24), G.P(44), G.P(44)), G.Blue, Color.FromArgb(155, 89, 182), 45f))
                using (var p = G.Round(new RectangleF(G.P(28), G.P(26), G.P(44), G.P(44)), G.P(10))) g.FillPath(b, p);
                using (var f = new Font("Segoe UI Black", 13f)) TextRenderer.DrawText(g, "VR", f, new Rectangle(G.P(28), G.P(26), G.P(44), G.P(44)), Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                using (var f = new Font("Segoe UI Semibold", 22f)) TextRenderer.DrawText(g, "MCassador VR", f, new Point(G.P(84), G.P(18)), G.Text);
                using (var f = new Font("Segoe UI", 10f)) TextRenderer.DrawText(g, L.T("Seus jogos em realidade virtual — instale, atualize e jogue daqui.", "Your games in virtual reality — install, update and play from here."), f, new Point(G.P(87), G.P(62)), G.Dim);
            };
            baseBtn = new FlatBtn(L.T("Pasta dos jogos", "Games folder"), Color.FromArgb(52, 58, 74));
            baseBtn.Click += (s, e) => ChooseBase();
            var refresh = new FlatBtn(L.T("Procurar atualizações", "Check for updates"), G.Blue);
            refresh.Click += async (s, e) => await CheckAll();
            var langBtn = new FlatBtn(L.En ? "PT" : "EN", Color.FromArgb(52, 58, 74));
            langBtn.Click += (s, e) => { state.Lang = L.En ? "pt" : "en"; state.Save(); Application.Restart(); };
            appUpdateBtn = new FlatBtn("", G.Green) { Visible = false };
            appUpdateBtn.Click += async (s, e) => await UpdateApp();
            header.Controls.Add(baseBtn); header.Controls.Add(refresh); header.Controls.Add(langBtn); header.Controls.Add(appUpdateBtn);
            header.Layout += (s, e) =>
            {
                refresh.SetBounds(header.Width - G.P(28) - G.P(200), G.P(32), G.P(200), G.P(38));
                baseBtn.SetBounds(refresh.Left - G.P(12) - G.P(160), G.P(32), G.P(160), G.P(38));
                langBtn.SetBounds(baseBtn.Left - G.P(12) - G.P(56), G.P(32), G.P(56), G.P(38));
                appUpdateBtn.SetBounds(langBtn.Left - G.P(12) - G.P(220), G.P(32), G.P(220), G.P(38));
            };

            list = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false,
                                         BackColor = G.Bg, Padding = new Padding(G.P(22), G.P(4), G.P(22), G.P(16)) };
            foreach (var game in Catalog)
            {
                var c = new GameCard(game, this) { Height = G.P(196), Margin = new Padding(0, 0, 0, G.P(14)) };
                cards.Add(c); list.Controls.Add(c);
            }
            list.HandleCreated += (s, e) => { try { SetWindowTheme(list.Handle, "DarkMode_Explorer", null); } catch { } };
            list.Resize += (s, e) => { foreach (var c in cards) c.Width = list.ClientSize.Width - list.Padding.Horizontal - G.P(2); };
            Controls.Add(list); Controls.Add(header);
            var footer = new Label { Dock = DockStyle.Bottom, Height = G.P(28), ForeColor = G.Dim, BackColor = G.Bg, TextAlign = ContentAlignment.MiddleCenter,
                                     Font = new Font("Segoe UI", 8.5f), Text = L.T("Os jogos não estão incluídos — use cópias suas. Feito por MCassador.", "The games are not included — use your own copies. Made by MCassador.") };
            Controls.Add(footer);
            Shown += async (s, e) => { foreach (var c in cards) c.Width = list.ClientSize.Width - list.Padding.Horizontal - G.P(2); LoadCovers(); await CheckAll(); await CheckAppUpdate(); };
        }

        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4); } catch { }   // dark title bar (Windows 10 2004+ / 11)
        }

        // box art: from the local cache, else downloaded once (GameTDB) and kept in %APPDATA%\MCassador VR\capas
        void LoadCovers()
        {
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MCassador VR", "capas");
            foreach (var c in cards)
            {
                var card = c; var g = c.Game; if (g.CoverUrl == null) continue;
                var file = Path.Combine(cache, Path.GetFileName(new Uri(g.CoverUrl).AbsolutePath));
                Task.Run(async () =>
                {
                    try
                    {
                        if (!File.Exists(file)) { Directory.CreateDirectory(cache); File.WriteAllBytes(file, await GitHub.BytesAsync(g.CoverUrl)); }
                        var img = Image.FromStream(new MemoryStream(File.ReadAllBytes(file)));
                        BeginInvoke(new Action(() => { g.Cover = img; card.Invalidate(); }));
                    }
                    catch { }
                });
            }
        }

        string GameDir(Game g)
        {
            Installed i;
            if (state.Games.TryGetValue(g.Id, out i) && !string.IsNullOrEmpty(i.Dir)) return i.Dir;
            return g.Folder == null ? null : Path.Combine(state.BaseDir, g.Folder);
        }
        Installed Inst(Game g) { Installed i; return state.Games.TryGetValue(g.Id, out i) ? i : null; }

        async Task CheckAll()
        {
            foreach (var c in cards)
            {
                if (c.Game.Kind == Kind.Soon) { Refresh(c); continue; }
                c.Status = L.T("Procurando a versão mais nova...", "Looking for the newest version..."); c.StatusColor = G.Dim; c.Invalidate();
            }
            await Task.WhenAll(cards.Where(c => c.Game.Kind != Kind.Soon).Select(async c =>
            {
                try { c.Game.Latest = await GitHub.LatestAsync(c.Game.Repo); c.Game.Error = null; }
                catch (Exception ex) { c.Game.Error = ex.Message; }
                Refresh(c);
            }));
        }

        void Refresh(GameCard c)
        {
            var g = c.Game; var inst = Inst(g);
            c.Progress = -1; c.Detail = ""; c.Notes.Enabled = g.Latest != null; c.Folder.Enabled = inst != null; c.More.Enabled = g.Kind != Kind.Soon;
            if (g.Kind == Kind.Soon)
            {
                c.Status = L.T("Em breve", "Coming soon"); c.StatusColor = G.Amber; c.Primary.Text = L.T("Em breve", "Coming soon"); c.Primary.Enabled = false; c.Notes.Enabled = false; c.Folder.Enabled = false;
            }
            else if (g.Latest == null)
            {
                c.Status = g.Error != null ? L.T("Sem conexão com o GitHub", "No connection to GitHub") : L.T("Procurando...", "Looking..."); c.StatusColor = g.Error != null ? G.Red : G.Dim;
                c.Primary.Text = inst != null ? L.T("Jogar", "Play") : L.T("Instalar", "Install"); c.Primary.Fill = inst != null ? G.Green : G.Blue; c.Primary.Enabled = inst != null && !busy;
                c.Detail = inst != null ? L.T("Instalado: ", "Installed: ") + inst.Tag : "";
            }
            else if (inst == null)
            {
                c.Status = L.T("Não instalado · versão ", "Not installed · version ") + g.Latest.Tag + L.T(" disponível", " available"); c.StatusColor = G.Dim;
                c.Primary.Text = L.T("Instalar", "Install"); c.Primary.Fill = G.Blue; c.Primary.Enabled = !busy;
                var size = InstallAsset(g); c.Detail = size != null ? "Download: " + Mb(size.Size) : "";
            }
            else if (inst.Tag != g.Latest.Tag)
            {
                c.Status = L.T("Atualização disponível: ", "Update available: ") + inst.Tag + " → " + g.Latest.Tag; c.StatusColor = G.Green;
                c.Primary.Text = L.T("Atualizar", "Update"); c.Primary.Fill = G.Green; c.Primary.Enabled = !busy;
                c.Detail = L.T("Baixa só o que mudou.", "Downloads only what changed.");
            }
            else
            {
                c.Status = L.T("Instalado · ", "Installed · ") + inst.Tag + L.T(" (a mais nova)", " (the newest)"); c.StatusColor = G.Dim;
                c.Primary.Text = L.T("Jogar", "Play"); c.Primary.Fill = G.Green; c.Primary.Enabled = !busy;
            }
            c.Primary.Invalidate(); c.Invalidate();
        }

        static string Mb(long b) { return (b / 1048576.0).ToString("0.0") + " MB"; }
        Asset InstallAsset(Game g)
        {
            if (g.Latest == null) return null;
            switch (g.Kind)
            {
                case Kind.Cemu3DWorld: return g.Latest.Assets.FirstOrDefault(a => a.Name.EndsWith("-install.zip", StringComparison.OrdinalIgnoreCase));
                case Kind.Galaxy2:
                case Kind.CemuZelda: return g.Latest.Assets.FirstOrDefault(a => a.Name.IndexOf("completo", StringComparison.OrdinalIgnoreCase) >= 0);
                default: return g.Latest.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            }
        }
        Asset ModAsset(Game g)
        {
            return g.Latest?.Assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && a.Name.IndexOf("completo", StringComparison.OrdinalIgnoreCase) < 0);
        }

        void SetBusy(bool b) { busy = b; foreach (var c in cards) Refresh(c); }

        public async void OnPrimary(GameCard c)
        {
            var g = c.Game; var inst = Inst(g);
            if (busy || g.Kind == Kind.Soon) return;
            if (inst != null && (g.Latest == null || inst.Tag == g.Latest.Tag)) { Play(g); return; }
            SetBusy(true);
            try
            {
                if (inst == null) await Install(c); else await Update(c);
                SetBusy(false);
                c.Status = L.T("Pronto! ", "Done! ") + g.Name + " " + g.Latest.Tag + L.T(" instalado.", " installed."); c.StatusColor = G.Green; c.Invalidate();
            }
            catch (OperationCanceledException) { SetBusy(false); }
            catch (Exception ex)
            {
                SetBusy(false);
                c.Status = L.T("Não deu certo: ", "It did not work: ") + ex.Message; c.StatusColor = G.Red; c.Invalidate();
            }
        }

        void Show(GameCard c, string text, double progress, string detail)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => Show(c, text, progress, detail))); return; }
            c.Status = text; c.StatusColor = G.Text; c.Progress = progress; c.Detail = detail ?? ""; c.Invalidate();
        }

        async Task<string> Download(GameCard c, Asset a, string what)
        {
            var tmp = Path.Combine(Path.GetTempPath(), "MCassadorVR-" + a.Name);
            await GitHub.DownloadAsync(a.Url, tmp, (done, total) =>
                Show(c, what, total > 0 ? (double)done / total : 0, Mb(done) + (total > 0 ? L.T(" de ", " of ") + Mb(total) : "")));
            return tmp;
        }

        // the folder the complete package goes to (the game's own folder is made inside it); null = cancelled
        string AskInstallDir(Game g, string what)
        {
            using (var d = new FolderBrowserDialog { Description = L.T("Onde instalar ", "Where should ") + what + L.T("? (uma pasta \"", " be installed? (a folder \"") + g.Folder + L.T("\" é criada dentro dela, já com tudo pronto)", "\" is created inside it, with everything ready)"),
                                                     SelectedPath = Directory.Exists(state.BaseDir) ? state.BaseDir : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ShowNewFolderButton = true })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return null;
                state.BaseDir = d.SelectedPath; state.Save();
                return Path.Combine(d.SelectedPath, g.Folder);
            }
        }

        // the complete package (the mod with Cemu or with Dolphin VR) into the chosen folder
        async Task InstallFull(GameCard c, Game g, string dir)
        {
            var a = InstallAsset(g) ?? throw new Exception(L.T("a versão ", "version ") + g.Latest.Tag + L.T(" não tem o pacote completo.", " has no complete package."));
            var zip = await Download(c, a, L.T("Baixando ", "Downloading ") + g.Name + (g.Kind == Kind.Cemu3DWorld || g.Kind == Kind.CemuZelda ? L.T(" com o Cemu...", " with Cemu...") : L.T(" com o Dolphin VR...", " with Dolphin VR...")));
            await Task.Run(() => Ops.Extract(zip, dir, (i, n) => Show(c, L.T("Instalando...", "Installing..."), (double)i / n, i + L.T(" de ", " of ") + n + L.T(" arquivos", " files"))));
            try { File.Delete(zip); } catch { }
            state.Games[g.Id] = new Installed { Tag = g.Latest.Tag, Dir = dir }; state.Save();
        }

        async Task Install(GameCard c)
        {
            var g = c.Game;
            if (g.Kind == Kind.Galaxy1)
            {
                // Galaxy 1 has no emulator of its own: it goes into the Dolphin VR of the Galaxy 2 package, downloaded too when it is not there yet
                var g2game = Catalog.First(x => x.Id == "smg2"); var g2 = Inst(g2game);
                if (g2 == null)
                {
                    if (g2game.Latest == null) g2game.Latest = await GitHub.LatestAsync(g2game.Repo);
                    var dir2 = AskInstallDir(g2game, L.T("o Dolphin VR (vem junto com o Super Mario Galaxy 2 VR, os dois jogos usam o mesmo)", "Dolphin VR (it comes with Super Mario Galaxy 2 VR; both games use the same one)"));
                    if (dir2 == null) throw new OperationCanceledException();
                    await InstallFull(c, g2game, dir2);
                    g2 = Inst(g2game);
                }
                await InstallGalaxyMod(c, Ops.ModDir(g2game, g2.Dir), "instalar-smg-vr.bat");
                state.Games[g.Id] = new Installed { Tag = g.Latest.Tag, Dir = g2.Dir }; state.Save();
                return;
            }
            var dir = AskInstallDir(g, g.Name + (g.Kind == Kind.Cemu3DWorld || g.Kind == Kind.CemuZelda ? L.T(" (com o Cemu)", " (with Cemu)") : L.T(" (com o Dolphin VR)", " (with Dolphin VR)")));
            if (dir == null) throw new OperationCanceledException();
            await InstallFull(c, g, dir);
            if (g.Kind == Kind.CemuZelda)
            {
                var cemu = Ops.ModDir(g, dir);
                await InstallCommunityPacks(c, cemu);
                SetupZeldaGameFolder(cemu);
            }
        }

        // Cemu's community graphic packs (FPS++ and the Zelda graphics come in them), the same zip and place Cemu's own "download" button uses
        async Task InstallCommunityPacks(GameCard c, string cemuDir)
        {
            var packs = Path.Combine(cemuDir, "portable", "graphicPacks", "downloadedGraphicPacks");
            if (File.Exists(Path.Combine(packs, "BreathOfTheWild", "Mods", "FPS++", "rules.txt"))) return;
            var rel = await GitHub.LatestAsync("cemu-project/cemu_graphic_packs");
            var a = rel.Assets.FirstOrDefault(x => x.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ?? throw new Exception(L.T("não achei os graphic packs da comunidade.", "could not find the community graphic packs."));
            var zip = await Download(c, a, L.T("Baixando os graphic packs da comunidade...", "Downloading the community graphic packs..."));
            await Task.Run(() => Ops.Extract(zip, packs, (i, n) => Show(c, L.T("Instalando os graphic packs...", "Installing the graphic packs..."), (double)i / n, i + L.T(" de ", " of ") + n + L.T(" arquivos", " files"))));
            try { File.Delete(zip); } catch { }
            var number = new string(rel.Tag.Where(char.IsDigit).ToArray());
            File.WriteAllText(Path.Combine(packs, "version.txt"), "Cemu Graphic Packs: v" + number);
        }

        // asks for the player's own Zelda (.wua or the game folder) and writes Cemu's first settings with that folder, so Cemu opens straight into
        // the game list (BotwVR.exe then turns on its packs and Vulkan); skipped = Cemu's welcome screen does it on the first start
        void SetupZeldaGameFolder(string cemuDir)
        {
            var settings = Path.Combine(cemuDir, "portable", "settings.xml");
            if (File.Exists(settings)) return;
            MessageBox.Show(this, L.T("Agora escolha o arquivo do SEU Zelda: Breath of the Wild (o .wua com a atualização v208), para o Cemu já achar o jogo.\n\nSe preferir, cancele: na primeira vez o Cemu pede a pasta dos jogos.",
                                      "Now choose YOUR Zelda: Breath of the Wild file (the .wua with update v208), so Cemu finds the game right away.\n\nOr cancel: Cemu asks for the games folder the first time."), Text);
            using (var d = new OpenFileDialog { Title = "Zelda: Breath of the Wild", Filter = L.T("Jogo de Wii U", "Wii U game") + " (*.wua;*.wud;*.wux;*.rpx)|*.wua;*.wud;*.wux;*.rpx" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var folder = Path.GetDirectoryName(d.FileName);
                if (d.FileName.EndsWith(".rpx", StringComparison.OrdinalIgnoreCase))   // ...\<game>\code\U-King.rpx -> the folder that holds <game>
                    folder = Path.GetDirectoryName(Path.GetDirectoryName(folder) ?? folder) ?? folder;
                Directory.CreateDirectory(Path.GetDirectoryName(settings));
                File.WriteAllText(settings,
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<content>\r\n    <GamePaths>\r\n        <Entry>" + System.Security.SecurityElement.Escape(folder) + "</Entry>\r\n    </GamePaths>\r\n" +
                    "    <Graphic>\r\n        <api>1</api>\r\n    </Graphic>\r\n    <GraphicPack>\r\n    </GraphicPack>\r\n</content>\r\n", new UTF8Encoding(false));
            }
        }

        async Task InstallGalaxyMod(GameCard c, string root, string bat)
        {
            var a = ModAsset(c.Game) ?? throw new Exception(L.T("a versão não tem o pacote do mod.", "the release has no mod package."));
            var zip = await Download(c, a, L.T("Baixando o mod...", "Downloading the mod..."));
            await Task.Run(() => Ops.Extract(zip, root, (i, n) => Show(c, L.T("Copiando...", "Copying..."), (double)i / n, i + L.T(" de ", " of ") + n + L.T(" arquivos", " files"))));
            try { File.Delete(zip); } catch { }
            Show(c, L.T("Instalando no Dolphin VR (feche o Dolphin se estiver aberto)...", "Installing into Dolphin VR (close Dolphin if it is open)..."), 1, "");
            var code = await Ops.RunAndWait(Path.Combine(root, bat), root);
            if (code != 0) throw new Exception(L.T("o instalador do mod terminou com erro (código ", "the mod installer ended with an error (code ") + code + ").");
        }

        async Task Update(GameCard c)
        {
            var g = c.Game; var inst = Inst(g);
            switch (g.Kind)
            {
                case Kind.Cemu3DWorld:
                    if (Process.GetProcessesByName("Cemu").Length > 0) throw new Exception(L.T("feche o Cemu antes de atualizar.", "close Cemu before updating."));
                    var n = await Ops.IncrementalAsync(g, g.Latest, Ops.ModDir(g, inst.Dir), (t, p) => Show(c, t, p, ""));
                    state.Games[g.Id].Tag = g.Latest.Tag; state.Save();
                    Show(c, L.T("Atualizado: ", "Updated: ") + n + L.T(" arquivo(s) novo(s).", " new file(s)."), -1, "");
                    break;
                case Kind.Galaxy2:
                    await InstallGalaxyMod(c, Ops.ModDir(g, inst.Dir), "instalar-smg2-vr.bat");
                    state.Games[g.Id].Tag = g.Latest.Tag; state.Save();
                    break;
                case Kind.CemuZelda:
                {
                    if (Process.GetProcessesByName("Cemu").Length > 0 || Process.GetProcessesByName("BotwVR").Length > 0)
                        throw new Exception(L.T("feche o Cemu antes de atualizar.", "close Cemu before updating."));
                    var a = ModAsset(g) ?? throw new Exception(L.T("a versão não tem o pacote do mod.", "the release has no mod package."));
                    var zip = await Download(c, a, L.T("Baixando o mod...", "Downloading the mod..."));
                    var tmp = Path.Combine(Path.GetTempPath(), "MCassadorVR-BotwVR");
                    await Task.Run(() => Ops.Extract(zip, tmp, (i, n) => { }));
                    var cemu = Ops.ModDir(g, inst.Dir);
                    File.Copy(Path.Combine(tmp, "BotwVR.exe"), Path.Combine(cemu, "BotwVR.exe"), true);
                    try { File.Delete(zip); Directory.Delete(tmp, true); } catch { }
                    await InstallCommunityPacks(c, cemu);
                    state.Games[g.Id].Tag = g.Latest.Tag; state.Save();
                    break;
                }
                case Kind.Galaxy1:
                    var g2 = Inst(Catalog.First(x => x.Id == "smg2"));
                    if (g2 == null) throw new Exception(L.T("o Super Mario Galaxy 2 VR (que traz o Dolphin VR) não está mais instalado.", "Super Mario Galaxy 2 VR (which brings Dolphin VR) is no longer installed."));
                    await InstallGalaxyMod(c, Ops.ModDir(Catalog.First(x => x.Id == "smg2"), g2.Dir), "instalar-smg-vr.bat");
                    state.Games[g.Id].Tag = g.Latest.Tag; state.Save();
                    break;
            }
        }

        void Play(Game g)
        {
            var inst = Inst(g); if (inst == null) return;
            string file = null, dir = null;
            switch (g.Kind)
            {
                case Kind.Cemu3DWorld: dir = Ops.ModDir(g, inst.Dir); file = Path.Combine(dir, "Start-VR.cmd"); break;
                case Kind.Galaxy2: dir = Ops.ModDir(g, inst.Dir); file = Path.Combine(dir, "Jogar-SMG2-VR.bat"); break;
                case Kind.Galaxy1: dir = Ops.ModDir(Catalog.First(x => x.Id == "smg2"), inst.Dir); file = Path.Combine(dir, "Dolphin.exe"); break;
                case Kind.CemuZelda: dir = Ops.ModDir(g, inst.Dir); file = Path.Combine(dir, "BotwVR.exe"); break;
            }
            if (file == null || !File.Exists(file)) { MessageBox.Show(this, L.T("Não achei ", "Could not find ") + file + L.T(".\nTente instalar de novo.", ".\nTry installing again."), Text); return; }
            Process.Start(new ProcessStartInfo(file) { WorkingDirectory = dir, UseShellExecute = true });
        }

        public void ShowNotes(Game g)
        {
            if (g.Latest == null) return;
            using (var f = new Form { Text = g.Name + " — " + g.Latest.Tag, BackColor = G.Card, ForeColor = G.Text, StartPosition = FormStartPosition.CenterParent,
                                      ClientSize = new Size(G.P(720), G.P(520)), MinimizeBox = false, ShowIcon = false })
            {
                var t = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Vertical, BorderStyle = BorderStyle.None,
                                      BackColor = G.Card, ForeColor = G.Text, Font = new Font("Segoe UI", 10.5f),
                                      Text = (g.Latest.Title ?? g.Latest.Tag) + "\r\n\r\n" + g.Latest.Notes.Replace("\r\n", "\n").Replace("\n", "\r\n").Replace("**", "") };
                f.Padding = new Padding(G.P(18)); f.Controls.Add(t); t.Select(0, 0);
                f.ShowDialog(this);
            }
        }

        public void OpenFolder(Game g)
        {
            var i = Inst(g); if (i == null) return;
            var d = g.Kind == Kind.Galaxy1 ? Ops.ModDir(Catalog.First(x => x.Id == "smg2"), i.Dir) : Ops.ModDir(g, i.Dir);
            if (Directory.Exists(d)) Process.Start("explorer.exe", "\"" + d + "\"");
        }

        public void ShowMore(GameCard c, Control at)
        {
            var m = new ContextMenuStrip { BackColor = G.Card, ForeColor = G.Text, ShowImageMargin = false, Font = new Font("Segoe UI", 10f) };
            var g = c.Game; var inst = Inst(g);
            if (g.Kind == Kind.Cemu3DWorld)
                m.Items.Add(L.T("Já tenho instalado: localizar a pasta...", "Already installed: locate the folder..."), null, (s, e) => Locate(c));
            if (g.Kind == Kind.CemuZelda)
                m.Items.Add(L.T("Já tenho o Cemu com o Zelda: localizar a pasta do Cemu...", "I already have Cemu with Zelda: locate the Cemu folder..."), null, (s, e) => LocateZelda(c));
            if (inst != null)
            {
                m.Items.Add(L.T("Instalar de novo (versão ", "Install again (version ") + (g.Latest?.Tag ?? inst.Tag) + ")", null, async (s, e) =>
                {
                    if (busy || g.Latest == null) return;
                    state.Games.Remove(g.Id); SetBusy(true);
                    try { await Install(c); SetBusy(false); c.Status = L.T("Pronto!", "Done!"); c.StatusColor = G.Green; c.Invalidate(); }
                    catch (Exception ex) { state.Games[g.Id] = inst; state.Save(); SetBusy(false); c.Status = L.T("Não deu certo: ", "It did not work: ") + ex.Message; c.StatusColor = G.Red; c.Invalidate(); }
                });
                m.Items.Add(L.T("Tirar da lista (não apaga arquivos)", "Remove from the list (keeps the files)"), null, (s, e) => { state.Games.Remove(g.Id); state.Save(); Refresh(c); });
            }
            if (g.Repo != null) m.Items.Add(L.T("Abrir a página no GitHub", "Open the GitHub page"), null, (s, e) => Process.Start("https://github.com/" + g.Repo + "/releases"));
            m.Show(at, new Point(0, at.Height));
        }

        void Locate(GameCard c)
        {
            using (var d = new FolderBrowserDialog { Description = L.T("Escolha a pasta que tem o Cemu_2.6 (ou a própria Cemu_2.6) do Super Mario 3D World VR", "Choose the folder that holds Cemu_2.6 (or Cemu_2.6 itself) of Super Mario 3D World VR") })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                var dir = d.SelectedPath;
                if (!Directory.Exists(Path.Combine(dir, "Cemu_2.6")) && Directory.Exists(Path.Combine(dir, "Mario3DWorld-VR"))) dir = Path.GetDirectoryName(dir);
                var ver = Path.Combine(dir, "Cemu_2.6", "Mario3DWorld-VR", "VERSION");
                if (!File.Exists(ver)) { MessageBox.Show(this, L.T("Não achei Cemu_2.6\\Mario3DWorld-VR\\VERSION nessa pasta.", "Could not find Cemu_2.6\\Mario3DWorld-VR\\VERSION in that folder."), Text); return; }
                var v = File.ReadAllText(ver).Trim();
                var tag = "v" + v.Replace("Version", "").Trim();
                state.Games[c.Game.Id] = new Installed { Tag = tag, Dir = dir }; state.Save(); Refresh(c);
            }
        }

        // an existing Cemu that already runs Zelda: it becomes the install, and "Update" puts the newest BotwVR.exe in it
        void LocateZelda(GameCard c)
        {
            using (var d = new FolderBrowserDialog { Description = L.T("Escolha a pasta do Cemu (a que tem o Cemu.exe) onde você joga o Zelda", "Choose the Cemu folder (the one with Cemu.exe) where you play Zelda") })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!File.Exists(Path.Combine(d.SelectedPath, "Cemu.exe"))) { MessageBox.Show(this, L.T("Não achei o Cemu.exe nessa pasta.", "Could not find Cemu.exe in that folder."), Text); return; }
                var hasMod = File.Exists(Path.Combine(d.SelectedPath, "BotwVR.exe"));
                state.Games[c.Game.Id] = new Installed { Tag = hasMod && c.Game.Latest != null ? c.Game.Latest.Tag : "v0", Dir = d.SelectedPath }; state.Save(); Refresh(c);
            }
        }

        static Version ParseVersion(string tag)
        {
            var digits = new string((tag ?? "").SkipWhile(ch => !char.IsDigit(ch)).TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray());
            Version v;
            return Version.TryParse(digits.Contains(".") ? digits : digits + ".0", out v) ? v : new Version(0, 0);
        }

        async Task CheckAppUpdate()
        {
            try
            {
                appLatest = await GitHub.LatestAsync(AppInfo.Repo);
                if (ParseVersion(appLatest.Tag) > ParseVersion(AppInfo.Version) && appLatest.Assets.Any(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                {
                    appUpdateBtn.Text = L.T("Atualizar o app → ", "Update the app → ") + appLatest.Tag;
                    appUpdateBtn.Visible = true; appUpdateBtn.Invalidate();
                }
            }
            catch { }
        }

        // the running .exe can be renamed but not overwritten: rename it to .old, put the new one in its place and restart (the .old goes at the next start)
        async Task UpdateApp()
        {
            if (busy || appLatest == null) return;
            var a = appLatest.Assets.FirstOrDefault(x => x.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (a == null) return;
            SetBusy(true);
            try
            {
                var tmp = Path.Combine(Path.GetTempPath(), "MCassadorVR-novo.exe");
                appUpdateBtn.Text = L.T("Baixando...", "Downloading..."); appUpdateBtn.Invalidate();
                await GitHub.DownloadAsync(a.Url, tmp, (done, total) => { });
                var exe = Application.ExecutablePath;
                var old = exe + ".old";
                if (File.Exists(old)) File.Delete(old);
                File.Move(exe, old);
                File.Move(tmp, exe);
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                Application.Exit();
            }
            catch (Exception ex)
            {
                SetBusy(false);
                appUpdateBtn.Text = L.T("Atualizar o app → ", "Update the app → ") + appLatest.Tag;
                MessageBox.Show(this, L.T("Não deu certo atualizar o app: ", "Could not update the app: ") + ex.Message, Text);
            }
        }

        void ChooseBase()
        {
            using (var d = new FolderBrowserDialog { Description = L.T("Pasta onde os jogos novos serão instalados", "Folder where new games are installed"), SelectedPath = state.BaseDir })
                if (d.ShowDialog(this) == DialogResult.OK) { state.BaseDir = d.SelectedPath; state.Save(); foreach (var c in cards) Refresh(c); }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            try { var old = Application.ExecutablePath + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }   // left by the last self-update
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var st = State.Load();   // the language before anything (the game list is built with it)
            L.En = st.Lang != null ? st.Lang == "en" : !System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("pt", StringComparison.OrdinalIgnoreCase);
            Application.Run(new MainForm());
        }
    }
}
