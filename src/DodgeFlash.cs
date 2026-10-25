using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using b1;
using b1.BGW;
using BtlShare;
using GSDispLib;
using UnrealEngine.Engine;
using UnrealEngine.Runtime;

namespace VisualDodge
{
    // The glow. The game opens the player's perfect-dodge window when an enemy's attack warning reaches the player:
    // BUS_PreciseDodgeComp sets BUC_PreciseDodgeData.CurrentPreciseDodgeTimeSeconds to PreciseDodgeTolerantTimeSeconds
    // (0.03 s in 1.0.21) on every frame of that contact and counts it down. The glow comes ReactionSeconds before that
    // moment (AttackPredictor reads it from the enemy's attack montage), so a press after the player's reaction time
    // lands in the window. An attack that is not predicted (projectiles, ground effects) gets the glow when the window
    // opens.
    //
    // The glow is one of the game's own display clips for Wukong (DBC data assets in sunwukong/DBC), played on the
    // character through his clip manager BUS_DispLibDBCManageComp (Evt_RequestApplyOneDBCDataAsset). The default clip,
    // Talent/DBC_wukong_buff_precisedodge, turns up the emissive material layer ML_Emissive_JGB of the meshes tagged
    // Weapon (the staff) and plays a particle effect. The game's rim light "ArtFresnel" does not work on characters:
    // the game gives them no glow component, and setting its parameter in Wukong's materials showed nothing (1.1 to 1.3).
    internal sealed class DodgeFlash
    {
        private const string ClipFolder = "/Game/00Main/VFX/Characters/sunwukong/DBC/";
        private const float DefaultSeconds = 0.25f;
        // A window that opens this long after the last glow (plus ReactionSeconds) was not predicted.
        private const double PredictionSlack = 1.0;
        private const int Reports = 20;

        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly AttackPredictor predictor = new AttackPredictor();
        private readonly Dictionary<string, double> cued = new Dictionary<string, double>();
        private readonly Dictionary<string, BUC_DispLibDispBaseConfigDataAsset> clips = new Dictionary<string, BUC_DispLibDispBaseConfigDataAsset>();
        private readonly Dictionary<string, double> nextLoad = new Dictionary<string, double>();
        private readonly HashSet<string> missingLogged = new HashSet<string>();

        private bool wasOpen;
        private bool flashing;
        private double flashStart;
        private float flashSeconds;
        private int flashRequest;
        private BUS_DispLibEventCollection flashEvents;
        private BGUPlayerCharacterCS flashPlayer;
        private double lastCueAt = -100.0;
        private double expectedOpenAt = -1.0;
        private int windows;
        private int predictedCues;
        private int predictedWindows;
        private double leadSum;
        private int fallbackGlows;
        private bool noEventsLogged;
        private bool noPredictionLogged;

        private static float Seconds => Config.FlashSeconds > 0f ? Config.FlashSeconds : DefaultSeconds;

        public void Frame()
        {
            if (!Config.Enable)
            {
                StopFlash();
                wasOpen = false;
                return;
            }
            List<BGWGameInstanceCS> instances = BGWGameInstanceCS.GetAllAliveInstance();
            BGWGameInstanceCS instance = instances != null && instances.Count > 0 ? instances[0] : null;
            if (instance == null || instance.IsInLoading())
            {
                StopFlash();
                wasOpen = false;
                return;
            }
            UWorld world = instance.GetWorldForCS();
            APlayerController controller = world == null ? null : UGSE_EngineFuncLib.GetFirstLocalPlayerController(world);
            BGUPlayerCharacterCS player = controller == null ? null : controller.GetControlledPawn() as BGUPlayerCharacterCS;
            BUC_PreciseDodgeData data = player == null ? null : BGU_DataUtil.GetUnPersistentReadOnlyData<BUC_PreciseDodgeData>(player);
            if (data == null)
            {
                StopFlash();
                wasOpen = false;
                return;
            }

            double now = clock.Elapsed.TotalSeconds;
            // Load the clip in the background, so the first glow has it.
            GetClip(player, ClipPath(Config.Clip), false, now);
            if (Config.ReactionSeconds > 0f)
            {
                PredictedGlows(world, player, data, now);
            }
            float length = data.PreciseDodgeTolerantTimeSeconds;
            bool open = data.CurrentPreciseDodgeTimeSeconds > 0f && length > 0f;
            if (open && !wasOpen)
            {
                OnWindowOpened(player, data, length, now);
            }
            wasOpen = open;
            if (flashing && (!ReferenceEquals(player, flashPlayer) || now - flashStart >= flashSeconds))
            {
                StopFlash();
            }
        }

        public void Remove()
        {
            StopFlash();
        }

