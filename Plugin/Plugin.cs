using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using Talesshop;
using HighlightPlus;

namespace SaniyangFreeCamMod
{
    [BepInPlugin("com.saniyang.freecam", "Saniyang FreeCam", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Logger.LogInfo("Saniyang FreeCam starting (Harmony + URP render-hook)...");
            try
            {
                new Harmony("com.saniyang.freecam").PatchAll();
                Logger.LogInfo("Harmony patches applied.");
            }
            catch (Exception e) { Logger.LogError($"Patch failed: {e}"); }

            // URP 렌더 직전 콜백 — 모든 LateUpdate 후라 카메라 pose를 마지막에 강제할 수 있음
            try
            {
                RenderPipelineManager.beginCameraRendering += FreeCam.OnBeginCameraRendering;
                Logger.LogInfo("beginCameraRendering hook registered.");
            }
            catch (Exception e) { Logger.LogError($"Render hook failed: {e}"); }

            Logger.LogInfo("F7=FreeCam  F8=Fog  F9=캐릭터하이라이트  F10=UI숨기기  F5=시간정지  F6=HUD숨기기");
        }
    }

    internal static class FreeCam
    {
        public static bool Active;
        public static bool FogOff;
        public static bool HighlightOff;
        public static bool UiHidden;
        public static bool TimeStopped;
        private static float _origTimeScale;
        private static readonly List<Canvas> _hiddenCanvases = new List<Canvas>();
        private static readonly List<HighlightEffect> _suspendedHighlightEffects = new List<HighlightEffect>();

        public static Camera Cam;
        private static Transform CamT => Cam != null ? Cam.transform : null;

        private static readonly List<Behaviour> Suspended = new List<Behaviour>();

        private static Vector3 _pos;
        private static float _yaw, _pitch;
        private static float _speed = 8f;
        private static int _lastInputFrame = -1;

        // 프리캠 시작 시점 pose (R키 복귀용)
        private static Vector3 _homePos;
        private static float _homeYaw, _homePitch;

        private static float _origFar;
        private static bool _origFog;
        private static readonly List<object[]> _origFogOverrideActive = new List<object[]>();

        private static Type FindType(string fullName)
        {
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = a.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }

        // Volume 프로필 안의 개별 override 컴포넌트 목록 (profile.components 리스트)
        private static System.Collections.IEnumerable GetVolumeComponents(object volume, PropertyInfo profileProp)
        {
            var profile = profileProp.GetValue(volume, null);
            var cf = profile?.GetType().GetField("components", BindingFlags.Public | BindingFlags.Instance)
                  ?? profile?.GetType().BaseType?.GetField("components", BindingFlags.Public | BindingFlags.Instance);
            return cf?.GetValue(profile) as System.Collections.IEnumerable;
        }

        private static Text _hud;
        private static bool _hudHidden;

        // ====== per-frame: 입력/토글/이동 계산 (게임 카메라 컨트롤러 Update postfix) ======
        public static void Tick(EasyTouchCameraRewiredInput inst)
        {
            if (_lastInputFrame == Time.frameCount) return; // 프레임당 1회
            _lastInputFrame = Time.frameCount;

            if (Input.GetKeyDown(KeyCode.F7) || Input.GetKeyDown(KeyCode.Backslash))
            {
                if (Active) Deactivate(); else Activate(inst);
            }
            if (Input.GetKeyDown(KeyCode.F8) || Input.GetKeyDown(KeyCode.LeftBracket)) ToggleFog();
            if (Input.GetKeyDown(KeyCode.F9) || Input.GetKeyDown(KeyCode.RightBracket)) ToggleHighlight();
            if (Input.GetKeyDown(KeyCode.F10) || Input.GetKeyDown(KeyCode.Delete)) ToggleUI();
            if (Input.GetKeyDown(KeyCode.F5) || Input.GetKeyDown(KeyCode.Backspace)) ToggleTime();
            if (Input.GetKeyDown(KeyCode.F6) || Input.GetKeyDown(KeyCode.H))
            {
                _hudHidden = !_hudHidden;
                UpdateHud();
            }

            // R = 프리캠 시작 위치로 복귀
            if (Active && (Input.GetKeyDown(KeyCode.R) || Input.GetKeyDown(KeyCode.Home)))
            {
                _pos = _homePos; _yaw = _homeYaw; _pitch = _homePitch;
                L("시작 위치 복귀");
                ShowHud();
            }

            // 하이라이트 꺼둔 동안, 나중에 새로 켜지거나 인스턴스화되는 HighlightEffect도 계속 억제
            if (HighlightOff) SuppressHighlightEffects();

            if (Active && CamT != null) ComputeMovement();
            UpdateHud();
        }

