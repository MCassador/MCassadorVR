// Headless check of the Zelda (BotwVR) install pieces: the complete package keeps its empty portable/ folder, and Cemu's community graphic
// packs land where BotwVR.exe looks for FPS++ (built with -main:MCassadorVR.TestZelda into a separate exe).
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MCassadorVR
{
    static class TestZelda
    {
        static int fails;
        static void Need(bool c, string m) { if (!c) fails++; Console.WriteLine((c ? "ok   " : "FAIL ") + m); }
        static int Main() { try { Run().GetAwaiter().GetResult(); } catch (Exception e) { Console.WriteLine("ERRO " + e); fails++; } Console.WriteLine("falhas: " + fails); return fails; }
        static async Task Run()
        {
            var root = Path.Combine(Path.GetTempPath(), "mcvr-teste-zelda"); if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root);
            var g = new Game { Id = "zelda", Name = "Zelda", Repo = "MCassador/BotwVR", Kind = Kind.CemuZelda };
            g.Latest = await GitHub.LatestAsync(g.Repo);
            var full = g.Latest.Assets.FirstOrDefault(x => x.Name.IndexOf("completo", StringComparison.OrdinalIgnoreCase) >= 0);
            Need(full != null, "BotwVR " + g.Latest.Tag + ": complete package " + (full != null ? full.Name : "missing"));
            var zip = Path.Combine(root, full.Name);
            await GitHub.DownloadAsync(full.Url, zip, (d, t) => { });
            var dir = Path.Combine(root, "Zelda BotW VR");
            Ops.Extract(zip, dir, (i, n) => { });
            var cemu = Ops.ModDir(g, dir);
            Need(File.Exists(Path.Combine(cemu, "Cemu.exe")) && File.Exists(Path.Combine(cemu, "BotwVR.exe")), "Cemu.exe and BotwVR.exe in " + cemu);
            Need(Directory.Exists(Path.Combine(cemu, "portable")), "the empty portable folder exists (Cemu keeps its settings inside)");

            var packsRel = await GitHub.LatestAsync("cemu-project/cemu_graphic_packs");
            var pa = packsRel.Assets.First(x => x.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            var pzip = Path.Combine(root, pa.Name);
            await GitHub.DownloadAsync(pa.Url, pzip, (d, t) => { });
            var packs = Path.Combine(cemu, "portable", "graphicPacks", "downloadedGraphicPacks");
            Ops.Extract(pzip, packs, (i, n) => { });
            Need(File.Exists(Path.Combine(packs, "BreathOfTheWild", "Mods", "FPS++", "rules.txt")), "community packs " + packsRel.Tag + ": FPS++ where BotwVR.exe looks for it");
            Need(Directory.Exists(Path.Combine(packs, "BreathOfTheWild", "Graphics")), "community packs: the Zelda Graphics pack too");

            // a located Cemu folder (Cemu.exe directly inside) is used as is
            Need(Ops.ModDir(g, cemu) == cemu, "ModDir of a located Cemu folder is the folder itself");
            var mod = g.Latest.Assets.FirstOrDefault(x => x.Name.EndsWith(".zip") && x.Name.IndexOf("completo", StringComparison.OrdinalIgnoreCase) < 0);
            Need(mod != null, "mod-only package for updates: " + (mod != null ? mod.Name : "missing"));
        }
    }
}