        // A glow for each coming warning that is ReactionSeconds or less away.
        private void PredictedGlows(UWorld world, BGUPlayerCharacterCS player, BUC_PreciseDodgeData data, double now)
        {
            if (!predictor.Ready)
            {
                if (!noPredictionLogged)
                {
                    noPredictionLogged = true;
                    ModLog.Error("the attack warning notify (BANS_GSAttackWarnning) was not found: the glow comes when the window opens.");
                }
                return;
            }
            foreach (AttackPredictor.Warning warning in predictor.Collect(world, player, now))
            {
                if (warning.Seconds > Config.ReactionSeconds)
                {
                    break;
                }
                if (cued.ContainsKey(warning.Key))
                {
                    continue;
                }
                cued[warning.Key] = now;
                if (IsBlocked(player, data))
                {
                    continue;
                }
                predictedCues++;
                lastCueAt = now;
                expectedOpenAt = now + warning.Seconds;
                StartFlash(player, Seconds, predictedCues <= Reports
                    ? $"attack of {warning.Enemy.GetName()} ({warning.Montage}) in {Number(warning.Seconds)} s: glow #{predictedCues}"
                    : null, now);
            }
            if (cued.Count > 200)
            {
                List<string> old = new List<string>();
                foreach (KeyValuePair<string, double> pair in cued)
                {
                    if (now - pair.Value > 10.0)
                    {
                        old.Add(pair.Key);
                    }
                }
                foreach (string key in old)
                {
                    cued.Remove(key);
                }
            }
        }

        private void OnWindowOpened(BGUPlayerCharacterCS player, BUC_PreciseDodgeData data, float length, double now)
        {
            windows++;
            bool report = windows <= Reports || windows % 50 == 0;
            if (Config.ReactionSeconds > 0f && now - lastCueAt <= Config.ReactionSeconds + PredictionSlack)
            {
                // A predicted attack: write how early the glow really came.
                if (expectedOpenAt > 0.0)
                {
                    predictedWindows++;
                    leadSum += now - lastCueAt;
                    if (report)
                    {
                        ModLog.Info($"window #{windows}: opened {Number(now - lastCueAt)} s after the glow (predicted {Number(expectedOpenAt - lastCueAt)} s).");
                    }
                    expectedOpenAt = -1.0;
                }
            }
            else if (IsBlocked(player, data))
            {
                if (report)
                {
                    ModLog.Info($"window #{windows}: {Number(length)} s, no glow (no perfect dodge possible now).");
                }
            }
            else if (!flashing)
            {
                fallbackGlows++;
                lastCueAt = now;
                StartFlash(player, Seconds, report
                    ? $"window #{windows}: {(Config.ReactionSeconds > 0f ? "not predicted, glow now" : "glow")}"
                    : null, now);
            }
            if (windows % 20 == 0)
            {
                string lead = predictedWindows == 0 ? "-" : Number(leadSum / predictedWindows);
                ModLog.Info($"after {windows} windows: {predictedWindows} predicted (average lead {lead} s), {fallbackGlows} glows at the window.");
            }
        }

        private void StartFlash(BGUPlayerCharacterCS player, float seconds, string report, double now)
        {
            BUS_DispLibEventCollection events = BUS_DispLibEventCollection.Get(player);
            if (events == null)
            {
                if (!noEventsLogged)
                {
                    noEventsLogged = true;
                    ModLog.Info($"{player.GetName()} has no display events: no glow for this character.");
                }
                return;
            }
            string path = ClipPath(Config.Clip);
            BUC_DispLibDispBaseConfigDataAsset clip = GetClip(player, path, true, now);
            if (clip == null)
            {
                if (missingLogged.Add(path))
                {
                    ModLog.Error($"clip not found: {path}");
                }
                return;
            }
            StopFlash();
            events.Evt_RequestApplyOneDBCDataAsset(clip, out int request, player.Mesh, FVector.ZeroVector,
                DBCTransformParam.Default, DBCSetCallbackParams.Default, 0u, 0u);
            flashing = true;
            flashStart = now;
            flashSeconds = seconds;
            flashRequest = request;
            flashEvents = events;
            flashPlayer = player;
            if (report != null)
            {
                ModLog.Info($"{report}.");
            }
        }

        // Ends the clip: its timed parts end by themselves, the parts that wait for the game end now.
        private void StopFlash()
        {
            if (!flashing)
            {
                return;
            }
            flashing = false;
            try
            {
                flashEvents.Evt_RequestEndDBCEffects(flashRequest, false, true, -1, false);
            }
            catch (Exception e)
            {
                // The character is gone (a map change), and its clips with it.
                ModLog.Info($"clip end: {e.Message}");
            }
            flashEvents = null;
            flashPlayer = null;
        }

