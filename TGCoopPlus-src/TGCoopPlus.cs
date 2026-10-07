// TGCoopPlus — companion plugin for TGCoop 0.5.36 (Tainted Grail: The Fall of Avalon)
//
//  1. AnimFix   : the game build renumbered HeroLayerType (Legs 9 -> 10, CameraShakes 20 -> 21).
//                 TGCoop hard-codes the old values, so the partner's clone never gets its legs
//                 animations and stays in T-pose. We patch those constants at runtime using the
//                 enum values of the game that is actually running.
//  2. StoryRetry: TGCoop applies the host's quest/flag state immediately when the packet arrives.
//                 If the guest has no world loaded yet (main menu, zone travel) the game throws
//                 "No service registered with type GameplayMemory" and the packet is lost.
//                 We hold those packets and replay them once the world is ready.
//  3. Panel     : an in-game window (Insert key by default) to choose HOST / CLIENT, create the
//                 lobby, open the Steam invite, join a friend's lobby directly, or leave.
//
// Written for C# 5 so it compiles with the csc.exe that ships with Windows (.NET Framework 4.x).

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Steamworks;
using UnityEngine;
using Awaken.TG.MVC;
using Awaken.TG.Main.Heroes;
using Awaken.TG.Main.Memories;
using Awaken.TG.Main.Animations.FSM.Heroes.Base;

namespace TGCoopPlus
{
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("com.tgcoop.core", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.tgcoop.plus";
        public const string Name = "TGCoopPlus";
        public const string Version = "1.1.0";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        // ---- config ----
        internal static ConfigEntry<KeyCode> PanelKey;
        internal static ConfigEntry<string> PreferredRole;   // Host | Client | None
        internal static ConfigEntry<string> Language;        // zh | en
        internal static ConfigEntry<bool> EnableAnimFix;
        internal static ConfigEntry<bool> EnableStoryRetry;
        internal static ConfigEntry<bool> EnableHostAuthority;
        internal static ConfigEntry<bool> AnimDebug;

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            PanelKey = Config.Bind("Panel", "ToggleKey", KeyCode.Insert,
                "Key that opens/closes the co-op panel. / 開關連線面板的按鍵。");
            PreferredRole = Config.Bind("Panel", "PreferredRole", "None",
                "Role you normally play: Host, Client or None. / 你平常的角色：Host（主機）、Client（客戶端）或 None。");
            Language = Config.Bind("Panel", "Language", "zh",
                "Panel language: zh or en. / 面板語言：zh 或 en。");
            EnableAnimFix = Config.Bind("Fixes", "AnimLayerFix", true,
                "Fix the partner clone T-pose caused by the renumbered HeroLayerType enum. / 修正圖層列舉改變造成的隊友 T-pose。");
            EnableStoryRetry = Config.Bind("Fixes", "StoryRetry", true,
                "Hold host quest/flag packets until the world is loaded, then apply them. / 世界未載入時暫存主機的任務狀態，載入後再套用。");
            EnableHostAuthority = Config.Bind("Fixes", "HostAuthority", true,
                "Progress flows ONLY host -> client: the client never sends quests/flags/story rewards and the host ignores them. / 進度只從主機流向客戶端：客戶端不送任務、旗標、劇情獎勵，主機也忽略它們。");
            AnimDebug = Config.Bind("Debug", "AnimDebug", true,
                "Log every remote animation state applied to the partner clone (first 300 per session). / 記錄套用到隊友分身的每個遠端動畫狀態（每場前 300 筆）。");

            _harmony = new Harmony(Guid);
            try
            {
                if (EnableAnimFix.Value) AnimFix.Apply(_harmony);
            }
            catch (Exception e) { Log.LogError("[AnimFix] failed: " + e); }
            try
            {
                if (EnableStoryRetry.Value) StoryRetry.Apply(_harmony);
            }
            catch (Exception e) { Log.LogError("[StoryRetry] failed: " + e); }
            try
            {
                if (EnableHostAuthority.Value) HostAuthority.Apply(_harmony);
            }
            catch (Exception e) { Log.LogError("[HostAuthority] failed: " + e); }
            try
            {
                if (AnimDebug.Value) AnimDebugLog.Apply(_harmony);
            }
            catch (Exception e) { Log.LogError("[AnimDebug] failed: " + e); }

            Log.LogInfo(Name + " " + Version + " loaded.");
        }

