using System;
using System.Collections;
using HarmonyLib;
using Il2Cpp;
using Il2CppHexabodyVR.PlayerController;
using Il2CppHurricaneVR.Framework.Core.Player;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;

[assembly: MelonInfo(typeof(GiantPlayer.Core), "GiantPlayer", "1.0.0", "geom2543")]
[assembly: MelonGame("Kubunautilus", "BrutalistickVR")]

namespace GiantPlayer
{
    public class Core : MelonMod
    {
        private static MelonPreferences_Category _cat;
        private static MelonPreferences_Entry<bool> _enabled;
        private static MelonPreferences_Entry<float> _scale;
        private static MelonPreferences_Entry<bool> _scaleSpeed;
        private static MelonPreferences_Entry<float> _applyDelay;
        private static MelonPreferences_Entry<float> _torqueExp;
        private static MelonPreferences_Entry<bool> _interpolate;
        private static MelonPreferences_Entry<int> _iterMul;

        // The player currently being scaled and the factor applied to it right now.
        internal static HexaBodyPlayer4 Body;
        internal static float Current = 1f;
        internal static Core Instance;

        public override void OnInitializeMelon()
        {
            _cat = MelonPreferences.CreateCategory("GiantPlayer", "Giant Player");
            _enabled = _cat.CreateEntry("Enabled", true, "Enabled", "Scale the player when a level loads");
            _scale = _cat.CreateEntry("Scale", 2f, "Scale", "Player size multiplier (2 = twice as big)");
            _scaleSpeed = _cat.CreateEntry("ScaleWalkSpeed", true, "Scale walk speed", "Walk/run speed grows with size");
            _applyDelay = _cat.CreateEntry("ApplyDelay", 1.0f, "Apply delay", "Seconds to wait after a level loads before scaling");
            _interpolate = _cat.CreateEntry("SmoothBody", true, "Smooth body",
                "Interpolate the visible body bones between physics steps (removes small visual jitter)");
            _iterMul = _cat.CreateEntry("BodySolverIterationsMultiplier", 2, "Body solver iterations x",
                "Physics solver iterations multiplier for the visible body while big (longer bones need more)");
            _torqueExp = _cat.CreateEntry("RagdollTorqueExponent", 0f, "Ragdoll torque exponent",
                "Body joint torque is multiplied by Scale^this. 0 = unchanged, 1 = x Scale, 2 = x Scale^2 (stiffer, may shake)");
            Extras.CreateEntries(_cat);
            _cat.SaveToFile(false);
            Instance = this;
            LoggerInstance.Msg($"Loaded. Scale x{_scale.Value}. F7 = reset body, F8 = toggle size, F9 = re-apply.");
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            Body = null;
            Current = 1f;
            OrigAnchors.Clear();
            OrigRb.Clear();
            Extras.SceneReset();
            if (_enabled.Value)
                MelonCoroutines.Start(ApplyWhenReady());
        }

        private IEnumerator ApplyWhenReady()
        {
            float wait = Mathf.Max(0f, _applyDelay.Value);
            float t = 0f;
            while (t < wait) { t += Time.unscaledDeltaTime; yield return null; }

            // The rig can spawn a bit later than the scene itself.
            for (int i = 0; i < 300 && FindBody() == null; i++)
                yield return null;

            var body = FindBody();
            if (body == null) yield break;
            SetScale(body, _scale.Value);
        }

        private float _diagTimer;

        // Every few seconds: how far each physics hand is from its controller and from the shoulder.
        private void LogArmDiag()
        {
            _diagTimer += Time.unscaledDeltaTime;
            if (_diagTimer < 3f) return;
            _diagTimer = 0f;
            var b = Body;
            if (b == null || !b) return;
            try
            {
                string Side(HexaHandsBase h, Transform ctrl)
                {
                    if (h == null || ctrl == null) return "-";
                    Vector3 hp = h.transform.position;
                    float toCtrl = Vector3.Distance(hp, ctrl.position);
                    float toShoulder = h.Shoulder != null ? Vector3.Distance(hp, h.Shoulder.position) : -1f;
                    float ctrlShoulder = h.Shoulder != null ? Vector3.Distance(ctrl.position, h.Shoulder.position) : -1f;
                    var j = h.Joint;
                    string tp = j != null ? j.targetPosition.magnitude.ToString("F2") : "?";
                    return $"hand-ctrl={toCtrl:F2} hand-shoulder={toShoulder:F2} ctrl-shoulder={ctrlShoulder:F2} target={tp}";
                }
                string height = "-";
                if (b.Camera != null && b.LocoBall != null && b.LocoCollider != null)
                {
                    float floorY = b.LocoBall.position.y - b.LocoCollider.radius * b.LocoCollider.transform.lossyScale.y;
                    float camY = b.Camera.position.y - floorY;
                    height = $"cam={camY:F2} hexaCamH={b.CameraHeight:F2} eye={b.EyeHeight:F2}";
                }
                LoggerInstance.Msg($"[diag x{Current}] {height} | L: {Side(b.LeftHand, b.LeftController)} | R: {Side(b.RightHand, b.RightController)}");
                LogArmJointStretch(b);
            }
            catch (Exception e) { LoggerInstance.Warning("diag: " + e.Message); _diagTimer = -1000f; }
        }

