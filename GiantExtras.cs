using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using Il2Cpp;
using Il2CppHexabodyVR.PlayerController;
using Il2CppHurricaneVR.Framework.Core.Player;
using Il2CppHurricaneVR.Framework.Core.ScriptableObjects;
using MelonLoader;
using UnityEngine;
using UnityEngine.Animations;

namespace GiantPlayer
{
    // Extras on top of the size change: wrist HP bars, strength x N, health x N, respawn repair.
    internal static class Extras
    {
        internal static MelonPreferences_Entry<float> Strength;
        internal static MelonPreferences_Entry<float> Health;
        internal static MelonPreferences_Entry<float> Mass;

        internal static void CreateEntries(MelonPreferences_Category cat)
        {
            Strength = cat.CreateEntry("StrengthMultiplier", 10f, "Strength x",
                "Hand force/torque limit and punch force multiplier while big");
            Health = cat.CreateEntry("HealthMultiplier", 10f, "Health x",
                "Damage taken is divided by this while big (= x health)");
            Mass = cat.CreateEntry("MassMultiplier", 4f, "Weight x",
                "Body mass multiplier while big (leg/head springs are scaled with it so the body doesn't sag)");
        }

        internal static bool Big => !Mathf.Approximately(Core.Current, 1f);

        // ---------- Wrist bars (HealthBar / SlowmoBar) ----------
        // In the scene they follow LElbowPoint through a ParentConstraint. Its offset is in metres and
        // ignores the rig scale, so at x2 the bars flew away. Most ParentConstraint calls (GetSource,
        // translationOffsets, SetTranslationOffset) are stripped icalls in this build and crash the game,
        // so we don't touch them at all: at x1 remember the bar pose relative to the elbow, then switch the
        // constraint off (plain Behaviour.enabled) and place the bars ourselves each LateUpdate at x scale.
        private class Bar { public Transform T, Src; public Behaviour Pc; public Vector3 Pos; public Quaternion Rot; }
        private static readonly Dictionary<IntPtr, Bar> Bars = new();

        internal static void SceneReset()
        {
            Bars.Clear();
            PunchOrig.Clear();
        }

        private static Transform FindChild(Transform root, string name)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (t != null && t.name == name) return t;
            return null;
        }

        // ParentConstraint data of the bars, read from the scene file (source = LElbowPoint).
        // The constraint does: pos = src.pos + src.rot * offset, rot = src.rot * rotOffset (no scale).
        private static readonly Dictionary<string, (Vector3 pos, Vector3 rot)> SceneOffsets = new()
        {
            ["HealthBar"] = (new Vector3(0.05487490f, -1.66012228f, 0.44340134f), new Vector3(0f, -90f, 0f)),
            ["SlowmoBar"] = (new Vector3(0.05487490f, -1.66912198f, 0.46640015f), new Vector3(0f, -90f, -25.37099266f)),
        };

        internal static void BeforeScale(Transform rig, bool atX1)
        {
            var stat = rig.Find("StatBars");
            var src = FindChild(rig, "LElbowPoint");
            if (stat == null || src == null) return;
            for (int i = 0; i < stat.childCount; i++)
            {
                var t = stat.GetChild(i);
                if (t == null || Bars.ContainsKey(t.Pointer)) continue;
                if (!SceneOffsets.TryGetValue(t.name, out var o)) continue;
                var pc = t.GetComponent<ParentConstraint>();
                if (pc == null) continue;
                Bars[t.Pointer] = new Bar { T = t, Src = src, Pc = pc, Pos = o.pos, Rot = Quaternion.Euler(o.rot) };
            }
        }

        internal static void AfterScale(MelonLogger.Instance log)
        {
            bool big = Big;
            foreach (var b in Bars.Values)
                if (b.Pc != null) b.Pc.enabled = !big;
            if (Bars.Count > 0) log.Msg($"Wrist bars: {Bars.Count} following the elbow at x{Core.Current}");
        }

        internal static void FollowBars()
        {
            if (Bars.Count == 0 || !Big) return;
            float s = Core.Current;
            foreach (var b in Bars.Values)
            {
                if (b.T == null || b.Src == null) continue;
                b.T.SetPositionAndRotation(b.Src.position + b.Src.rotation * (b.Pos * s), b.Src.rotation * b.Rot);
            }
        }

        // ---------- Strength ----------
        // PDStrength assets are shared and survive scene loads, so keep the true originals forever and
        // always write original * factor (never compound).
        private static readonly Dictionary<IntPtr, (float force, float torque)> StrengthOrig = new();
        private static readonly Dictionary<IntPtr, float> PunchOrig = new();

        private static void ApplyPd(PDStrength pd, float k)
        {
            if (pd == null) return;
            if (!StrengthOrig.ContainsKey(pd.Pointer)) StrengthOrig[pd.Pointer] = (pd.MaxForce, pd.MaxTorque);
            var o = StrengthOrig[pd.Pointer];
            pd.MaxForce = o.force * k;
            pd.MaxTorque = o.torque * k;
        }