        // A clip asset from the game's asset cache. A background load is asked for at most once a second.
        private BUC_DispLibDispBaseConfigDataAsset GetClip(BGUPlayerCharacterCS player, string path, bool wait, double now)
        {
            if (clips.TryGetValue(path, out BUC_DispLibDispBaseConfigDataAsset cached) && cached != null)
            {
                return cached;
            }
            if (!wait && nextLoad.TryGetValue(path, out double next) && now < next)
            {
                return null;
            }
            nextLoad[path] = now + 1.0;
            BGW_PreloadAssetMgr loader = BGW_PreloadAssetMgr.Get(player);
            if (loader == null)
            {
                return null;
            }
            BUC_DispLibDispBaseConfigDataAsset clip = loader.TryGetCachedResourceObj<BUC_DispLibDispBaseConfigDataAsset>(path,
                wait ? ELoadResourceType.SyncLoadAndCache : ELoadResourceType.AsyncLoadAndCache, EAssetPriority.Medium);
            if (clip != null)
            {
                clips[path] = clip;
                Describe(clip);
            }
            return clip;
        }

        // Writes what a clip does into the log: its parts, and for material changes the meshes and parameters.
        private static void Describe(BUC_DispLibDispBaseConfigDataAsset clip)
        {
            try
            {
                StringBuilder text = new StringBuilder($"clip {clip.GetName()}: ");
                text.Append($"ModifyMaterial {clip.ModifyMaterial.Count}, ChangeMaterial {clip.ChangeMaterial.Count}, ");
                text.Append($"PlayArtFresnel {clip.PlayArtFresnel.Count}, PlayNiagara {clip.PlayNiagara.Count}, ");
                text.Append($"PlayAdvanceNiagara {clip.PlayAdvanceNiagara.Count}, PlayCascade {clip.PlayCascade.Count}, ");
                text.Append($"PlayAudio {clip.PlayAudio.Count}, ModifyMPCParams {clip.ModifyMPCParams.Count}");
                foreach (BUC_DispLibDBC_ModifyMaterial change in clip.ModifyMaterial)
                {
                    List<FName> tags = change.FilterMode.CompTags;
                    text.Append($"; material change ({change.FilterMode.CompsFilterMode}");
                    text.Append(tags == null || tags.Count == 0 ? "" : $" {string.Join(" ", tags.ConvertAll(t => t.ToString()).ToArray())}");
                    text.Append($", {change.EndMode}, {Number(change.Duration)} s):");
                    foreach (BUC_DispLibUtil_DBCAdvProcessScalarMatLayerParam p in change.SetScalarParam ?? new List<BUC_DispLibUtil_DBCAdvProcessScalarMatLayerParam>())
                    {
                        text.Append($" scalar {p.ParamName} ({p.Association} {Name(p.LayerFunction)})");
                    }
                    foreach (BUC_DispLibUtil_DBCPCurveScalarMatLayerParam p in change.SetPCurveScalarParam ?? new List<BUC_DispLibUtil_DBCPCurveScalarMatLayerParam>())
                    {
                        text.Append($" curve {p.ParamName} ({p.Association} {Name(p.LayerFunction)})");
                    }
                    foreach (BUC_DispLibUtil_DBCAdvProcessFLinearColorMatLayerParam p in change.SetFLinearColorParam ?? new List<BUC_DispLibUtil_DBCAdvProcessFLinearColorMatLayerParam>())
                    {
                        text.Append($" color {p.ParamName} ({p.Association} {Name(p.LayerFunction)})");
                    }
                }
                ModLog.Info(text.ToString());
            }
            catch (Exception e)
            {
                ModLog.Info($"clip {clip.GetName()}: could not read its parts ({e.Message}).");
            }
        }

        private static string Name(UObject value)
        {
            return value == null ? "-" : value.GetName();
        }

        // "Talent/DBC_x" means the clip in Wukong's clip folder; a path that starts with /Game/ stays as it is.
        private static string ClipPath(string clip)
        {
            string path = clip.StartsWith("/Game/", StringComparison.Ordinal) ? clip : ClipFolder + clip;
            int slash = path.LastIndexOf('/');
            return path.IndexOf('.', slash + 1) >= 0 ? path : $"{path}.{path.Substring(slash + 1)}";
        }

        // The game's own conditions for a perfect dodge (BUS_PreciseDodgeComp.TryPreciseDodge).
        private static bool IsBlocked(BGUPlayerCharacterCS player, BUC_PreciseDodgeData data)
        {
            if (!data.CanPreciseDodge)
            {
                return true;
            }
            BUC_SimpleStateData simple = BGU_DataUtil.GetReadOnlyData<BUC_SimpleStateData>(player);
            if (simple != null && (simple.HasSimpleState(EBGUSimpleState.CantTriggerPreciseDodge) ||
                                   simple.HasSimpleState(EBGUSimpleState.InAnimationSyncing)))
            {
                return true;
            }
            if (GSGameplayCVar.CVar_PreciseDodgeBeatbackCanTrigger.GetValueInGameThread() != 1)
            {
                BUC_UnitStateData unit = BGU_DataUtil.GetReadOnlyData<BUC_UnitStateData>(player);
                if (unit != null && unit.HasState(EBGUUnitState.Beatback))
                {
                    return true;
                }
            }
            return false;
        }

        private static string Number(double value)
        {
            return value.ToString("0.000", CultureInfo.InvariantCulture);
        }
    }
}