        // How far apart the two ends of each arm joint are (0 = joint holds, >0 = the arm is being pulled apart).
        // Ragdoll joint anchors as they were at x1, per joint (for the stretch diagnostic).
        internal static readonly System.Collections.Generic.Dictionary<IntPtr, (Vector3 a, Vector3 ca)> OrigAnchors = new();

        private void LogArmJointStretch(HexaBodyPlayer4 b)
        {
            Transform rig = b.transform.parent != null ? b.transform.parent : b.transform;
            var sb = new System.Text.StringBuilder();
            foreach (var j in rig.GetComponentsInChildren<ConfigurableJoint>(false))
            {
                if (j == null) continue;
                string nm = j.gameObject.name;
                if (!(nm.Contains("Shoulder") || nm.Contains("Elbow") || nm.Contains("Collarbone"))) continue;
                // Measure with the original (x1) anchors: that's where the bone ends visibly are.
                Vector3 la = j.anchor, lca = j.connectedAnchor;
                if (OrigAnchors.TryGetValue(j.Pointer, out var orig)) { la = orig.a; lca = orig.ca; }
                Vector3 a = j.transform.TransformPoint(la);
                Vector3 c = j.connectedBody != null ? j.connectedBody.transform.TransformPoint(lca) : lca;
                string to = j.connectedBody != null ? j.connectedBody.name : "world";
                sb.Append($"{nm}->{to}={Vector3.Distance(a, c):F3} ");
            }
            if (sb.Length > 0) LoggerInstance.Msg($"[stretch x{Current}] " + sb);
        }

        public override void OnLateUpdate()
        {
            try { Extras.FollowBars(); } catch { }
        }

        public override void OnUpdate()
        {
            LogArmDiag();
            WatchBody();

            Keyboard kb = null;
            try { kb = Keyboard.current; } catch { }
            if (kb == null) return;

            if (kb.f7Key.wasPressedThisFrame)
            {
                ResetBodyPose("F7");
            }
            else if (kb.f8Key.wasPressedThisFrame)
            {
                var body = FindBody();
                if (body != null)
                    SetScale(body, Mathf.Approximately(Current, 1f) ? _scale.Value : 1f);
            }
            else if (kb.f9Key.wasPressedThisFrame)
            {
                _cat.LoadFromFile(false);
                var body = FindBody();
                if (body != null) SetScale(body, _scale.Value);
            }
        }

        private static HexaBodyPlayer4 FindBody()
        {
            if (Body != null && Body.WasCollected == false && Body) return Body;
            Body = UnityEngine.Object.FindObjectOfType<HexaBodyPlayer4>();
            return Body;
        }

        private void SetScale(HexaBodyPlayer4 body, float target)
        {
            if (target <= 0.01f) return;
            float ratio = target / Current;
            if (Mathf.Approximately(ratio, 1f)) return;

            float prev = Current;
            try
            {
                // Set first: the CalibrateHeight patch reads it during Rescale.
                Current = target;
                Rescale(body, ratio);
                LoggerInstance.Msg($"Player scale set to x{target}");
            }
            catch (Exception e)
            {
                LoggerInstance.Error("Failed to scale player: " + e);
                // Don't leave the patches thinking we're big when the body isn't.
                // (only if the rig itself was never resized; otherwise keep the new value)
                Transform rig = body != null && body.transform.parent != null ? body.transform.parent : body?.transform;
                if (rig == null || Mathf.Approximately(rig.localScale.y, prev))
                    Current = prev;
            }
        }