        internal static void ApplyStrength(Transform rig, MelonLogger.Instance log)
        {
            float k = Big ? Mathf.Max(0.01f, Strength.Value) : 1f;
            int hands = 0;
            foreach (var h in rig.GetComponentsInChildren<HVRHexaBodyHands>(true))
            {
                if (h == null) continue;
                ApplyPd(h.Strength, k);
                ApplyPd(h.OneHandedClimbStrength, k);
                ApplyPd(h.TwoHandedClimbStrength, k);
                var sh = h.StrengthHandler;
                if (sh != null)
                {
                    ApplyPd(sh.CurrentStrength, k);
                    if (sh.CurrentStrength != null) sh.UpdateStrength(sh.CurrentStrength);
                }
                hands++;
            }
            int punches = 0;
            foreach (var p in rig.GetComponentsInChildren<PunchForceManager>(true))
            {
                if (p == null) continue;
                if (!PunchOrig.ContainsKey(p.Pointer)) PunchOrig[p.Pointer] = p.forceMultiplier;
                p.forceMultiplier = PunchOrig[p.Pointer] * k;
                punches++;
            }
            log.Msg($"Strength x{k} on {hands} hands, punch x{k} on {punches} punch components");
        }

        // ---------- Weight ----------
        // HexaBody sizes its leg spring from the body mass at Start and keeps copies of the masses and the
        // leg drive (for jumps). Scale the masses, the copies and the HexaBody joint drives by the same factor
        // so the body is heavier for everything it hits but still stands and moves the same.
        private static readonly Dictionary<IntPtr, float> MassOrig = new();
        private static readonly Dictionary<(IntPtr, int), JointDrive> DriveOrig = new();

        private static float SetMass(Rigidbody rb, float k)
        {
            if (rb == null) return 0f;
            if (!MassOrig.ContainsKey(rb.Pointer)) MassOrig[rb.Pointer] = rb.mass;
            rb.mass = MassOrig[rb.Pointer] * k;
            return rb.mass;
        }

        private static JointDrive Scaled(IntPtr owner, int slot, JointDrive cur, float k)
        {
            var key = (owner, slot);
            if (!DriveOrig.ContainsKey(key)) DriveOrig[key] = cur;
            var d = DriveOrig[key];
            d.positionSpring *= k;
            d.positionDamper *= k;
            if (!float.IsInfinity(d.maximumForce) && d.maximumForce < 1e30f) d.maximumForce *= k;
            return d;
        }

        private static void ScaleJoint(ConfigurableJoint j, float k)
        {
            if (j == null) return;
            j.xDrive = Scaled(j.Pointer, 0, j.xDrive, k);
            j.yDrive = Scaled(j.Pointer, 1, j.yDrive, k);
            j.zDrive = Scaled(j.Pointer, 2, j.zDrive, k);
            j.angularXDrive = Scaled(j.Pointer, 3, j.angularXDrive, k);
            j.angularYZDrive = Scaled(j.Pointer, 4, j.angularYZDrive, k);
            j.slerpDrive = Scaled(j.Pointer, 5, j.slerpDrive, k);
        }

        internal static void ApplyMass(HexaBodyPlayer4 b, MelonLogger.Instance log)
        {
            if (b == null) return;
            float k = Big ? Mathf.Max(0.1f, Mass.Value) : 1f;
            b._ballMass = SetMass(b.LocoBall, k);
            b._kneeMass = SetMass(b.Knee, k);
            b._headMass = SetMass(b.Head, k);
            b._pelvisMass = SetMass(b.Pelvis, k);
            ScaleJoint(b.JointLegTorso, k);
            ScaleJoint(b.JointLegBall, k);
            ScaleJoint(b.JointHead, k);
            b._legYDrive = Scaled(b.Pointer, 99, b._legYDrive, k);
            log.Msg($"Weight x{k}: body mass {b.CombinedMass:F0} kg");
        }

        // ---------- Health ----------
        // Every damage path ends in one of these methods; give back (1 - 1/N) of whatever was lost.
        internal static void Refund(PlayerHealthManager hm, float before)
        {
            if (!Big || hm == null) return;
            float n = Mathf.Max(1f, Health.Value);
            float lost = before - hm.health;
            if (lost <= 0f) return;
            hm.health = before - lost / n;
        }
    }

    [HarmonyPatch(typeof(PlayerHealthManager), nameof(PlayerHealthManager.HitByBullet))]
    internal static class HpBulletPatch
    {
        private static void Prefix(PlayerHealthManager __instance, out float __state) => __state = __instance.health;
        private static void Postfix(PlayerHealthManager __instance, float __state) => Extras.Refund(__instance, __state);
    }

    [HarmonyPatch(typeof(PlayerHealthManager), nameof(PlayerHealthManager.BittenByZombie))]
    internal static class HpBitePatch
    {
        private static void Prefix(PlayerHealthManager __instance, out float __state) => __state = __instance.health;
        private static void Postfix(PlayerHealthManager __instance, float __state) => Extras.Refund(__instance, __state);
    }

    [HarmonyPatch(typeof(PlayerHealthManager), nameof(PlayerHealthManager.HitByZombie))]
    internal static class HpZombiePatch
    {
        private static void Prefix(PlayerHealthManager __instance, out float __state) => __state = __instance.health;
        private static void Postfix(PlayerHealthManager __instance, float __state) => Extras.Refund(__instance, __state);
    }

    [HarmonyPatch(typeof(PlayerHealthManager), nameof(PlayerHealthManager.HitDetected))]
    internal static class HpMeleePatch
    {
        private static void Prefix(PlayerHealthManager __instance, out float __state) => __state = __instance.health;
        private static void Postfix(PlayerHealthManager __instance, float __state) => Extras.Refund(__instance, __state);
    }

    // On respawn the game's own resolver re-enables auto anchors and puts the body back at its x1 start
    // pose, which undoes the scaled joint anchors -> re-apply them and reset the body pose.
    [HarmonyPatch(typeof(PlayerJointsBugResolver), nameof(PlayerJointsBugResolver.ForceEnableJoints))]
    internal static class RespawnPatch
    {
        private static void Postfix() => Core.Instance?.RepairAfterRespawn();
    }
}
