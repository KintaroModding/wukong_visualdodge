using System.Reflection;
using CSharpModBase;

namespace VisualDodge
{
    // C# Loader creates this class and calls Init() once the game runs (on the loader's own thread, not the game
    // thread), and DeInit() before it reloads the mods (Ctrl+F5 with Develop=1 in b1cs.ini).
    public class ModMain : ICSharpMod
    {
        private Ticker ticker;

        public string Name => "Visual Dodge";

        // build.ps1 sets the version from mod.json.
        public string Version { get; } = typeof(ModMain).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

        public void Init()
        {
            ModLog.Start(Name, Version);
            ModLog.Info($"{Version} loaded.");
            Config.Load();
            ticker = new Ticker(new DodgeFlash());
            ticker.Start();
        }

        public void DeInit()
        {
            ticker?.Stop();
            ticker = null;
            ModLog.Info("unloaded.");
        }
    }
}
