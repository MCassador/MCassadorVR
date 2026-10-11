// Headless check of MCassadorVR's install / incremental update logic (built with -main:MCassadorVR.TestMain into a separate exe).
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MCassadorVR
{
    static class TestMain
    {
        static int fails;
        static void Need(bool c, string m) { if (!c) fails++; Console.WriteLine((c ? "ok   " : "FAIL ") + m); }
        static int Main() { try { Run().GetAwaiter().GetResult(); } catch (Exception e) { Console.WriteLine("ERRO " + e); fails++; } Console.WriteLine("falhas: " + fails); return fails; }
        static async Task Run()
        {
            var root = Path.Combine(Path.GetTempPath(), "mcvr-teste"); if (Directory.Exists(root)) Directory.Delete(root, true);
            var g = new Game { Id = "sm3dw", Name = "3D World", Repo = "MCassador/SuperMario3DWorld-VR", Kind = Kind.Cemu3DWorld };
            g.Latest = await GitHub.LatestAsync(g.Repo);
            Need(g.Latest.Tag.StartsWith("v"), "latest release: " + g.Latest.Tag + ", " + g.Latest.Assets.Count + " asset(s)");
            var a = g.Latest.Assets.First(x => x.Name.EndsWith("-install.zip"));
            var zip = Path.Combine(root, a.Name); Directory.CreateDirectory(root);
            long last = 0; await GitHub.DownloadAsync(a.Url, zip, (d, t) => last = d);
            Need(new FileInfo(zip).Length == a.Size && last == a.Size, "download complete: " + new FileInfo(zip).Length + " bytes");
            var dir = Path.Combine(root, "Super Mario 3D World VR");
            int ext = 0; Ops.Extract(zip, dir, (i, n) => ext = n);
            var mod = Ops.ModDir(g, dir);
            Need(File.Exists(Path.Combine(mod, "Start-VR.cmd")) && File.Exists(Path.Combine(dir, "Cemu_2.6", "Cemu.exe")), "extracted " + ext + " files: Cemu.exe and Start-VR.cmd in place");
            // nothing to do on a fresh install
            var n0 = await Ops.IncrementalAsync(g, g.Latest, mod, (t, p) => { });
            Need(n0 == 0, "fresh install: incremental update finds 0 changed files (" + n0 + ")");
            // an older patch and a missing DLL: exactly those two come back, verified against SHA256.json
            File.AppendAllText(Path.Combine(mod, "graphicPacks", "Mario3DWorld_VR", "patch_vr.asm"), "\n; old\n");
            File.Delete(Path.Combine(mod, "layer", "cemuvr_layer.dll"));
            var n1 = await Ops.IncrementalAsync(g, g.Latest, mod, (t, p) => Console.WriteLine("     " + t));
            Need(n1 == 2, "changed patch + missing DLL: 2 files downloaded (" + n1 + ")");
            var n2 = await Ops.IncrementalAsync(g, g.Latest, mod, (t, p) => { });
            Need(n2 == 0, "after the update everything matches SHA256.json (" + n2 + ")");
            // the overwrite-extract keeps existing files replaceable and never writes outside the folder
            Ops.Extract(zip, dir, (i, n) => { });
            Need(File.Exists(Path.Combine(mod, "VERSION")), "extract over an existing install works (overwrite)");
            // Galaxy 2 mod package (small) extracts next to its installer
            var g2 = new Game { Id = "smg2", Repo = "MCassador/SuperMarioGalaxy2-VR", Kind = Kind.Galaxy2 };
            g2.Latest = await GitHub.LatestAsync(g2.Repo);
            var m2 = g2.Latest.Assets.First(x => x.Name.EndsWith(".zip") && !x.Name.Contains("completo"));
            var zip2 = Path.Combine(root, m2.Name); await GitHub.DownloadAsync(m2.Url, zip2, (d, t) => { });
            var gdir = Path.Combine(root, "g2"); Ops.Extract(zip2, gdir, (i, n) => { });
            Need(File.Exists(Path.Combine(gdir, "instalar-smg2-vr.bat")) && File.Exists(Path.Combine(gdir, "SMG2-VR", "smgvr_layer.dll")), "Galaxy 2 mod package: installer and layer extracted");
            Directory.Delete(root, true);
        }
    }
}