        private void Rescale(HexaBodyPlayer4 body, float r)
        {
            // Whole player rig: HexaBody, physics hands, ragdoll body, backpack, UI, etc.
            Transform rig = body.transform.parent != null ? body.transform.parent : body.transform;
            // Extras must never stop the scaling itself (1.14.0 crashed here and left a half-scaled player).
            try { Extras.BeforeScale(rig, Mathf.Approximately(Current / r, 1f)); }
            catch (Exception e) { LoggerInstance.Warning("Wrist bars (before): " + e.Message); }

            // Scale around the bottom of the loco ball so the player stays on the ground where they stand.
            Vector3 pivot = body.transform.position;
            if (body.LocoBall != null)
            {
                pivot = body.LocoBall.position;
                if (body.LocoCollider != null)
                    pivot.y -= body.LocoCollider.radius * body.LocoCollider.transform.lossyScale.y;
            }

            rig.position = pivot + (rig.position - pivot) * r;
            rig.localScale = rig.localScale * r;

            // HexaBody tunables that are distances in world meters.
            body.EyeHeight *= r;
            body.PlayerWaistHeight *= r;
            body.BumperOffset *= r;
            body.TipToesOffset *= r;
            body.CameraCeilingOffset *= r;
            body.PelvisMaxOffset *= r;
            body.DefaultArmLength *= r;
            body.GroundedRayLength *= r;
            body.AirCrouchMaxHeight *= r;
            body.FloorOffsetAdjustment *= r;
            body.MinCameraHeight *= r;
            body.CameraMoveThreshold *= r;
            if (_scaleSpeed.Value)
            {
                body.WalkSpeed *= r;
                body.RunSpeed *= r;
            }

            BakeHexaColliders(rig, body, r);

            Physics.SyncTransforms();

            // PhysX keeps joint frames at the old size until Unity rebuilds them, and writing back the same
            // value is ignored. At x1 the arm joints hold at ~0 error; at x2 they were off by exactly the old
            // bone length - the arm was physically pulled back to its old size (the stretch + jitter).
            //  - Ragdoll joints: nudge the anchors and put the original local values back, which forces a real
            //    rebuild at the new scale. Auto-configure stays off so Unity doesn't recompute the connected
            //    anchor from the current (possibly stretched) pose.
            //  - HexaBody's own joints are re-set every frame by HexaBody; rebuilding them broke the height
            //    in 1.6.0, so they keep the plain re-set.
            //  Update after 1.7.0: rebuilding changed nothing, so the anchors are NOT scaled by the transform
            //  in this game's physics - they act as plain meters. So scale the anchor numbers themselves.
            //  (Same reason the hand target needed x scale in HandTargetPatch.)
            var hexaOwned = HexaOwnedJoints(body);
            int rebuilt = 0;
            foreach (var j in rig.GetComponentsInChildren<Joint>(true))
            {
                if (j == null) continue;
                Vector3 a = j.anchor;
                Vector3 ca = j.connectedAnchor;
                if (hexaOwned.Contains(j.Pointer))
                {
                    j.anchor = a;
                    if (!j.autoConfigureConnectedAnchor) j.connectedAnchor = ca;
                    continue;
                }
                if (!OrigAnchors.ContainsKey(j.Pointer)) OrigAnchors[j.Pointer] = (a, ca);
                j.autoConfigureConnectedAnchor = false;
                j.anchor = a * r;
                if (j.connectedBody != null) j.connectedAnchor = ca * r; // world-space when unconnected
                rebuilt++;
            }
            LoggerInstance.Msg($"Scaled anchors of {rebuilt} ragdoll joints");

            ScaleArms(rig, body, r);
            ScaleBodyIK(rig, r);
            ScaleRagdollJoints(rig, body, r);
            SmoothBody(rig);
            CaptureBodyPose(rig);
            try { Extras.ApplyMass(body, LoggerInstance); }
            catch (Exception e) { LoggerInstance.Warning("Weight: " + e.Message); }
            try { Extras.ApplyStrength(rig, LoggerInstance); }
            catch (Exception e) { LoggerInstance.Warning("Strength: " + e.Message); }
            try { Extras.AfterScale(LoggerInstance); }
            catch (Exception e) { LoggerInstance.Warning("Wrist bars: " + e.Message); }

            // Let HexaBody rebuild camera mapping and arm limits with the new sizes.
            body.CalibrateHeight(body.CalibratedHeight);
            body.SetArmLength(body.DefaultArmLength);

            foreach (var rb in rig.GetComponentsInChildren<Rigidbody>(true))
            {
                if (rb == null || rb.isKinematic) continue;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
        }

        // HexaBody reads/writes its collider sizes (loco ball radius, leg capsule height, ...) as if they
        // were world meters - e.g. the ground check casts from "ball center - radius". Under a scaled
        // rig those local numbers are wrong by the scale factor, so the player never counts as grounded.
        // Fix: remove the scale from those collider transforms and put it into the collider sizes instead,
        // so local size == world size again.
        private void BakeHexaColliders(Transform rig, HexaBodyPlayer4 body, float r)
        {
            var cols = new Collider[]
            {
                body.LocoCollider, body.FenderCollider, body.KneeCollider,
                body.ChestCollider, body.NeckCollider, body.PelvisCollider
            };
            var done = new System.Collections.Generic.HashSet<IntPtr>();
            var allJoints = rig.GetComponentsInChildren<Joint>(true);
            string names = "";

            foreach (var c in cols)
            {
                if (c == null) continue;
                Transform t = c.transform;
                if (!done.Add(t.Pointer)) continue;
                names += t.name + " ";

                t.localScale = t.localScale / r;
                // keep children (debug shapes etc.) where they were
                for (int i = 0; i < t.childCount; i++)
                {
                    var ch = t.GetChild(i);
                    ch.localPosition = ch.localPosition * r;
                    ch.localScale = ch.localScale * r;
                }

                foreach (var col in t.GetComponents<Collider>())
                {
                    var sc = col.TryCast<SphereCollider>();
                    if (sc != null) { sc.radius *= r; sc.center *= r; continue; }
                    var cc = col.TryCast<CapsuleCollider>();
                    if (cc != null) { cc.radius *= r; cc.height *= r; cc.center *= r; continue; }
                    var bc = col.TryCast<BoxCollider>();
                    if (bc != null) { bc.size *= r; bc.center *= r; }
                }

                // Joint anchors are in local space of the transform we just un-scaled.
                var rb = t.GetComponent<Rigidbody>();
                foreach (var j in allJoints)
                {
                    if (j == null) continue;
                    if (j.transform.Pointer == t.Pointer) j.anchor = j.anchor * r;
                    if (rb != null && j.connectedBody != null && j.connectedBody.Pointer == rb.Pointer
                        && !j.autoConfigureConnectedAnchor)
                        j.connectedAnchor = j.connectedAnchor * r;
                }
            }

            // Cached "original" radii are used to restore the balls after jumps/crouch.
            body._originalLocoRadius *= r;
            body._originalBumperRadius *= r;
            LoggerInstance.Msg("Baked scale into colliders: " + names);
        }

        // Physics hands clamp to the controller with world-meter limits. Left unscaled, the controller
        // ends up past the arm length / max distance and the hand keeps snapping back (the jitter).
        private void ScaleArms(Transform rig, HexaBodyPlayer4 body, float r)
        {
            int n = 0;
            foreach (var h in rig.GetComponentsInChildren<HVRJointHand>(true))
            {
                if (h == null) continue;
                h.ArmLength *= r;
                h.MaxTargetDistance *= r;
                n++;
            }

            var hexaHands = new System.Collections.Generic.List<HexaHandsBase>();
            foreach (var h in rig.GetComponentsInChildren<HexaHandsBase>(true))
                if (h != null) hexaHands.Add(h);
            // HexaBody's hand refs can live outside the rig in some scenes.
            if (body.LeftHand != null && !hexaHands.Exists(x => x.Pointer == body.LeftHand.Pointer)) hexaHands.Add(body.LeftHand);
            if (body.RightHand != null && !hexaHands.Exists(x => x.Pointer == body.RightHand.Pointer)) hexaHands.Add(body.RightHand);
            foreach (var h in hexaHands)
            {
                // ArmLength clamps a local-space target (see HandTargetPatch), so it stays as is:
                // the rig scale already stretches it in world space.
                h.DistanceThreshold *= r;
                h.DownDistanceThreshold *= r;
                // stuck detection compares joint forces, which grow with size too
                h.StuckForce *= r;
                h.DownMaxAllowedForce *= r;
                n++;
            }
            LoggerInstance.Msg($"Scaled {n} hand components");
        }

        // The stick body is posed from the camera with world-meter offsets and curves.
        // Original settings of the visible body's rigidbodies, to restore at x1.
        private static readonly System.Collections.Generic.Dictionary<IntPtr, (RigidbodyInterpolation interp, int it, int vit)> OrigRb = new();

        // Remaining small jitter: bones move twice as far per physics step at x2, so stepping between
        // physics ticks is visible, and longer joint chains need more solver iterations to settle.
        // Only touches the visible stick body (PhysicalUpperbody), not HexaBody or the physics hands.
        private void SmoothBody(Transform rig)
        {
            bool big = !Mathf.Approximately(Current, 1f);
            int n = 0;
            foreach (var rb in rig.GetComponentsInChildren<Rigidbody>(true))
            {
                if (rb == null || !UnderVisibleBody(rb.transform)) continue;
                if (!OrigRb.ContainsKey(rb.Pointer))
                    OrigRb[rb.Pointer] = (rb.interpolation, rb.solverIterations, rb.solverVelocityIterations);
                var o = OrigRb[rb.Pointer];
                if (big)
                {
                    if (_interpolate.Value) rb.interpolation = RigidbodyInterpolation.Interpolate;
                    int m = Math.Max(1, _iterMul.Value);
                    rb.solverIterations = o.it * m;
                    rb.solverVelocityIterations = o.vit * m;
                }
                else
                {
                    rb.interpolation = o.interp;
                    rb.solverIterations = o.it;
                    rb.solverVelocityIterations = o.vit;
                }
                n++;
            }
            // Physics hands too: they move twice as far per physics step at x2.
            var b = Body;
            if (b != null)
            {
                foreach (var h in new[] { b.LeftHand, b.RightHand })
                {
                    if (h == null || h.RigidBody == null) continue;
                    var rb = h.RigidBody;
                    if (!OrigRb.ContainsKey(rb.Pointer))
                        OrigRb[rb.Pointer] = (rb.interpolation, rb.solverIterations, rb.solverVelocityIterations);
                    rb.interpolation = big && _interpolate.Value ? RigidbodyInterpolation.Interpolate : OrigRb[rb.Pointer].interp;
                }
            }
            LoggerInstance.Msg($"Smoothed {n} body bones + hands (interpolate={_interpolate.Value && big}, iterations x{(big ? _iterMul.Value : 1)})");
            LogPhysicsSummary(rig);
        }

        // ---- Body recovery (Meta recenter etc.) ----
        // A recenter teleports the camera and controllers; the hands jump after them (twice as far at x2),
        // the locked arm chain can't follow, gets tangled and the body thrashes forever. When the locked
        // arm joints stay pulled apart, snap the visible body back to a known good pose.
        private struct BonePose { public Rigidbody rb; public Transform t; public Vector3 lp; public Quaternion lr; }
        private static readonly System.Collections.Generic.List<BonePose> RestPose = new();
        private static readonly System.Collections.Generic.List<ConfigurableJoint> LockedArmJoints = new();
        private float _badTime, _cooldown;

        private void CaptureBodyPose(Transform rig)
        {
            RestPose.Clear();
            LockedArmJoints.Clear();
            if (Mathf.Approximately(Current, 1f)) return;
            foreach (var rb in rig.GetComponentsInChildren<Rigidbody>(true))
            {
                if (rb == null || !UnderVisibleBody(rb.transform)) continue;
                RestPose.Add(new BonePose { rb = rb, t = rb.transform, lp = rb.transform.localPosition, lr = rb.transform.localRotation });
            }
            foreach (var j in rig.GetComponentsInChildren<ConfigurableJoint>(true))
            {
                if (j == null || !UnderVisibleBody(j.transform)) continue;
                string nm = j.gameObject.name;
                if (!(nm.Contains("Shoulder") || nm.Contains("Elbow") || nm.Contains("Collarbone"))) continue;
                if (j.xMotion != ConfigurableJointMotion.Locked) continue;
                LockedArmJoints.Add(j);
            }
        }

        private float MaxLockedStretch()
        {
            float max = 0f;
            foreach (var j in LockedArmJoints)
            {
                if (j == null || !j.gameObject.activeInHierarchy) continue;
                Vector3 la = j.anchor, lca = j.connectedAnchor;
                if (OrigAnchors.TryGetValue(j.Pointer, out var o)) { la = o.a; lca = o.ca; }
                Vector3 a = j.transform.TransformPoint(la);
                Vector3 c = j.connectedBody != null ? j.connectedBody.transform.TransformPoint(lca) : lca;
                max = Mathf.Max(max, Vector3.Distance(a, c));
            }
            return max;
        }

        private void WatchBody()
        {
            if (Mathf.Approximately(Current, 1f) || LockedArmJoints.Count == 0) return;
            float dt = Time.unscaledDeltaTime;
            if (_cooldown > 0f) { _cooldown -= dt; return; }
            // 0.08 m at x2. Healthy play stays ~0.01-0.05; after a recenter the body sat at 0.1-0.18 forever.
            float limit = 0.04f * Current;
            float m;
            try { m = MaxLockedStretch(); } catch { return; }
            _badTime = m > limit ? _badTime + dt : 0f;
            if (_badTime > 0.75f) ResetBodyPose($"arm joints pulled apart by {m:F2} m");
        }

        // Called after the game's own respawn resolver re-enabled the body joints.
        internal void RepairAfterRespawn()
        {
            if (Mathf.Approximately(Current, 1f)) return;
            var b = Body;
            if (b == null) return;
            Transform rig = b.transform.parent != null ? b.transform.parent : b.transform;
            int n = 0;
            foreach (var j in rig.GetComponentsInChildren<Joint>(true))
            {
                if (j == null || !OrigAnchors.TryGetValue(j.Pointer, out var o)) continue;
                j.autoConfigureConnectedAnchor = false;
                j.anchor = o.a * Current;
                if (j.connectedBody != null) j.connectedAnchor = o.ca * Current;
                n++;
            }
            _cooldown = 2f;
            LoggerInstance.Msg($"Respawn: re-applied scaled anchors on {n} joints");
        }

        private void ResetBodyPose(string why)
        {
            _badTime = 0f;
            _cooldown = 1f;
            if (RestPose.Count == 0) return;
            foreach (var p in RestPose)
            {
                if (p.t == null || p.rb == null) continue;
                p.t.localPosition = p.lp;
                p.t.localRotation = p.lr;
                if (!p.rb.isKinematic)
                {
                    p.rb.linearVelocity = Vector3.zero;
                    p.rb.angularVelocity = Vector3.zero;
                }
            }
            Physics.SyncTransforms();
            LoggerInstance.Msg($"Body reset ({why})");
        }

        // One-time summary to find visual stepping: physics step and which bodies are smoothed.
        private void LogPhysicsSummary(Transform rig)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.Append($"fixedDt={Time.fixedDeltaTime:F4} ");
                var b = Body;
                void Rb(string label, Rigidbody rb)
                {
                    if (rb == null) { sb.Append($"{label}=null "); return; }
                    sb.Append($"{label}[{rb.interpolation},kin={rb.isKinematic},m={rb.mass:F1},it={rb.solverIterations}] ");
                }
                if (b != null)
                {
                    Rb("pelvis", b.Pelvis); Rb("head", b.Head); Rb("knee", b.Knee); Rb("loco", b.LocoBall);
                    if (b.LeftHand != null) Rb("handL", b.LeftHand.RigidBody);
                    if (b.RightHand != null) Rb("handR", b.RightHand.RigidBody);
                }
                foreach (var rb in rig.GetComponentsInChildren<Rigidbody>(true))
                {
                    if (rb == null) continue;
                    string nm = rb.name;
                    if (nm == "PlayerUpperChest" || nm == "CollarboneR" || nm == "RShoulderPoint" || nm == "RElbowPoint" || nm == "Chest" || nm == "PlayerPelvis")
                        Rb(nm, rb);
                }
                LoggerInstance.Msg("[physics] " + sb);
            }
            catch (Exception e) { LoggerInstance.Warning("physics summary: " + e.Message); }
        }

