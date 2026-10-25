using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using b1;
using UnrealEngine.Engine;
using UnrealEngine.Runtime;

namespace VisualDodge
{
    // Predicts when an enemy's attack warning reaches the player, so the glow can come before it. A melee attack opens
    // the player's perfect-dodge window through the notify state BANS_GSAttackWarnning in the enemy's attack montage:
    // while it runs, its warning shape is swept, and touching the player opens the window. So the time left until the
    // next warning notify of the playing montage is the time left until the window opens (at the earliest).
    //
    // For each enemy near the player that targets the player, every frame: the montage position and play rate, the
    // montage's warning notifies (read once per montage), its speed changes (BANS_GSSetAMSpeedRate), the current and the
    // next montage section, and the time dilation of the world and of the enemy. Projectiles and ground effects open the
    // window in other ways; they are not predicted.
    internal sealed class AttackPredictor
    {
        // One coming warning of one enemy.
        public struct Warning
        {
            public string Key;
            public AActor Enemy;
            public string Montage;
            public double Seconds;
        }

        private sealed class MontageInfo
        {
            public string Name;
            public readonly List<float> Warnings = new List<float>();
            // Montage time ranges with their own play rate; a rate of 0 means unknown (the base rate is used).
            public readonly List<float[]> Spans = new List<float[]>();
        }

        private sealed class EnemyState
        {
            public UAnimMontage Montage;
            public MontageInfo Info;
            public int Serial;
            public float LastPos = -1f;
            public float BaseRate = 1f;
            public double LastSeen;
        }

        private const float RangeMeters = 15f;
        private const double RefreshSeconds = 0.5;
        // Warnings further away than this are not reported.
        private const double HorizonSeconds = 3.0;
        // Two warnings closer than this in one montage are one attack (two weapons, two shapes).
        private const float SameAttackSeconds = 0.12f;
        private const int MontageLogs = 30;

        private static readonly Type WarningType = typeof(BANS_GSSetAMSpeedRate).Assembly.GetType("b1.BANS_GSAttackWarnning");
        private static readonly Type ScaleType = typeof(BANS_GSSetAMSpeedRate).Assembly.GetType("b1.BANS_GSCalcAMScale");

        private readonly Dictionary<AActor, EnemyState> states = new Dictionary<AActor, EnemyState>();
        private readonly Dictionary<string, MontageInfo> montages = new Dictionary<string, MontageInfo>();
        private readonly List<AActor> enemies = new List<AActor>();
        private double nextRefresh;
        private int montageLogs;
        private int errors;
        private bool disabled;

        public bool Ready => !disabled && WarningType != null;

