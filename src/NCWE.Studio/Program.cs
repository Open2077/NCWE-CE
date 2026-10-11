// SPDX-License-Identifier: GPL-3.0-only
namespace NCWE.Studio;

// One executable, two roles: the editor (XAML) or, with --cache-worker, the headless
// preflight worker that converts game data into the shared disk cache. The worker runs
// in its own process so its decoding never pauses the editor's garbage collector.
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--cache-worker")) return CacheWorker.Run(args);
        XamlGeneratedProgram.XamlGeneratedMain();
        return 0;
    }
}