        private static bool UnderVisibleBody(Transform t)
        {
            for (var p = t; p != null; p = p.parent)
                if (p.name == "PhysicalUpperbody") return true;
            return false;
        }

        // Joints driven every frame by HexaBody itself - leave them to HexaBody / our patches.
        private static System.Collections.Generic.HashSet<IntPtr> HexaOwnedJoints(HexaBodyPlayer4 body)
        {
            var set = new System.Collections.Generic.HashSet<IntPtr>();
            void Add(Joint j) { if (j != null) set.Add(j.Pointer); }
            Add(body.JointLegTorso); Add(body.JointLegBall); Add(body.JointHead);
            Add(body._leftArmJoint); Add(body._rightArmJoint);
            if (body.LeftHand != null) Add(body.LeftHand.Joint);
            if (body.RightHand != null) Add(body.RightHand.Joint);
            return set;
        }

        // The visible stick body (collarbone -> shoulder -> elbow -> hand) is a chain of ConfigurableJoints.
        // Anchors follow the transform scale, but limits, projection distance and drive strengths are
        // absolute numbers. Uniform physics scaling with the same masses: distances x r, linear forces x r,
        // torques x r^2 (bones are r times longer, so inertia is r^2 bigger).
        // Projection distance is the big one: it's the error after which PhysX snaps a bone back into
        // place, and with x2 bones normal error crosses it constantly -> the arm jitters.
        private void ScaleRagdollJoints(Transform rig, HexaBodyPlayer4 body, float r)
        {
            var skip = HexaOwnedJoints(body);
            // Torque multiplier = r^exponent. Default 0 (unchanged): some bodies (chest, hands) have a
            // hand-set inertia that doesn't grow with size, and x4 torque made the arms shake.
            float r2 = Mathf.Pow(r, _torqueExp.Value);
            int n = 0;
            foreach (var j in rig.GetComponentsInChildren<ConfigurableJoint>(true))
            {
                if (j == null || skip.Contains(j.Pointer)) continue;

                var ll = j.linearLimit; ll.limit *= r; ll.contactDistance *= r; j.linearLimit = ll;
                j.projectionDistance *= r;

                j.xDrive = ScaleDrive(j.xDrive, 1f, r);
                j.yDrive = ScaleDrive(j.yDrive, 1f, r);
                j.zDrive = ScaleDrive(j.zDrive, 1f, r);
                j.angularXDrive = ScaleDrive(j.angularXDrive, r2, r2);
                j.angularYZDrive = ScaleDrive(j.angularYZDrive, r2, r2);
                j.slerpDrive = ScaleDrive(j.slerpDrive, r2, r2);

                if (!float.IsInfinity(j.breakForce)) j.breakForce *= r;
                if (!float.IsInfinity(j.breakTorque)) j.breakTorque *= r2;
                n++;
            }
            LoggerInstance.Msg($"Scaled {n} ragdoll joints");
        }