        private void Update()
        {
            try
            {
                if (Input.GetKeyDown(PanelKey.Value)) Panel.Toggle();
                StoryRetry.Tick();
                Panel.Tick();
            }
            catch (Exception e)
            {
                if (Time.frameCount % 600 == 0) Log.LogError("[Update] " + e.Message);
            }
        }

        private void OnGUI()
        {
            try { Panel.Draw(); }
            catch (Exception e) { if (Time.frameCount % 600 == 0) Log.LogError("[OnGUI] " + e.Message); }
        }
    }

    // =====================================================================================
    //  Reflection helpers into TGCoop
    // =====================================================================================
    internal static class Coop
    {
        private static Type _tManager, _tSession, _tTransport, _tMsgType;
        private static FieldInfo _fInstance, _fSession, _fTransport;
        private static MethodInfo _mSendTo, _mHostLobby, _mOpenInvite, _mLeave, _mRequestResync;
        private static PropertyInfo _pIsHost, _pConnected, _pInLobby, _pRemotePeer, _pLobbyId;
        private static bool _resolved;

        private static void Resolve()
        {
            if (_resolved) return;
            _resolved = true;
            _tManager = AccessTools.TypeByName("TGCoop.CoopManager");
            _tSession = AccessTools.TypeByName("TGCoop.Net.CoopSession");
            _tTransport = AccessTools.TypeByName("TGCoop.Net.CoopTransport");
            _tMsgType = AccessTools.TypeByName("TGCoop.Net.MsgType");
            if (_tManager == null || _tSession == null) { Plugin.Log.LogError("[Coop] TGCoop types not found."); return; }
            _fInstance = AccessTools.Field(_tManager, "_instance");
            _fSession = AccessTools.Field(_tManager, "_session");
            _fTransport = AccessTools.Field(_tManager, "_transport");
            _mRequestResync = AccessTools.Method(_tManager, "RequestResync");
            _mHostLobby = AccessTools.Method(_tSession, "HostLobby");
            _mOpenInvite = AccessTools.Method(_tSession, "OpenInviteOverlay");
            _mLeave = AccessTools.Method(_tSession, "Leave");
            _pIsHost = AccessTools.Property(_tSession, "IsHost");
            _pConnected = AccessTools.Property(_tSession, "Connected");
            _pInLobby = AccessTools.Property(_tSession, "InLobby");
            _pRemotePeer = AccessTools.Property(_tSession, "RemotePeer");
            _pLobbyId = AccessTools.Property(_tSession, "LobbyId");
            if (_tTransport != null) _mSendTo = AccessTools.Method(_tTransport, "SendTo");
        }

        private static object Manager { get { Resolve(); return _fInstance == null ? null : _fInstance.GetValue(null); } }
        public static object Session { get { var m = Manager; return (m == null || _fSession == null) ? null : _fSession.GetValue(m); } }
        private static object Transport { get { var m = Manager; return (m == null || _fTransport == null) ? null : _fTransport.GetValue(m); } }

        public static bool Available { get { return Session != null; } }

        private static bool GetBool(PropertyInfo p)
        {
            var s = Session; if (s == null || p == null) return false;
            try { return (bool)p.GetValue(s, null); } catch { return false; }
        }
        public static bool IsHost { get { return GetBool(_pIsHost); } }
        public static bool Connected { get { return GetBool(_pConnected); } }
        public static bool InLobby { get { return GetBool(_pInLobby); } }
        public static CSteamID RemotePeer
        {
            get { var s = Session; if (s == null || _pRemotePeer == null) return CSteamID.Nil; try { return (CSteamID)_pRemotePeer.GetValue(s, null); } catch { return CSteamID.Nil; } }
        }
        public static CSteamID LobbyId
        {
            get { var s = Session; if (s == null || _pLobbyId == null) return CSteamID.Nil; try { return (CSteamID)_pLobbyId.GetValue(s, null); } catch { return CSteamID.Nil; } }
        }

        public static void HostLobby() { var s = Session; if (s != null && _mHostLobby != null) _mHostLobby.Invoke(s, null); }
        public static void OpenInvite() { var s = Session; if (s != null && _mOpenInvite != null) _mOpenInvite.Invoke(s, null); }
        public static void Leave() { var s = Session; if (s != null && _mLeave != null) _mLeave.Invoke(s, null); }

        /// Ask the host to resend its full world/story state (same message the guest sends after loading a save).
        public static bool RequestHostResync()
        {
            var t = Transport; if (t == null || _mSendTo == null || _tMsgType == null) return false;
            if (!Connected || IsHost) return false;
            object msg = Enum.ToObject(_tMsgType, 42); // MsgType.SaveLoaded; payload[0] == 0 means "guest loaded -> please resync"
            _mSendTo.Invoke(t, new object[] { RemotePeer, msg, new byte[] { 0 }, true });
            return true;
        }
    }

    // =====================================================================================
    //  1. AnimFix — retarget hard-coded HeroLayerType constants
    // =====================================================================================
    internal static class AnimFix
    {
        private static int _legsOld = 9, _legsNew;
        private static int _shakeOld = 20, _shakeNew;

        public static void Apply(Harmony h)
        {
            _legsNew = (int)(HeroLayerType)Enum.Parse(typeof(HeroLayerType), "Legs");
            try { _shakeNew = (int)(HeroLayerType)Enum.Parse(typeof(HeroLayerType), "CameraShakes"); }
            catch { _shakeNew = _shakeOld; }

            Plugin.Log.LogInfo(string.Format("[AnimFix] game enum: Legs={0} CameraShakes={1} (mod expects {2}/{3})",
                _legsNew, _shakeNew, _legsOld, _shakeOld));

            if (_legsNew == _legsOld && _shakeNew == _shakeOld)
            {
                Plugin.Log.LogInfo("[AnimFix] enum matches the mod; nothing to patch.");
                return;
            }

            Type puppet = AccessTools.TypeByName("TGCoop.Sync.PuppetAnimancer");
            Type anim = AccessTools.TypeByName("TGCoop.Sync.AnimSync");
            if (puppet == null || anim == null) { Plugin.Log.LogError("[AnimFix] TGCoop animation types not found."); return; }

            if (_legsNew != _legsOld)
            {
                PatchConst(h, puppet, "TryFinishLoad", "LegsTranspiler");
                PatchConst(h, puppet, "GetLayerIndexFor", "LegsTranspiler");
                PatchConst(h, puppet, "PlayRemoteState", "LegsTranspiler");
            }
            if (_shakeNew != _shakeOld)
            {
                PatchConst(h, anim, "OnLocalStateChanged", "ShakeTranspiler");
            }
        }

        private static void PatchConst(Harmony h, Type t, string method, string transpiler)
        {
            MethodInfo m = AccessTools.Method(t, method);
            if (m == null) { Plugin.Log.LogWarning("[AnimFix] method not found: " + t.Name + "." + method); return; }
            h.Patch(m, null, null, new HarmonyMethod(typeof(AnimFix), transpiler));
            Plugin.Log.LogInfo("[AnimFix] patched " + t.Name + "." + method);
        }

        public static IEnumerable<CodeInstruction> LegsTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            return Replace(instructions, _legsOld, _legsNew);
        }
        public static IEnumerable<CodeInstruction> ShakeTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            return Replace(instructions, _shakeOld, _shakeNew);
        }

        private static IEnumerable<CodeInstruction> Replace(IEnumerable<CodeInstruction> instructions, int from, int to)
        {
            int n = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if (ci.opcode == OpCodes.Ldc_I4_S)
                {
                    int v = Convert.ToInt32(ci.operand);
                    if (v == from) { ci.operand = (sbyte)to; n++; }
                }
                else if (ci.opcode == OpCodes.Ldc_I4)
                {
                    int v = Convert.ToInt32(ci.operand);
                    if (v == from) { ci.operand = to; n++; }
                }
                yield return ci;
            }
            Plugin.Log.LogInfo(string.Format("[AnimFix]   {0} -> {1}: {2} constant(s) replaced", from, to, n));
        }
    }

    // =====================================================================================
    //  2. StoryRetry — hold host story packets until the guest world is ready
    // =====================================================================================
    internal static class StoryRetry
    {
        private class Pending
        {
            public MethodInfo Target; public byte[] Payload; public CSteamID From; public float Expires;
        }

        private static readonly List<Pending> _queue = new List<Pending>();
        private static readonly Dictionary<string, MethodInfo> _targets = new Dictionary<string, MethodInfo>();
        private static bool _replaying;
        private static float _nextCheck;
        private static int _dropped;

        private static readonly string[] Handlers = {
            "OnQuestSnapshotReceived", "OnQuestStateReceived", "OnObjectiveStateReceived", "OnFlagReceived", "OnFlagBaselineReceived"
        };

        public static void Apply(Harmony h)
        {
            Type story = AccessTools.TypeByName("TGCoop.Sync.StorySync");
            if (story == null) { Plugin.Log.LogError("[StoryRetry] StorySync not found."); return; }
            foreach (string name in Handlers)
            {
                MethodInfo m = AccessTools.Method(story, name);
                if (m == null) { Plugin.Log.LogWarning("[StoryRetry] handler not found: " + name); continue; }
                _targets[name] = m;
                h.Patch(m, new HarmonyMethod(typeof(StoryRetry), "Prefix"));
            }
            Plugin.Log.LogInfo("[StoryRetry] guarding " + _targets.Count + " story handler(s).");
        }

        public static bool WorldReady()
        {
            try
            {
                if (Hero.Current == null) return false;
                Services s = World.Services;
                if (s == null) return false;
                GameplayMemory gm;
                return s.TryGet<GameplayMemory>(out gm) && gm != null;
            }
            catch { return false; }
        }

        // Harmony prefix for every StorySync.On*Received(byte[] payload, CSteamID from)
        public static bool Prefix(MethodBase __originalMethod, object[] __args)
        {
            if (_replaying) return true;
            // Host authority: the host never applies story state coming from the client.
            if (Plugin.EnableHostAuthority.Value && Coop.IsHost) { HostAuthority.CountBlockedIn(); return false; }
            if (WorldReady()) return true;
            if (__args == null || __args.Length < 2) return true;
            var p = new Pending();
            p.Target = __originalMethod as MethodInfo;
            p.Payload = __args[0] as byte[];
            p.From = __args[1] is CSteamID ? (CSteamID)__args[1] : CSteamID.Nil;
            p.Expires = Time.realtimeSinceStartup + 300f;
            lock (_queue) _queue.Add(p);
            if (_queue.Count == 1 || _queue.Count % 25 == 0)
                Plugin.Log.LogInfo("[StoryRetry] world not ready; holding host story packets (" + _queue.Count + ").");
            return false;
        }

        public static void Tick()
        {
            if (_queue.Count == 0) return;
            if (Time.realtimeSinceStartup < _nextCheck) return;
            _nextCheck = Time.realtimeSinceStartup + 1f;
            if (!WorldReady())
            {
                lock (_queue)
                {
                    for (int i = _queue.Count - 1; i >= 0; i--)
                        if (_queue[i].Expires < Time.realtimeSinceStartup) { _queue.RemoveAt(i); _dropped++; }
                }
                return;
            }
            List<Pending> batch;
            lock (_queue) { batch = new List<Pending>(_queue); _queue.Clear(); }
            _replaying = true;
            int ok = 0;
            try
            {
                foreach (Pending p in batch)
                {
                    if (p.Target == null) continue;
                    try { p.Target.Invoke(null, new object[] { p.Payload, p.From }); ok++; }
                    catch (Exception e) { Plugin.Log.LogWarning("[StoryRetry] replay failed: " + e.Message); }
                }
            }
            finally { _replaying = false; }
            Plugin.Log.LogInfo("[StoryRetry] world ready: replayed " + ok + " held story packet(s)." + (_dropped > 0 ? " dropped(expired)=" + _dropped : ""));
            _dropped = 0;
        }

        public static int QueueCount { get { return _queue.Count; } }
    }

    // =====================================================================================
    //  2b. HostAuthority — progress flows only host -> client
    //      TGCoop broadcasts quest/flag/reward changes from BOTH peers and merges "higher" states.
    //      That makes the host complete the client's quests (and receive their XP). We cut the
    //      client -> host direction on both ends.
    // =====================================================================================
    internal static class HostAuthority
    {
        private static int _blockedOut, _blockedIn;
        private static float _nextReport;

        // client-side senders (static methods on StorySync / RewardSync)
        private static readonly string[] StorySenders = {
            "OnLocalQuestState", "OnLocalObjectiveState", "OnLocalFlag",
            "ScheduleFullSnapshot", "SendFullSnapshot", "SendQuestSnapshot", "TrySendAllFlags", "SendMemoryBaseline"
        };

        public static void Apply(Harmony h)
        {
            Type story = AccessTools.TypeByName("TGCoop.Sync.StorySync");
            Type reward = AccessTools.TypeByName("TGCoop.Sync.RewardSync");
            int n = 0;
            if (story != null)
            {
                foreach (string name in StorySenders)
                {
                    MethodInfo m = AccessTools.Method(story, name);
                    if (m == null) { Plugin.Log.LogWarning("[HostAuthority] sender not found: StorySync." + name); continue; }
                    h.Patch(m, new HarmonyMethod(typeof(HostAuthority), "ClientSendPrefix"));
                    n++;
                }
            }
            if (reward != null)
            {
                MethodInfo send = AccessTools.Method(reward, "ExecutePostfix");
                if (send != null) { h.Patch(send, new HarmonyMethod(typeof(HostAuthority), "ClientSendPrefix")); n++; }
                MethodInfo recv = AccessTools.Method(reward, "OnStoryRewardReceived");
                if (recv != null) { h.Patch(recv, new HarmonyMethod(typeof(HostAuthority), "HostReceivePrefix")); n++; }
            }
            // The five StorySync.On*Received handlers are guarded inside StoryRetry.Prefix (same prefix, avoids ordering issues).
            if (!Plugin.EnableStoryRetry.Value && story != null)
            {
                foreach (string name in new[] { "OnQuestSnapshotReceived", "OnQuestStateReceived", "OnObjectiveStateReceived", "OnFlagReceived", "OnFlagBaselineReceived" })
                {
                    MethodInfo m = AccessTools.Method(story, name);
                    if (m != null) { h.Patch(m, new HarmonyMethod(typeof(HostAuthority), "HostReceivePrefix")); n++; }
                }
            }
            Plugin.Log.LogInfo("[HostAuthority] enabled: " + n + " method(s) guarded. Progress flows host -> client only.");
        }

        // Skip the original when WE are the connected client (never push story state to the host).
        public static bool ClientSendPrefix()
        {
            if (Coop.Connected && !Coop.IsHost) { _blockedOut++; Report(); return false; }
            return true;
        }

        // Skip the original when WE are the host (never apply story state coming from the client).
        public static bool HostReceivePrefix()
        {
            if (Coop.IsHost) { _blockedIn++; Report(); return false; }
            return true;
        }

        public static void CountBlockedIn() { _blockedIn++; Report(); }

        private static void Report()
        {
            if (Time.realtimeSinceStartup < _nextReport) return;
            _nextReport = Time.realtimeSinceStartup + 30f;
            Plugin.Log.LogInfo("[HostAuthority] blocked so far: client->host sends=" + _blockedOut + ", host-side applies=" + _blockedIn);
        }
    }

    // =====================================================================================
    //  2c. AnimDebugLog — see which remote animation states reach the clone
    // =====================================================================================
    internal static class AnimDebugLog
    {
        private static int _count;
        private const int Max = 300;

        public static void Apply(Harmony h)
        {
            Type puppet = AccessTools.TypeByName("TGCoop.Sync.PuppetAnimancer");
            if (puppet == null) return;
            MethodInfo m = AccessTools.Method(puppet, "PlayRemoteState");
            if (m == null) return;
            h.Patch(m, new HarmonyMethod(typeof(AnimDebugLog), "Prefix"));
            Plugin.Log.LogInfo("[AnimDebug] logging remote states applied to the clone (max " + Max + ").");
        }

        public static void Prefix(object[] __args)
        {
            if (_count >= Max || __args == null || __args.Length < 2) return;
            _count++;
            string layer = __args[0] == null ? "?" : __args[0].ToString();
            string state = __args[1] == null ? "?" : __args[1].ToString();
            Plugin.Log.LogInfo("[AnimDebug] remote " + layer + "/" + state);
            if (_count == Max) Plugin.Log.LogInfo("[AnimDebug] limit reached; further states not logged.");
        }
    }

    // =====================================================================================
    //  3. Panel — IMGUI window for host / client control
    // =====================================================================================
    internal static class Panel
    {
        private static bool _open;
        private static Rect _rect = new Rect(40, 120, 460, 420);
        private static Font _font;
        private static bool _fontTried;
        private static string _status = "";
        private static float _statusUntil;
        private static float _nextFriendScan;
        private static readonly List<FriendLobby> _friends = new List<FriendLobby>();
        private static Vector2 _scroll;
        private static uint _appId;

        private struct FriendLobby { public string Name; public CSteamID Lobby; public bool IsCoop; }

        private static bool Zh { get { return !string.Equals(Plugin.Language.Value, "en", StringComparison.OrdinalIgnoreCase); } }
        private static string T(string zh, string en) { return Zh ? zh : en; }

        public static void Toggle() { _open = !_open; if (_open) { _nextFriendScan = 0; } }

        private static void Flash(string s) { _status = s; _statusUntil = Time.realtimeSinceStartup + 6f; Plugin.Log.LogInfo("[Panel] " + s); }

        public static void Tick()
        {
            if (!_open) return;
            // keep the mouse usable while the panel is open
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
            if (!Cursor.visible) Cursor.visible = true;
            if (Time.realtimeSinceStartup >= _nextFriendScan) { _nextFriendScan = Time.realtimeSinceStartup + 3f; ScanFriends(); }
        }

        private static void EnsureFont()
        {
            if (_fontTried) return;
            _fontTried = true;
            try
            {
                string[] names = { "Microsoft JhengHei UI", "Microsoft JhengHei", "Microsoft YaHei UI", "Noto Sans CJK TC", "Arial Unicode MS", "Arial" };
                _font = Font.CreateDynamicFontFromOSFont(names, 15);
            }
            catch { _font = null; }
        }

        private static void ScanFriends()
        {
            _friends.Clear();
            try
            {
                if (_appId == 0) _appId = SteamUtils.GetAppID().m_AppId;
                int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
                for (int i = 0; i < n; i++)
                {
                    CSteamID id = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                    FriendGameInfo_t info;
                    if (!SteamFriends.GetFriendGamePlayed(id, out info)) continue;
                    if (info.m_gameID.AppID().m_AppId != _appId) continue;
                    if (!info.m_steamIDLobby.IsValid() || !info.m_steamIDLobby.IsLobby()) continue;
                    var f = new FriendLobby();
                    f.Name = SteamFriends.GetFriendPersonaName(id);
                    f.Lobby = info.m_steamIDLobby;
                    string tag = SteamMatchmaking.GetLobbyData(info.m_steamIDLobby, "tgcoop");
                    f.IsCoop = !string.IsNullOrEmpty(tag);
                    _friends.Add(f);
                }
            }
            catch (Exception e) { if (Time.frameCount % 300 == 0) Plugin.Log.LogWarning("[Panel] friend scan: " + e.Message); }
        }

        public static void Draw()
        {
            if (!_open) return;
            EnsureFont();
            Font prev = GUI.skin.font;
            if (_font != null) GUI.skin.font = _font;
            _rect = GUILayout.Window(0x7C00, _rect, Body, T("TGCoop 連線面板  (" + Plugin.PanelKey.Value + " 關閉)", "TGCoop co-op panel  (" + Plugin.PanelKey.Value + " to close)"));
            GUI.skin.font = prev;
        }

        private static void Body(int id)
        {
            GUILayout.BeginVertical();

            bool coop = Coop.Available;
            bool hero = false;
            try { hero = Hero.Current != null; } catch { }

            // ---- status ----
            if (!coop)
            {
                GUILayout.Label(T("TGCoop 尚未初始化（Steam 未就緒？）", "TGCoop not initialised (Steam not ready?)"));
            }
            else
            {
                string me = "";
                try { me = SteamFriends.GetPersonaName(); } catch { }
                string role = !Coop.InLobby ? T("未連線", "not connected") : (Coop.IsHost ? T("主機 HOST", "HOST") : T("客戶端 CLIENT", "CLIENT"));
                string peer = Coop.Connected ? SafeName(Coop.RemotePeer) : T("（無）", "(none)");
                GUILayout.Label(T("我：", "Me: ") + me + "    " + T("角色：", "Role: ") + role);
                GUILayout.Label(T("隊友：", "Partner: ") + peer + "    " + T("連線：", "Link: ") + (Coop.Connected ? T("已建立", "up") : T("無", "down")));
                if (!hero)
                {
                    GUI.color = new Color(1f, 0.75f, 0.3f);
                    GUILayout.Label(T("⚠ 尚未載入存檔。請先載入存檔再建立 / 加入房間，否則任務進度不會同步。",
                                      "⚠ No save loaded. Load your save BEFORE hosting / joining, or quest progress will not sync."));
                    GUI.color = Color.white;
                }
                if (StoryRetry.QueueCount > 0)
                    GUILayout.Label(T("暫存中的主機任務封包：", "Held host story packets: ") + StoryRetry.QueueCount);
            }

            GUILayout.Space(6);

            // ---- role selection ----
            GUILayout.Label(T("我這次要當：", "This session I am:"));
            GUILayout.BeginHorizontal();
            bool isHostPref = Plugin.PreferredRole.Value == "Host";
            bool isClientPref = Plugin.PreferredRole.Value == "Client";
            if (GUILayout.Toggle(isHostPref, T("  主機（Server）", "  Host (server)"), GUI.skin.button, GUILayout.Height(32)) && !isHostPref)
            { Plugin.PreferredRole.Value = "Host"; }
            if (GUILayout.Toggle(isClientPref, T("  客戶端（Client）", "  Client"), GUI.skin.button, GUILayout.Height(32)) && !isClientPref)
            { Plugin.PreferredRole.Value = "Client"; }
            GUILayout.EndHorizontal();
            GUILayout.Label(T("進度以主機為準：客戶端的世界會向主機收斂，主機的存檔不受影響。",
                              "Progress follows the host: the client's world converges to the host's; the host's save is untouched."));
            GUILayout.Label(T("主機權威模式：", "Host authority: ") + (Plugin.EnableHostAuthority.Value ? T("開（客戶端不會改到主機的任務 / 經驗）", "ON (client cannot touch host quests / XP)") : T("關", "OFF")));

            GUILayout.Space(6);
            GUI.enabled = coop;

            // ---- host section ----
            if (Plugin.PreferredRole.Value == "Host")
            {
                if (GUILayout.Button(T("建立房間並開啟 Steam 邀請視窗", "Create lobby and open Steam invite"), GUILayout.Height(34)))
                {
                    if (!hero) Flash(T("請先載入存檔再建立房間。", "Load a save before hosting."));
                    else { Coop.HostLobby(); Flash(T("房間建立中，邀請視窗稍後會自動開啟。", "Creating lobby; the invite overlay opens shortly.")); }
                }
                if (Coop.InLobby && GUILayout.Button(T("再次開啟邀請視窗", "Open invite overlay again"), GUILayout.Height(28)))
                    Coop.OpenInvite();
            }

            // ---- client section ----
            if (Plugin.PreferredRole.Value == "Client")
            {
                GUILayout.Label(T("正在開房的好友（每 3 秒更新）：", "Friends hosting a lobby (refreshes every 3 s):"));
                _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(110));
                if (_friends.Count == 0)
                    GUILayout.Label(T("　找不到。請主機先按「建立房間」，或直接從 Steam 接受邀請。", "  None found. Ask the host to create the lobby first, or accept the Steam invite."));
                foreach (FriendLobby f in _friends)
                {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label(f.Name + (f.IsCoop ? "" : T("（非 TGCoop 房間）", " (not a TGCoop lobby)")), GUILayout.Width(250));
                    if (GUILayout.Button(T("加入", "Join"), GUILayout.Width(90)))
                    {
                        if (!hero) Flash(T("請先載入存檔再加入。", "Load a save before joining."));
                        else { SteamMatchmaking.JoinLobby(f.Lobby); Flash(T("加入 ", "Joining ") + f.Name + " ..."); }
                    }
                    GUILayout.EndHorizontal();
                }
                GUILayout.EndScrollView();
                if (Coop.Connected && !Coop.IsHost && GUILayout.Button(T("向主機要求重新同步進度", "Ask host to resync progress"), GUILayout.Height(28)))
                {
                    if (Coop.RequestHostResync()) Flash(T("已向主機送出重新同步要求。", "Resync request sent to host."));
                    else Flash(T("無法送出（尚未連線）。", "Could not send (not connected)."));
                }
            }

            GUILayout.Space(6);
            if (Coop.InLobby && GUILayout.Button(T("離開連線", "Leave session"), GUILayout.Height(28)))
            { Coop.Leave(); Flash(T("已離開。", "Left the session.")); }
            GUI.enabled = true;

            // ---- footer ----
            GUILayout.FlexibleSpace();
            if (Time.realtimeSinceStartup < _statusUntil && !string.IsNullOrEmpty(_status))
            {
                GUI.color = new Color(0.6f, 1f, 0.6f);
                GUILayout.Label(_status);
                GUI.color = Color.white;
            }
            GUILayout.Label(T("按鍵：F7 開房 / F3 傳送到隊友 / F10 離開 / H 扶起隊友", "Keys: F7 host / F3 go to partner / F10 leave / H revive"));
            if (GUILayout.Button(T("關閉面板", "Close"), GUILayout.Height(24))) _open = false;

            GUILayout.EndVertical();
            GUI.DragWindow(new Rect(0, 0, 10000, 22));
        }

        private static string SafeName(CSteamID id)
        {
            try { return id.IsValid() ? SteamFriends.GetFriendPersonaName(id) : "?"; } catch { return "?"; }
        }
    }
}