        private static void ComputeMovement()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f) dt = 0.016f;

            // 우클릭 안 눌러도 항상 시점 조작 (fly-cam처럼 커서 잠금)
            _yaw += Input.GetAxisRaw("Mouse X") * 2.5f;
            _pitch -= Input.GetAxisRaw("Mouse Y") * 2.5f;
            _pitch = Mathf.Clamp(_pitch, -89f, 89f);

            float rs = 70f * dt;
            if (Input.GetKey(KeyCode.LeftArrow)) _yaw -= rs;
            if (Input.GetKey(KeyCode.RightArrow)) _yaw += rs;
            if (Input.GetKey(KeyCode.UpArrow)) _pitch = Mathf.Clamp(_pitch - rs, -89f, 89f);
            if (Input.GetKey(KeyCode.DownArrow)) _pitch = Mathf.Clamp(_pitch + rs, -89f, 89f);

            float wheel = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(wheel) > 0.001f)
            {
                _speed = Mathf.Clamp(_speed * (1f + wheel * 4f), 0.5f, 1000f);
                ShowHud();
            }

            Quaternion rot = Quaternion.Euler(_pitch, _yaw, 0f);
            Vector3 fwd = rot * Vector3.forward;
            Vector3 right = rot * Vector3.right;

            Vector3 dir = Vector3.zero;
            if (Input.GetKey(KeyCode.W)) dir += fwd;
            if (Input.GetKey(KeyCode.S)) dir -= fwd;
            if (Input.GetKey(KeyCode.D)) dir += right;
            if (Input.GetKey(KeyCode.A)) dir -= right;
            if (Input.GetKey(KeyCode.Space)) dir += Vector3.up;
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl) || Input.GetKey(KeyCode.C)) dir += Vector3.down;

            if (dir.sqrMagnitude > 0.0001f)
            {
                float mult = (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) ? 4f : 1f;
                _pos += dir.normalized * _speed * mult * dt;
            }

            // 즉시 1차 적용 (render hook 실패 대비)
            CamT.SetPositionAndRotation(_pos, rot);
        }

        // ====== URP 렌더 직전: 카메라 pose 최종 강제 (boundary·다른 LateUpdate 전부 무시) ======
        public static void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            if (!Active || cam == null || cam != Cam) return;
            cam.transform.SetPositionAndRotation(_pos, Quaternion.Euler(_pitch, _yaw, 0f));
        }

        // ====== activate / deactivate ======
        private static void Activate(EasyTouchCameraRewiredInput inst)
        {
            Cam = Camera.main;
            if (Cam == null)
                foreach (var c in Camera.allCameras)
                    if (c != null && c.isActiveAndEnabled) { Cam = c; break; }
            if (Cam == null) { L("카메라 못 찾음"); return; }

            Suspended.Clear();

            // CameraPerspective(touchCameraPro) 끄기 — 떨림 방지 (pose는 render hook이 강제)
            try
            {
                var f = typeof(EasyTouchCameraRewiredInput).GetField("touchCameraPro", BindingFlags.Public | BindingFlags.Instance);
                var tcp = f?.GetValue(inst) as Behaviour;
                if (tcp != null && tcp.enabled) { tcp.enabled = false; Suspended.Add(tcp); }
            }
            catch { }
            foreach (var c in Cam.GetComponents<Behaviour>())
            {
                if (c == null || c is Camera || !c.enabled) continue;
                string n = c.GetType().Name;
                if (n.Contains("Handheld") || n.Contains("Shake") || n.Contains("CameraPerspective")
                    || n.Contains("Boundaries") || n.Contains("Follow"))
                { c.enabled = false; Suspended.Add(c); }
            }

            // far clip plane 키워서 멀리 가도 안 짤리게 (끌 때 원복)
            _origFar = Cam.farClipPlane;
            Cam.farClipPlane = Mathf.Max(Cam.farClipPlane, 10000f);

            _pos = CamT.position;
            Vector3 e = CamT.rotation.eulerAngles;
            _pitch = e.x > 180f ? e.x - 360f : e.x;
            _pitch = Mathf.Clamp(_pitch, -89f, 89f);
            _yaw = e.y;

            // 시작 pose 저장 (R키 복귀용)
            _homePos = _pos; _homeYaw = _yaw; _homePitch = _pitch;

            Active = true;
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
            L($"FreeCam ON (suspended {Suspended.Count} comps, far={Cam.farClipPlane})");
            ShowHud();
        }

        private static void Deactivate()
        {
            Active = false;
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            if (Cam != null)
            {
                Cam.farClipPlane = _origFar;
                // 게임 카메라 컴포넌트(Follow/Boundaries)가 맵 밖에서 재활성화되면
                // 순간이동/클리핑이 생길 수 있어서, 켰던 시작지점으로 먼저 스냅
                CamT.SetPositionAndRotation(_homePos, Quaternion.Euler(_homePitch, _homeYaw, 0f));
            }
            foreach (var b in Suspended) if (b != null) b.enabled = true;
            Suspended.Clear();
            L("FreeCam OFF (원래 카메라 위치로 게임이 복귀시킴)");
            ShowHud();
        }

        // ====== 시간정지 (NPC/애니메이션/트윈 전부 멈춤, 프리캠은 unscaledDeltaTime 써서 계속 움직임) ======
        private static void ToggleTime()
        {
            if (!TimeStopped)
            {
                _origTimeScale = Time.timeScale;
                Time.timeScale = 0f;
                TimeStopped = true;
                L("시간정지 ON (NPC/애니메이션 전부 멈춤, 프리캠은 그대로 움직임)");
            }
            else
            {
                Time.timeScale = _origTimeScale;
                TimeStopped = false;
                L("시간정지 OFF");
            }
            ShowHud();
        }

        // ====== fog (진단 + 끄기) ======
        private static void ToggleFog()
        {
            if (!FogOff)
            {
                DiagnoseFog();
                _origFog = RenderSettings.fog;
                RenderSettings.fog = false;
                int n = SetVolumeFogOverrides(false);
                FogOff = true;
                L($"Fog OFF (RenderSettings.fog=false, Volume Fog override {n}개 비활성화)");
            }
            else
            {
                RenderSettings.fog = _origFog;
                SetVolumeFogOverrides(true);
                FogOff = false;
                L("Fog ON");
            }
            ShowHud();
        }

        // Volume 프로필 안의 "Fog" override 컴포넌트를 직접 active=false 처리 (RenderSettings.fog로 안 꺼지는 URP Fog용)
        private static int SetVolumeFogOverrides(bool restore)
        {
            Type vt = FindType("UnityEngine.Rendering.Volume");
            if (vt == null) return 0;
            var pp = vt.GetProperty("profile", BindingFlags.Public | BindingFlags.Instance);

            if (restore)
            {
                foreach (var kv in _origFogOverrideActive)
                {
                    var comp = kv[0]; var ap = (PropertyInfo)kv[1]; var was = (bool)kv[2];
                    try { ap.SetValue(comp, was, null); } catch { }
                }
                int restored = _origFogOverrideActive.Count;
                _origFogOverrideActive.Clear();
                return restored;
            }

            _origFogOverrideActive.Clear();
            foreach (var v in UnityEngine.Object.FindObjectsOfType(vt))
            {
                if (v == null) continue;
                var comps = GetVolumeComponents(v, pp);
                if (comps == null) continue;
                foreach (var c in comps)
                {
                    if (c == null || c.GetType().Name != "Fog") continue;
                    var ap = c.GetType().GetProperty("active", BindingFlags.Public | BindingFlags.Instance);
                    if (ap == null || !ap.CanWrite) continue;
                    try
                    {
                        bool cur = (bool)ap.GetValue(c, null);
                        _origFogOverrideActive.Add(new object[] { c, ap, cur });
                        ap.SetValue(c, false, null);
                    }
                    catch { }
                }
            }
            return _origFogOverrideActive.Count;
        }

        private static void DiagnoseFog()
        {
            try
            {
                L("===== FOG 진단 =====");
                L($"RenderSettings.fog={RenderSettings.fog} mode={RenderSettings.fogMode} " +
                  $"color={RenderSettings.fogColor} density={RenderSettings.fogDensity} " +
                  $"start={RenderSettings.fogStartDistance} end={RenderSettings.fogEndDistance}");

                Type vt = FindType("UnityEngine.Rendering.Volume");

                if (vt != null)
                {
                    var pp = vt.GetProperty("profile", BindingFlags.Public | BindingFlags.Instance);
                    var found = UnityEngine.Object.FindObjectsOfType(vt);
                    L($"Volume {found.Length}개 발견:");
                    foreach (var v in found)
                    {
                        var comp = v as Component;
                        string names = "";
                        try
                        {
                            var comps = GetVolumeComponents(v, pp);
                            if (comps != null) foreach (var c in comps) if (c != null) names += c.GetType().Name + ",";
                        }
                        catch { }
                        L($"  - '{comp?.name}' overrides=[{names}]");
                    }
                }
                else L("Volume 타입 없음");

                // TranslucentImage (LeTai 블러) 탐지
                int blur = 0;
                foreach (var b in UnityEngine.Object.FindObjectsOfType<Behaviour>())
                    if (b != null && b.GetType().Name.Contains("TranslucentImage")) blur++;
                L($"TranslucentImage(블러) 컴포넌트: {blur}개");
                L("===================");
            }
            catch (Exception ex) { L("fog 진단 오류: " + ex.Message); }
        }

        // 새로 생기거나 다시 켜지는 HighlightEffect까지 계속 잡아서 끔 (NPC가 나중에 스폰되는 경우 대응)
        private static void SuppressHighlightEffects()
        {
            foreach (var he in UnityEngine.Object.FindObjectsOfType<HighlightEffect>())
            {
                if (he != null && he.enabled)
                {
                    he.enabled = false;
                    if (!_suspendedHighlightEffects.Contains(he)) _suspendedHighlightEffects.Add(he);
                }
            }
        }

        // ====== 캐릭터 마우스오버 하이라이트 ======
        // HighlightManager 유무와 상관없이 HighlightEffect 컴포넌트 자체를 직접 껐다 켬 (확실하게 작동)
        private static void ToggleHighlight()
        {
            try
            {
                HighlightOff = !HighlightOff;

                var mgr = HighlightManager.instance;
                if (mgr != null) mgr.highlightOnHover = !HighlightOff;

                if (HighlightOff)
                {
                    _suspendedHighlightEffects.Clear();
                    SuppressHighlightEffects();
                    L($"캐릭터 하이라이트 OFF (HighlightEffect {_suspendedHighlightEffects.Count}개 비활성화, manager={(mgr != null)})");
                }
                else
                {
                    foreach (var he in _suspendedHighlightEffects) if (he != null) he.enabled = true;
                    _suspendedHighlightEffects.Clear();
                    if (mgr != null)
                    {
                        // 마우스가 같은 캐릭터 위에 그대로 있으면 대상변경 이벤트가 안 터져서
                        // 다시 안 켜질 수 있음 -> currentObject 캐시를 비워서 다음 프레임에 재적용되게 함
                        try
                        {
                            var f = typeof(HighlightManager).GetField("currentObject", BindingFlags.NonPublic | BindingFlags.Instance);
                            f?.SetValue(mgr, null);
                        }
                        catch { }
                    }
                    L("캐릭터 하이라이트 ON");
                }
            }
            catch (Exception ex)
            {
                L("하이라이트 토글 실패: " + ex);
                HighlightOff = !HighlightOff;
            }
            ShowHud();
        }

        // ====== UI 숨기기 (우리 HUD 캔버스는 제외하고 나머지 Canvas 전부 끔) ======
        private static void ToggleUI()
        {
            if (!UiHidden)
            {
                _hiddenCanvases.Clear();
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>())
                {
                    if (c == null || !c.enabled) continue;
                    if (_hud != null && c == _hud.canvas) continue;
                    c.enabled = false;
                    _hiddenCanvases.Add(c);
                }
                UiHidden = true;
                L($"UI 숨김 ({_hiddenCanvases.Count}개 캔버스)");
            }
            else
            {
                foreach (var c in _hiddenCanvases) if (c != null) c.enabled = true;
                _hiddenCanvases.Clear();
                UiHidden = false;
                L("UI 표시");
            }
            ShowHud();
        }

        // ====== HUD ======
        private static void EnsureHud()
        {
            if (_hud != null) return;
            Canvas canvas = null;
            foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>())
                if (c != null && c.isActiveAndEnabled && c.renderMode != RenderMode.WorldSpace)
                    if (canvas == null || c.sortingOrder >= canvas.sortingOrder) canvas = c;
            if (canvas == null) return;

            var go = new GameObject("FreeCamHUD");
            go.transform.SetParent(canvas.transform, false);
            _hud = go.AddComponent<Text>();
            _hud.font = Font.CreateDynamicFontFromOSFont("Malgun Gothic", 20);
            _hud.fontSize = 20;
            _hud.color = Color.yellow;
            _hud.horizontalOverflow = HorizontalWrapMode.Overflow;
            _hud.verticalOverflow = VerticalWrapMode.Overflow;
            _hud.raycastTarget = false;
            var rt = _hud.rectTransform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(20f, -20f);
            rt.sizeDelta = new Vector2(900f, 200f);
            var ol = go.AddComponent<Outline>();
            ol.effectColor = Color.black; ol.effectDistance = new Vector2(1.5f, -1.5f);
            go.transform.SetAsLastSibling();
        }

        public static void ShowHud() { UpdateHud(); }

        private static void UpdateHud()
        {
            try
            {
                EnsureHud();
                if (_hud == null) return;
                bool show = !_hudHidden;
                _hud.gameObject.SetActive(show);
                if (!show) return;
                string t = Active
                    ? $"● FreeCam ON  속도 {_speed:F1}\nWASD 이동 / Space·Ctrl 상하 / 마우스 시점 / 휠 속도 / Shift 가속 / R 시작위치복귀\n"
                    : "○ FreeCam OFF (F7)\n";
                t += $"안개 {(FogOff ? "OFF" : "ON")} (F8) / 하이라이트 {(HighlightOff ? "OFF" : "ON")} (F9) / UI {(UiHidden ? "숨김" : "표시")} (F10) / 시간정지 {(TimeStopped ? "ON" : "OFF")} (F5)\n";
                t += "F6/H 로 이 안내창 숨기기";
                _hud.text = t.TrimEnd();
            }
            catch { }
        }

        private static void L(string m) { if (Plugin.Log != null) Plugin.Log.LogInfo(m); }
    }

    [HarmonyPatch(typeof(EasyTouchCameraRewiredInput), "Update")]
    internal static class Patch_Update
    {
        [HarmonyPostfix]
        public static void Postfix(EasyTouchCameraRewiredInput __instance)
        {
            try { FreeCam.Tick(__instance); }
            catch (Exception e) { if (Plugin.Log != null) Plugin.Log.LogError("Tick: " + e.Message); }
        }
    }
}