        // Linear drive: F = k*x and x grows by r, so k stays, only the force cap grows.
        // Angular drive: angle stays, needed torque grows by r^2, so spring, damper and cap grow by r^2.
        private static JointDrive ScaleDrive(JointDrive d, float springDamperScale, float maxScale)
        {
            d.positionSpring *= springDamperScale;
            d.positionDamper *= springDamperScale;
            if (!float.IsInfinity(d.maximumForce) && d.maximumForce < 1e30f) d.maximumForce *= maxScale;
            return d;
        }

        private void ScaleBodyIK(Transform rig, float r)
        {
            foreach (var ik in rig.GetComponentsInChildren<PlayerBodyIK>(true))
            {
                if (ik == null) continue;
                ik.pelvisOffsetFromCamera = ik.pelvisOffsetFromCamera * r;
                ik.cameraToUpperChestDistance *= r;
                ik.cameraToPelvisDistance *= r;
                // height above bumper (m) -> pelvis height (m): both axes are world meters
                ScaleCurve(ik.pelvisHeightFromBumperCurve, r, r);
                // camera-bumper distance (m) -> local position under the scaled rig: input only
                ScaleCurve(ik.middleChestHeightFromPelvisCurve, r, 1f);
            }
        }

        private static void ScaleCurve(AnimationCurve curve, float timeScale, float valueScale)
        {
            if (curve == null) return;
            var keys = curve.keys;
            if (keys == null) return;
            float slope = valueScale / timeScale;
            for (int i = 0; i < keys.Length; i++)
            {
                Keyframe k = keys[i];
                k.time *= timeScale;
                k.value *= valueScale;
                k.inTangent *= slope;
                k.outTangent *= slope;
                keys[i] = k;
            }
            curve.keys = keys;
        }
    }

    // HexaBody positions the tracking space under the pelvis with a floor offset in world meters
    // (eye height, waist height, crouch...). HexaCameraRig writes it into FloorOffsetTransform.localPosition,
    // which sits inside the scaled rig, so the offset got doubled a second time: the camera (head + controllers)
    // ended up at the wrong height relative to the body, and the arms stretched/jittered between them.
    internal static class FloorOffsetFix
    {
        internal static void Apply(HexaCameraRig cam)
        {
            float s = Core.Current;
            if (cam == null || Mathf.Approximately(s, 1f)) return;
            var b = Core.Body;
            if (b == null || b.CameraRig == null || b.CameraRig.Pointer != cam.Pointer) return;
            var t = cam.FloorOffsetTransform;
            if (t == null) return;
            Vector3 lp = t.localPosition;
            lp.y = cam._floorOffset / s + cam.CameraYOffset;
            t.localPosition = lp;
        }
    }

    [HarmonyPatch(typeof(HexaBodyPlayer4), nameof(HexaBodyPlayer4.ApplyCameraOffsets))]
    internal static class ApplyCameraOffsetsPatch
    {
        private static void Postfix(HexaBodyPlayer4 __instance) => FloorOffsetFix.Apply(__instance.CameraRig);
    }

    [HarmonyPatch(typeof(HexaCameraRig), nameof(HexaCameraRig.UpdateFloorOffset))]
    internal static class UpdateFloorOffsetPatch
    {
        private static void Postfix(HexaCameraRig __instance) => FloorOffsetFix.Apply(__instance);
    }

    [HarmonyPatch(typeof(HexaCameraRig), nameof(HexaCameraRig.Setup))]
    internal static class CameraRigSetupPatch
    {
        private static void Postfix(HexaCameraRig __instance) => FloorOffsetFix.Apply(__instance);
    }

    [HarmonyPatch(typeof(HexaCameraRig), nameof(HexaCameraRig.FloorOffset), MethodType.Setter)]
    internal static class FloorOffsetSetterPatch
    {
        private static void Postfix(HexaCameraRig __instance) => FloorOffsetFix.Apply(__instance);
    }

    // HandleHMDMovement: when the head walks away from the body in the room, HexaBody moves the body by
    // that world distance and shifts the CameraRig back by the same number written into localPosition.
    // Inside the x2 rig that local shift is x2 in the world, so the camera overshot to the other side of
    // the body every time -> camera and controllers ping-ponged left/right (the hand shake), and the big
    // jump from a Meta recenter blew the body apart. Divide the camera shift by the scale.
    [HarmonyPatch(typeof(HexaBodyPlayer4), nameof(HexaBodyPlayer4.HandleHMDMovement))]
    internal static class HmdMovementPatch
    {
        private static void Prefix(HexaBodyPlayer4 __instance, out Vector3 __state)
        {
            __state = Vector3.zero;
            var rig = __instance.CameraRig;
            if (rig != null) __state = rig.transform.localPosition;
        }

        private static void Postfix(HexaBodyPlayer4 __instance, Vector3 __state)
        {
            float s = Core.Current;
            if (Mathf.Approximately(s, 1f)) return;
            if (Core.Body == null || __instance.Pointer != Core.Body.Pointer) return;
            var rig = __instance.CameraRig;
            if (rig == null) return;
            var t = rig.transform;
            Vector3 after = t.localPosition;
            Vector3 shift = after - __state;
            if (shift.sqrMagnitude < 1e-12f) return;
            t.localPosition = __state + shift / s;
        }
    }

    // HexaBody hands drive their joint to the controller with targetPosition = the controller in the
    // chest's LOCAL space. PhysX applies that number as-is in meters, ignoring the rig scale, so under
    // a x2 rig the hand only reaches half way to the controller - the arm "doesn't get longer".
    // Convert the target (and its velocity) back to world-sized numbers.
    [HarmonyPatch(typeof(HexaHandsBase), nameof(HexaHandsBase.UpdateJointAnchors))]
    internal static class HandTargetPatch
    {
        private static void Postfix(HexaHandsBase __instance)
        {
            float s = Core.Current;
            if (Mathf.Approximately(s, 1f)) return;
            var j = __instance.Joint;
            if (j == null) return;
            j.targetPosition = j.targetPosition * s;

            // Moving a hand twice as far in the same time needs twice the force, but the drive's force cap
            // stayed the same, so the hand kept hitting it and twitching. The strength handler can rewrite the
            // drives at any time, so remember what we wrote and only scale fresh values (no compounding).
            j.xDrive = ScaleCap(j, 0, j.xDrive, s);
            j.yDrive = ScaleCap(j, 1, j.yDrive, s);
            j.zDrive = ScaleCap(j, 2, j.zDrive, s);
        }

        private static readonly System.Collections.Generic.Dictionary<(IntPtr, int), float> Written = new();

        private static JointDrive ScaleCap(ConfigurableJoint j, int axis, JointDrive d, float s)
        {
            float cur = d.maximumForce;
            if (float.IsInfinity(cur) || cur > 1e30f) return d;
            var key = (j.Pointer, axis);
            if (Written.TryGetValue(key, out float w) && Mathf.Approximately(w, cur)) return d;
            d.maximumForce = cur * s;
            Written[key] = d.maximumForce;
            return d;
        }
    }

    [HarmonyPatch(typeof(HexaHandsBase), nameof(HexaHandsBase.UpdateTargetVelocity))]
    internal static class HandVelocityPatch
    {
        private static void Postfix(HexaHandsBase __instance)
        {
            float s = Core.Current;
            if (Mathf.Approximately(s, 1f)) return;
            var j = __instance.Joint;
            if (j == null) return;
            // The game adds the body's own HMD velocity (already in world meters) to the local-space
            // controller velocity. Only the local part needs x scale; scaling everything made the hand
            // overshoot and shake.
            Vector3 bodyTerm = Vector3.zero;
            var hexa = __instance.HexaBody;
            var parent = __instance.ParentRigidBody;
            if (hexa != null && parent != null) bodyTerm = parent.transform.InverseTransformDirection(hexa.HMDSpeed);
            j.targetVelocity = j.targetVelocity * s - bodyTerm * (s - 1f);
        }
    }

    // The neck cylinder's localScale.y is set to a world distance, but it sits inside the scaled rig,
    // so it would come out stretched by the scale factor. Undo that.
    [HarmonyPatch(typeof(PlayerBodyIK), nameof(PlayerBodyIK.SetNeckRotation))]
    internal static class NeckPatch
    {
        private static void Postfix(PlayerBodyIK __instance)
        {
            float s = Core.Current;
            if (Mathf.Approximately(s, 1f)) return;
            var neck = __instance.neckCylinderTransform;
            if (neck == null) return;
            Vector3 ls = neck.localScale;
            ls.y /= s;
            neck.localScale = ls;
        }
    }

    // CalibrateHeight sets CameraScale = EyeHeight / realHeight. The rig transform already scales the
    // camera, so run the game's math with the unscaled eye height, then convert the results back to
    // the scaled world: CameraScale stays vanilla, internal height numbers are multiplied.
    [HarmonyPatch(typeof(HexaBodyPlayer4), nameof(HexaBodyPlayer4.CalibrateHeight))]
    internal static class CalibrateHeightPatch
    {
        private static void Prefix(HexaBodyPlayer4 __instance, out float __state)
        {
            __state = 1f;
            if (Core.Body == null || __instance.Pointer != Core.Body.Pointer) return;
            float s = Core.Current;
            if (Mathf.Approximately(s, 1f)) return;
            __state = s;
            __instance.EyeHeight /= s;
        }

        private static void Postfix(HexaBodyPlayer4 __instance, float __state)
        {
            float s = __state;
            if (Mathf.Approximately(s, 1f)) return;
            __instance.EyeHeight *= s;
            __instance._scale *= s;
            __instance.SittingOffset *= s;
        }
    }
}