        // The coming warnings of the enemies near the player, in real seconds from now, soonest first.
        public List<Warning> Collect(UWorld world, BGUPlayerCharacterCS player, double now)
        {
            List<Warning> result = new List<Warning>();
            if (!Ready)
            {
                return result;
            }
            if (now >= nextRefresh)
            {
                nextRefresh = now + RefreshSeconds;
                RefreshEnemies(world, player, now);
            }
            float dilation = 1f;
            try
            {
                dilation = Math.Max(0.01f, UGameplayStatics.GetGlobalTimeDilation(world));
            }
            catch
            {
            }
            foreach (AActor enemy in enemies)
            {
                try
                {
                    Step(enemy, player, dilation, now, result);
                }
                catch (Exception e)
                {
                    if (errors++ < 5)
                    {
                        ModLog.Error($"attack prediction: {e.GetType().Name}: {e.Message}");
                    }
                    if (errors >= 300)
                    {
                        disabled = true;
                        ModLog.Error("attack prediction: too many errors, the glow comes when the window opens from now on.");
                        return result;
                    }
                }
            }
            result.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));
            return result;
        }

        private void RefreshEnemies(UWorld world, BGUPlayerCharacterCS player, double now)
        {
            enemies.Clear();
            FVector at = player.GetActorLocation();
            double range = RangeMeters * 100.0;
            foreach (AActor actor in UGameplayStatics.GetAllActorsOfClass(world, UClass.GetClass<BGU_CharacterAI>()) ?? new AActor[0])
            {
                if (actor == null || ReferenceEquals(actor, player) || !BGUFunctionLibraryCS.BGUIsEnemyTeam(player, actor))
                {
                    continue;
                }
                FVector offset = actor.GetActorLocation() - at;
                if (offset.Size() <= range)
                {
                    enemies.Add(actor);
                }
            }
            // Forget enemies that left the range a while ago.
            List<AActor> gone = new List<AActor>();
            foreach (KeyValuePair<AActor, EnemyState> pair in states)
            {
                if (now - pair.Value.LastSeen > 10.0)
                {
                    gone.Add(pair.Key);
                }
            }
            foreach (AActor actor in gone)
            {
                states.Remove(actor);
            }
        }

        private void Step(AActor enemy, BGUPlayerCharacterCS player, float worldDilation, double now, List<Warning> result)
        {
            ACharacter character = enemy as ACharacter;
            USkeletalMeshComponent mesh = character == null ? null : character.Mesh;
            UAnimInstance anim = mesh == null ? null : mesh.GetAnimInstance();
            UAnimMontage montage = anim == null ? null : anim.GetCurrentActiveMontage();
            if (!states.TryGetValue(enemy, out EnemyState state))
            {
                state = new EnemyState();
                states[enemy] = state;
            }
            state.LastSeen = now;
            if (montage == null)
            {
                state.Montage = null;
                state.Info = null;
                return;
            }
            float pos = anim.Montage_GetPosition(montage);
            if (state.Montage == null || !ReferenceEquals(state.Montage, montage) && !montage.Equals(state.Montage))
            {
                state.Montage = montage;
                state.Info = InfoFor(montage);
                state.Serial++;
                state.LastPos = -1f;
            }
            else if (pos < state.LastPos - 0.05f)
            {
                // The same montage started again.
                state.Serial++;
            }
            state.LastPos = pos;
            MontageInfo info = state.Info;
            if (info == null || info.Warnings.Count == 0 || !TargetsPlayer(enemy, player))
            {
                return;
            }

            float rate = anim.Montage_GetPlayRate(montage);
            if (SpanAt(info, pos) == null && rate > 0.05f)
            {
                state.BaseRate = rate;
            }
            if (rate < 0.02f)
            {
                // Paused (hit-stop, frozen): no time is passing for this attack.
                return;
            }
            float dilation = worldDilation;
            try
            {
                dilation *= Math.Max(0.01f, enemy.CustomTimeDilation);
            }
            catch
            {
            }

            FName section = anim.Montage_GetCurrentSection(montage);
            bool sectionKnown = UBGUFunctionLibrary.BGUGetMontageSectionStartAndEndTime(montage, section, out float sectionStart, out float sectionEnd)
                && sectionEnd > sectionStart;
            FName next = sectionKnown ? UGSE_AnimFuncLib.GetMontageNextSection(anim, montage) : FName.None;
            float nextStart = 0f;
            float nextEnd = 0f;
            bool nextKnown = next != FName.None
                && UBGUFunctionLibrary.BGUGetMontageSectionStartAndEndTime(montage, next, out nextStart, out nextEnd)
                && nextEnd > nextStart;

            for (int i = 0; i < info.Warnings.Count; i++)
            {
                float target = info.Warnings[i];
                double seconds;
                if (!sectionKnown)
                {
                    if (target < pos)
                    {
                        continue;
                    }
                    seconds = GameSeconds(info, pos, target, rate, state.BaseRate);
                }
                else if (target >= pos && target <= sectionEnd + 0.001f)
                {
                    seconds = GameSeconds(info, pos, target, rate, state.BaseRate);
                }
                else if (nextKnown && target >= nextStart - 0.001f && target <= nextEnd + 0.001f)
                {
                    // In the section that follows (also the same section again, when it loops).
                    seconds = GameSeconds(info, pos, sectionEnd, rate, state.BaseRate) + GameSeconds(info, nextStart, target, state.BaseRate, state.BaseRate);
                }
                else
                {
                    continue;
                }
                seconds /= dilation;
                if (seconds < 0.0 || seconds > HorizonSeconds)
                {
                    continue;
                }
                result.Add(new Warning
                {
                    Key = $"{enemy.GetUniqueID()}:{state.Serial}:{i}",
                    Enemy = enemy,
                    Montage = info.Name,
                    Seconds = seconds
                });
            }
        }

        // Game seconds from montage time a to b. The first piece runs at the current rate, the rest at the rate of its
        // speed span or at the base rate.
        private static double GameSeconds(MontageInfo info, float a, float b, float currentRate, float baseRate)
        {
            double seconds = 0.0;
            float x = a;
            bool first = true;
            for (int step = 0; step < 32 && x < b - 0.00001f; step++)
            {
                float edge = Math.Min(b, NextEdge(info, x));
                float rate = baseRate;
                if (first)
                {
                    rate = currentRate;
                }
                else
                {
                    float[] span = SpanAt(info, (x + edge) * 0.5f);
                    if (span != null && span[2] > 0.01f)
                    {
                        rate = span[2];
                    }
                }
                seconds += (edge - x) / Math.Max(0.05f, rate);
                x = edge;
                first = false;
            }
            return seconds;
        }

        private static float[] SpanAt(MontageInfo info, float x)
        {
            foreach (float[] span in info.Spans)
            {
                if (x >= span[0] && x < span[1])
                {
                    return span;
                }
            }
            return null;
        }

        private static float NextEdge(MontageInfo info, float x)
        {
            float edge = float.MaxValue;
            foreach (float[] span in info.Spans)
            {
                if (span[0] > x + 0.00001f && span[0] < edge)
                {
                    edge = span[0];
                }
                if (span[1] > x + 0.00001f && span[1] < edge)
                {
                    edge = span[1];
                }
            }
            return edge;
        }

        // The enemy's lock-on target is the player; an enemy without target data counts as targeting the player.
        private static bool TargetsPlayer(AActor enemy, BGUPlayerCharacterCS player)
        {
            BUC_TargetInfoData data = BGU_DataUtil.GetReadOnlyData<BUC_TargetInfoData>(enemy);
            AActor target = data == null ? null : data.GetTargetInfo()?.LockTargetActor;
            return target == null || ReferenceEquals(target, player) || target.Equals(player);
        }

        private MontageInfo InfoFor(UAnimMontage montage)
        {
            string key = montage.GetPathName();
            if (montages.TryGetValue(key, out MontageInfo cached))
            {
                return cached;
            }
            MontageInfo info = new MontageInfo { Name = montage.GetName() };
            UBGUFuncLibSkill.BGUGetMontageNotifies(montage, out List<FAnimNotifyEvent> notifies);
            List<float> starts = new List<float>();
            foreach (FAnimNotifyEvent notify in notifies ?? new List<FAnimNotifyEvent>())
            {
                UAnimNotifyState state = notify.NotifyStateClass;
                if (state == null)
                {
                    continue;
                }
                float start = UGSE_AnimFuncLib.GetNotifyTriggerTime(notify);
                float end = Math.Max(start, UGSE_AnimFuncLib.GetNotifyEndTriggerTime(notify));
                if (WarningType.IsInstanceOfType(state))
                {
                    starts.Add(start);
                }
                else if (state is BANS_GSSetAMSpeedRate speed)
                {
                    info.Spans.Add(new[] { start, end, speed.SectionSpeedRate > 0.01f ? speed.SectionSpeedRate : 1f });
                }
                else if (ScaleType != null && ScaleType.IsInstanceOfType(state))
                {
                    info.Spans.Add(new[] { start, end, 0f });
                }
            }
            starts.Sort();
            foreach (float start in starts)
            {
                if (info.Warnings.Count == 0 || start - info.Warnings[info.Warnings.Count - 1] >= SameAttackSeconds)
                {
                    info.Warnings.Add(start);
                }
            }
            if (montages.Count > 600)
            {
                montages.Clear();
            }
            montages[key] = info;
            if (info.Warnings.Count > 0 && montageLogs++ < MontageLogs)
            {
                StringBuilder text = new StringBuilder($"attack {info.Name}: warnings at");
                foreach (float start in info.Warnings)
                {
                    text.Append(' ').Append(start.ToString("0.00", CultureInfo.InvariantCulture));
                }
                if (info.Spans.Count > 0)
                {
                    text.Append($", {info.Spans.Count} speed changes");
                }
                ModLog.Info(text.Append(" s.").ToString());
            }
            return info;
        }
    }
}
