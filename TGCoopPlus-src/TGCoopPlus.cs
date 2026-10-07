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
using System.Linq;
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
using Awaken.TG.Main.Fights.NPCs;
using Awaken.TG.Main.Fights.DamageInfo;
using Awaken.TG.Main.Utility.Animations;
using Awaken.TG.Main.Utility.Animations.ARAnimator;
using Animancer;

namespace TGCoopPlus
{
    [BepInPlugin(Guid, Name, Version)]
    [BepInDependency("com.tgcoop.core", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "com.tgcoop.plus";
        public const string Name = "TGCoopPlus";
        public const string Version = "1.6.2";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        // ---- config ----
        internal static ConfigEntry<KeyCode> PanelKey;
        internal static ConfigEntry<string> PreferredRole;   // Host | Client | None
        internal static ConfigEntry<string> Language;        // zh | en
        internal static ConfigEntry<bool> EnableAnimFix;
        internal static ConfigEntry<bool> EnableStoryRetry;
        internal static ConfigEntry<bool> EnableHostAuthority;
        internal static ConfigEntry<bool> EnableSharedKillExp;
        internal static ConfigEntry<bool> EnableLegsFix;
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
            EnableSharedKillExp = Config.Bind("Fixes", "SharedKillExp", true,
                "When the partner (or a host-confirmed kill) kills an enemy in your world, you also get the kill XP. / 隊友在你的世界殺死敵人時，你也獲得擊殺經驗。");
            EnableLegsFix = Config.Bind("Fixes", "LegsFix", true,
                "Drive the clone's leg blend tree from the partner's velocity directly and play crouched legs when the partner crouches (first-person senders have no legs state machine). / 直接用隊友速度驅動分身腿部混合動畫，蹲下時播放蹲姿腿部動畫。");
            AnimDebug = Config.Bind("Debug", "AnimDebug", true,
                "Log every remote animation state applied to the partner clone (first 300 per session). / 記錄套用到隊友分身的每個遠端動畫狀態（每場前 300 筆）。");

            try { BepInEx.Logging.Logger.Listeners.Add(new ErrorWatcher()); }
            catch (Exception e) { Log.LogWarning("[ErrorWatcher] could not attach: " + e.Message); }

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
                if (EnableSharedKillExp.Value) SharedKillExp.Apply(_harmony);
            }
            catch (Exception e) { Log.LogError("[SharedKillExp] failed: " + e); }
            try
            {
                if (EnableLegsFix.Value) LegsFix.Apply(_harmony);
            }
            catch (Exception e) { Log.LogError("[LegsFix] failed: " + e); }
            try
            {
                if (EnableLegsFix.Value) MaskFix.Apply(_harmony);
            }
            catch (Exception e) { Log.LogError("[MaskFix] failed: " + e); }
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
    //  2d. SharedKillExp — kill XP for kills made by the partner (or confirmed remotely)
    //      TGCoop applies remote damage/deaths with the partner's proxy NPC as the attacker, so
    //      NpcElement.ShouldAttributeKillToHero() is false and the game awards no XP. We say "yes"
    //      whenever the kill came through TGCoop's remote path or the attacker is the proxy.
    // =====================================================================================
    internal static class SharedKillExp
    {
        private static MethodInfo _getProxyNpc;
        private static FieldInfo _fRemoteDeath, _fRemoteDamage;
        private static PropertyInfo _pAttacker;
        private static int _count;

        public static void Apply(Harmony h)
        {
            Type ally = AccessTools.TypeByName("TGCoop.Sync.AllyBody");
            Type combat = AccessTools.TypeByName("TGCoop.Sync.CombatSync");
            if (ally != null) _getProxyNpc = AccessTools.Method(ally, "get_CurrentNpc");
            if (combat != null)
            {
                _fRemoteDeath = AccessTools.Field(combat, "_applyingRemoteDeath");
                _fRemoteDamage = AccessTools.Field(combat, "_applyingRemoteDamage");
            }
            MethodInfo target = AccessTools.Method(typeof(NpcElement), "ShouldAttributeKillToHero");
            if (target == null) { Plugin.Log.LogWarning("[SharedKillExp] NpcElement.ShouldAttributeKillToHero not found; disabled."); return; }
            h.Patch(target, null, new HarmonyMethod(typeof(SharedKillExp), "Postfix"));
            Plugin.Log.LogInfo("[SharedKillExp] enabled (proxy=" + (_getProxyNpc != null) + ", flags=" + (_fRemoteDeath != null && _fRemoteDamage != null) + ").");
        }

        private static bool Flag(FieldInfo f)
        {
            try { return f != null && (bool)f.GetValue(null); } catch { return false; }
        }

        public static void Postfix(object[] __args, ref bool __result)
        {
            if (__result) return;
            try
            {
                bool remote = Flag(_fRemoteDeath) || Flag(_fRemoteDamage);
                if (!remote && _getProxyNpc != null && __args != null && __args.Length > 0 && __args[0] != null)
                {
                    if (_pAttacker == null) _pAttacker = AccessTools.Property(__args[0].GetType(), "AttackerPure");
                    object proxy = _getProxyNpc.Invoke(null, null);
                    object attacker = _pAttacker == null ? null : _pAttacker.GetValue(__args[0], null);
                    if (proxy != null && attacker != null && ReferenceEquals(attacker, proxy)) remote = true;
                }
                if (!remote) return;
                __result = true;
                _count++;
                if (_count <= 20 || _count % 50 == 0)
                    Plugin.Log.LogInfo("[SharedKillExp] partner kill attributed to you for XP (#" + _count + ").");
            }
            catch (Exception e)
            {
                if (_count == 0) Plugin.Log.LogWarning("[SharedKillExp] " + e.Message);
            }
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
    //  2f. LegsFix — legs of the partner clone
    //      A first-person sender has no Legs state machine, so the clone's legs are driven only by
    //      PlayerState.RelVel (TargetLocomotion) and PlayerState.Crouching. We (1) push the velocity
    //      into whatever mixer is playing on layer 0 ourselves, (2) swap in the CrouchedIdle /
    //      CrouchedMovement legs clips while the partner crouches, (3) log the numbers periodically.
    // =====================================================================================
    internal static class LegsFix
    {
        private static Type _tPuppet;
        private static FieldInfo _fReady, _fCrouching, _fAnimations, _fIdle, _fMovement, _fMoving, _fTarget, _fMixerParam, _fAnimancer, _fLegsOverride, _fActiveMixers;
        private static MethodInfo _mPlayOnLegs;

        private class Slot
        {
            public object Idle, Move, CIdle, CMove;
            public bool Resolved, LastCrouch, Reported;
            public Vector2 Param;
            public float NextDiag;
        }
        private static readonly Dictionary<object, Slot> _slots = new Dictionary<object, Slot>();
        private static int _diagLines;
        private const int MaxDiag = 90;

        public static void Apply(Harmony h)
        {
            _tPuppet = AccessTools.TypeByName("TGCoop.Sync.PuppetAnimancer");
            if (_tPuppet == null) { Plugin.Log.LogWarning("[LegsFix] PuppetAnimancer not found."); return; }
            _fReady = AccessTools.Field(_tPuppet, "_ready");
            _fCrouching = AccessTools.Field(_tPuppet, "_crouching");
            _fAnimations = AccessTools.Field(_tPuppet, "_animations");
            _fIdle = AccessTools.Field(_tPuppet, "_idle");
            _fMovement = AccessTools.Field(_tPuppet, "_movement");
            _fMoving = AccessTools.Field(_tPuppet, "_moving");
            _fTarget = AccessTools.Field(_tPuppet, "TargetLocomotion");
            _fMixerParam = AccessTools.Field(_tPuppet, "_mixerParam");
            _fAnimancer = AccessTools.Field(_tPuppet, "_animancer");
            _fLegsOverride = AccessTools.Field(_tPuppet, "_legsOverride");
            _fActiveMixers = AccessTools.Field(_tPuppet, "_activeMixers");
            _mPlayOnLegs = AccessTools.Method(_tPuppet, "PlayOnLegs");
            if (_fReady == null || _fCrouching == null || _fIdle == null || _fMovement == null || _fTarget == null || _fAnimancer == null || _mPlayOnLegs == null)
            { Plugin.Log.LogWarning("[LegsFix] some PuppetAnimancer members not found; disabled."); return; }
            MethodInfo upd = AccessTools.Method(_tPuppet, "Update");
            h.Patch(upd, new HarmonyMethod(typeof(LegsFix), "UpdatePrefix"), new HarmonyMethod(typeof(LegsFix), "UpdatePostfix"));
            Plugin.Log.LogInfo("[LegsFix] enabled (velocity-driven legs mixer + crouched legs).");
        }

        private static Slot GetSlot(object inst)
        {
            Slot s;
            if (!_slots.TryGetValue(inst, out s)) { s = new Slot(); _slots[inst] = s; }
            return s;
        }

        private static void Resolve(object inst, Slot s)
        {
            s.Resolved = true;
            s.Idle = _fIdle.GetValue(inst);
            s.Move = _fMovement.GetValue(inst);
            try
            {
                ARHeroAnimancerBaseAnimations anims = _fAnimations == null ? null : _fAnimations.GetValue(inst) as ARHeroAnimancerBaseAnimations;
                if (anims != null && anims.animationMappings != null)
                {
                    foreach (ARHeroStateToAnimationMapping map in anims.animationMappings)
                    {
                        if (map == null || map.layerType != HeroLayerType.Legs) continue;
                        if (s.CIdle == null) s.CIdle = AnimancerUtils.GetAnimancerNodes(HeroStateType.CrouchedIdle, map).FirstOrDefault();
                        if (s.CMove == null) s.CMove = AnimancerUtils.GetAnimancerNodes(HeroStateType.CrouchedMovement, map).FirstOrDefault();
                    }
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("[LegsFix] crouched clips lookup failed: " + e.Message); }
            Plugin.Log.LogInfo("[LegsFix] legs clips: idle=" + (s.Idle != null) + " move=" + (s.Move != null) + " crouchIdle=" + (s.CIdle != null) + " crouchMove=" + (s.CMove != null));
        }

        public static void UpdatePrefix(object __instance)
        {
            try
            {
                if (!(bool)_fReady.GetValue(__instance)) return;
                Slot s = GetSlot(__instance);
                if (!s.Resolved) Resolve(__instance, s);
                if (s.CIdle == null && s.CMove == null) return;
                bool crouch = (bool)_fCrouching.GetValue(__instance);
                if (crouch == s.LastCrouch) return;
                s.LastCrouch = crouch;
                object idle = crouch && s.CIdle != null ? s.CIdle : s.Idle;
                object move = crouch && s.CMove != null ? s.CMove : s.Move;
                _fIdle.SetValue(__instance, idle);
                _fMovement.SetValue(__instance, move);
                bool legsOverride = _fLegsOverride != null && (bool)_fLegsOverride.GetValue(__instance);
                if (!legsOverride)
                {
                    bool moving = _fMoving != null && (bool)_fMoving.GetValue(__instance);
                    _mPlayOnLegs.Invoke(__instance, new object[] { moving ? move : idle });
                }
                if (_diagLines < MaxDiag) { _diagLines++; Plugin.Log.LogInfo("[LegsFix] crouch=" + crouch + " -> legs clips swapped"); }
            }
            catch (Exception e) { if (_diagLines < MaxDiag) { _diagLines++; Plugin.Log.LogWarning("[LegsFix] prefix: " + e.Message); } }
        }

        public static void UpdatePostfix(object __instance)
        {
            try
            {
                if (!(bool)_fReady.GetValue(__instance)) return;
                Slot s = GetSlot(__instance);
                AnimancerComponent ac = _fAnimancer.GetValue(__instance) as AnimancerComponent;
                if (ac == null || ac.Layers.Count == 0) return;
                AnimancerState cur = ac.Layers[0].CurrentState;
                Vector2 target = (Vector2)_fTarget.GetValue(__instance);
                Vector2 want = new Vector2(target.y, target.x);           // same axis order the game's TppMovementState uses
                s.Param = Vector2.MoveTowards(s.Param, want, 25f * Time.deltaTime);
                MixerState<Vector2> mixer = cur as MixerState<Vector2>;
                bool legsOverride = _fLegsOverride != null && (bool)_fLegsOverride.GetValue(__instance);
                if (mixer != null && !legsOverride) mixer.Parameter = s.Param;

                if (Plugin.AnimDebug.Value && _diagLines < MaxDiag && Time.realtimeSinceStartup >= s.NextDiag)
                {
                    s.NextDiag = Time.realtimeSinceStartup + 2f;
                    _diagLines++;
                    int active = 0; try { var d = _fActiveMixers == null ? null : _fActiveMixers.GetValue(__instance) as System.Collections.ICollection; if (d != null) active = d.Count; } catch { }
                    Vector2 modParam = _fMixerParam == null ? Vector2.zero : (Vector2)_fMixerParam.GetValue(__instance);
                    Plugin.Log.LogInfo(string.Format("[LegsFix] target=({0:F2},{1:F2}) modParam=({2:F2},{3:F2}) ours=({4:F2},{5:F2}) moving={6} crouch={7} override={8} activeMixers={9} L0={10}",
                        target.x, target.y, modParam.x, modParam.y, s.Param.x, s.Param.y,
                        _fMoving != null && (bool)_fMoving.GetValue(__instance), (bool)_fCrouching.GetValue(__instance), legsOverride, active,
                        cur == null ? "none" : (cur.GetType().Name + (mixer != null ? " p=(" + mixer.Parameter.x.ToString("F2") + "," + mixer.Parameter.y.ToString("F2") + ")" : ""))));
                }
            }
            catch (Exception e) { if (_diagLines < MaxDiag) { _diagLines++; Plugin.Log.LogWarning("[LegsFix] postfix: " + e.Message); } }
        }
    }

    // =====================================================================================
    //  2g. MaskFix — upper-body layers must not cover the legs
    //      TGCoop asks the game only for the MainHand/OffHand third-person masks. For every other
    //      weapon layer (two-handed, hidden weapons, tools, fishing...) it falls back to the sender's
    //      FIRST-person mask "Mask_AllExceptHead", which covers the legs and freezes them at weight 1
    //      -> the clone slides. We substitute the game's third-person masks (CommonReferences.GetTppMask).
    // =====================================================================================
    internal static class MaskFix
    {
        private static FieldInfo _fAnimancer;
        private static readonly Dictionary<object, Dictionary<int, AvatarMask>> _cache = new Dictionary<object, Dictionary<int, AvatarMask>>();
        private static readonly HashSet<string> _logged = new HashSet<string>();

        public static void Apply(Harmony h)
        {
            Type puppet = AccessTools.TypeByName("TGCoop.Sync.PuppetAnimancer");
            MethodInfo m = puppet == null ? null : AccessTools.Method(puppet, "GetActionLayerMask");
            if (m == null) { Plugin.Log.LogWarning("[MaskFix] PuppetAnimancer.GetActionLayerMask not found; disabled."); return; }
            _fAnimancer = AccessTools.Field(puppet, "_animancer");
            h.Patch(m, null, new HarmonyMethod(typeof(MaskFix), "Postfix"));
            Plugin.Log.LogInfo("[MaskFix] enabled (runtime upper-body masks for the clone).");
        }

        private static void LogOnce(string key, string msg) { if (_logged.Add(key)) Plugin.Log.LogInfo("[MaskFix] " + msg); }

        private static AvatarMask TryGetTpp(HeroLayerType layer)
        {
            try { var refs = Awaken.TG.Main.Scenes.SceneConstructors.CommonReferences.Get; return refs == null ? null : refs.GetTppMask(layer); }
            catch { return null; }
        }

        private static bool IsLeftLayer(HeroLayerType l) { return l == HeroLayerType.OffHand || l == HeroLayerType.DualOffHand || l == HeroLayerType.ActiveOffHand || l == HeroLayerType.HeadOffHand; }
        private static bool IsRightLayer(HeroLayerType l) { return l == HeroLayerType.MainHand || l == HeroLayerType.DualMainHand || l == HeroLayerType.ActiveMainHand || l == HeroLayerType.HeadMainHand; }

        private static string Describe(AvatarMask m)
        {
            if (m == null) return "null";
            var parts = new List<string>();
            try { for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++) if (m.GetHumanoidBodyPartActive((AvatarMaskBodyPart)i)) parts.Add(((AvatarMaskBodyPart)i).ToString()); } catch { }
            return m.name + " [transforms=" + m.transformCount + ", humanoid=" + string.Join(",", parts.ToArray()) + "]";
        }

        // Humanoid rig: describe the mask by body parts (rig independent).
        private static AvatarMask BuildHumanoid(HeroLayerType layer)
        {
            var m = new AvatarMask();
            m.name = "TGCoopPlus_" + layer;
            for (int i = 0; i < (int)AvatarMaskBodyPart.LastBodyPart; i++) m.SetHumanoidBodyPartActive((AvatarMaskBodyPart)i, false);
            bool left = IsLeftLayer(layer), right = IsRightLayer(layer);
            if (!left && !right)   // two-handed, hidden weapons, tools, fishing, spyglass, overrides...
            {
                m.SetHumanoidBodyPartActive(AvatarMaskBodyPart.Body, true);
                left = true; right = true;
            }
            if (left) { m.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftArm, true); m.SetHumanoidBodyPartActive(AvatarMaskBodyPart.LeftFingers, true); }
            if (right) { m.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightArm, true); m.SetHumanoidBodyPartActive(AvatarMaskBodyPart.RightFingers, true); }
            return m;
        }

        private static readonly System.Text.RegularExpressions.Regex LowerBody = new System.Text.RegularExpressions.Regex(
            "thigh|calf|shin|knee|foot|toe|leg|pelvis|hips?$|^root$|^hips?_|_hips?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private static readonly System.Text.RegularExpressions.Regex LeftSide = new System.Text.RegularExpressions.Regex(
            "(^|[_\\.\\s])(l|left)([_\\.\\s]|$)|left|_l$|^l_", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private static readonly System.Text.RegularExpressions.Regex RightSide = new System.Text.RegularExpressions.Regex(
            "(^|[_\\.\\s])(r|right)([_\\.\\s]|$)|right|_r$|^r_", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private static readonly System.Text.RegularExpressions.Regex ArmBone = new System.Text.RegularExpressions.Regex(
            "clavicle|shoulder|upperarm|forearm|arm|elbow|hand|finger|thumb|index|middle|ring|pinky|wrist|weapon|socket", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        private static readonly HashSet<string> _dumped = new HashSet<string>();

        // Generic rig: copy the paths of a mask that is known to match the clone (the sender's FPP mask) and
        // keep only the upper body. Returns null when the source has no transform paths.
        private static AvatarMask BuildFromWorkingMask(HeroLayerType layer, AvatarMask src, out int kept, out int dropped)
        {
            kept = 0; dropped = 0;
            if (src == null || src.transformCount == 0) return null;
            bool left = IsLeftLayer(layer), right = IsRightLayer(layer);
            bool both = !left && !right;
            var m = new AvatarMask();
            m.name = "TGCoopPlus_" + layer + "_upper";
            int n = src.transformCount;
            m.transformCount = n;
            var dump = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++)
            {
                string path = src.GetTransformPath(i) ?? "";
                bool active = src.GetTransformActive(i);
                m.SetTransformPath(i, path);
                string leaf = path.Substring(path.LastIndexOf('/') + 1);
                bool on = active;
                if (on && LowerBody.IsMatch(leaf)) on = false;
                if (on && !both && ArmBone.IsMatch(leaf))
                {
                    bool isL = LeftSide.IsMatch(leaf), isR = RightSide.IsMatch(leaf);
                    if (left && isR && !isL) on = false;
                    if (right && isL && !isR) on = false;
                }
                m.SetTransformActive(i, on);
                if (on) kept++; else if (active) dropped++;
                if (_dumped.Count == 0) dump.Append(leaf).Append(active ? "" : "(off)").Append(on ? "" : "[cut]").Append(' ');
            }
            if (_dumped.Add("src")) Plugin.Log.LogInfo("[MaskFix] source mask '" + src.name + "' leaves: " + dump);
            return kept > 0 ? m : null;
        }

        // Generic rig: take the game's TPP mask and re-root its bone paths onto the clone hierarchy by bone name.
        private static AvatarMask BuildRerooted(HeroLayerType layer, AvatarMask tpp, Animator anim, out int matched, out int wanted)
        {
            matched = 0; wanted = 0;
            if (tpp == null || anim == null || tpp.transformCount == 0) return null;
            var m = new AvatarMask();
            m.name = "TGCoopPlus_" + layer + "_rerooted";
            m.AddTransformPath(anim.transform, true);
            var byName = new Dictionary<string, List<int>>();
            for (int i = 0; i < m.transformCount; i++)
            {
                m.SetTransformActive(i, false);
                string p = m.GetTransformPath(i); if (string.IsNullOrEmpty(p)) continue;
                string n = p.Substring(p.LastIndexOf('/') + 1);
                List<int> l; if (!byName.TryGetValue(n, out l)) { l = new List<int>(); byName[n] = l; }
                l.Add(i);
            }
            for (int j = 0; j < tpp.transformCount; j++)
            {
                if (!tpp.GetTransformActive(j)) continue;
                wanted++;
                string p = tpp.GetTransformPath(j); if (string.IsNullOrEmpty(p)) continue;
                string n = p.Substring(p.LastIndexOf('/') + 1);
                List<int> l;
                if (byName.TryGetValue(n, out l)) { foreach (int idx in l) m.SetTransformActive(idx, true); matched++; }
            }
            return matched > 0 ? m : null;
        }

        private static Type _tPuppet;
        private static Animator FindPuppetAnimator()
        {
            try
            {
                if (_tPuppet == null) _tPuppet = AccessTools.TypeByName("TGCoop.Sync.PuppetAnimancer");
                if (_tPuppet == null || _fAnimancer == null) return null;
                foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(_tPuppet))
                {
                    var mb = o as MonoBehaviour;
                    if (mb == null || !mb.gameObject.scene.IsValid()) continue;
                    AnimancerComponent ac = _fAnimancer.GetValue(mb) as AnimancerComponent;
                    if (ac != null && ac.Animator != null) return ac.Animator;
                }
            }
            catch { }
            return null;
        }

        // GetActionLayerMask is STATIC in TGCoop, so there is no __instance: cache per layer, keyed by the animator we built for.
        public static void Postfix(object[] __args, ref AvatarMask __result)
        {
            try
            {
                if (__args == null || __args.Length < 1 || !(__args[0] is HeroLayerType)) return;
                HeroLayerType layer = (HeroLayerType)__args[0];
                if (layer == HeroLayerType.Legs || layer == HeroLayerType.Idle || layer == HeroLayerType.CameraShakes) return;

                Animator anim = FindPuppetAnimator();
                object key = anim != null ? (object)anim : "no-animator";
                Dictionary<int, AvatarMask> perLayer;
                if (!_cache.TryGetValue(key, out perLayer)) { perLayer = new Dictionary<int, AvatarMask>(); _cache[key] = perLayer; }
                AvatarMask cached;
                if (perLayer.TryGetValue((int)layer, out cached) && cached != null) { __result = cached; return; }

                bool human = anim != null && anim.isHuman;
                AvatarMask tpp = TryGetTpp(layer); if (tpp == null) tpp = TryGetTpp(HeroLayerType.BothHands);

                LogOnce("info:" + layer, "layer " + layer + ": original=" + Describe(__result) + " gameTpp=" + Describe(tpp) + " animator.isHuman=" + human);

                AvatarMask built = null;
                string how = "";
                if (human) { built = BuildHumanoid(layer); how = "humanoid body parts"; }
                else
                {
                    // Generic rig. The sender's first-person mask is KNOWN to drive the clone's arms (its paths match),
                    // so copy its paths and switch off legs / pelvis / root (and the other arm for one-hand layers).
                    int kept, dropped;
                    built = BuildFromWorkingMask(layer, __result, out kept, out dropped);
                    how = "copy of " + (__result == null ? "null" : __result.name) + " minus lower body (" + kept + " on, " + dropped + " off)";
                    if (built == null)
                    {
                        int matched, wanted;
                        built = BuildRerooted(layer, tpp, anim, out matched, out wanted);
                        how = "re-rooted TPP mask (" + matched + "/" + wanted + " bones matched)";
                    }
                }
                if (built == null) { LogOnce("keep:" + layer, "layer " + layer + ": no usable mask built, keeping " + (__result == null ? "null" : __result.name)); return; }

                perLayer[(int)layer] = built;
                __result = built;
                LogOnce("use:" + layer, "layer " + layer + ": -> " + Describe(built) + " via " + how);
            }
            catch (Exception e)
            {
                LogOnce("err:" + e.Message, "error: " + e.Message);
            }
        }
    }

    // =====================================================================================
    //  2e. ErrorWatcher — count TGCoop / TGCoopPlus errors, keep the last few, export diagnostics
    // =====================================================================================
    internal class ErrorWatcher : ILogListener
    {
        public static int Errors, Warnings;
        public static readonly List<string> Recent = new List<string>();
        public static string LastError = "";
        public static float LastErrorAt = -999f;
        private const int Keep = 6;

        public void LogEvent(object sender, LogEventArgs e)
        {
            try
            {
                if (e == null || e.Source == null) return;
                string src = e.Source.SourceName ?? "";
                if (src.IndexOf("TGCoop", StringComparison.OrdinalIgnoreCase) < 0) return;
                if ((e.Level & (LogLevel.Error | LogLevel.Fatal)) != 0)
                {
                    Errors++;
                    string line = src + ": " + (e.Data == null ? "" : e.Data.ToString());
                    int nl = line.IndexOf('\n'); if (nl > 0) line = line.Substring(0, nl);
                    if (line.Length > 160) line = line.Substring(0, 160) + "…";
                    lock (Recent)
                    {
                        if (Recent.Count == 0 || Recent[Recent.Count - 1] != line) { Recent.Add(line); if (Recent.Count > Keep) Recent.RemoveAt(0); }
                    }
                    LastError = line; LastErrorAt = Time.realtimeSinceStartup;
                }
                else if ((e.Level & LogLevel.Warning) != 0) Warnings++;
            }
            catch { }
        }

        public void Dispose() { }

        public static void Clear() { Errors = 0; Warnings = 0; lock (Recent) Recent.Clear(); LastError = ""; }

        /// Copy LogOutput.log + configs + a summary into Desktop\TGCoop_diag_<time>\ and return the folder.
        public static string ExportDiagnostics()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string dir = System.IO.Path.Combine(desktop, "TGCoop_diag_" + DateTime.Now.ToString("yyyyMMdd_HHmm"));
            System.IO.Directory.CreateDirectory(dir);
            string log = System.IO.Path.Combine(Paths.BepInExRootPath, "LogOutput.log");
            if (System.IO.File.Exists(log))
            {
                // the file is open for writing by BepInEx; copy via shared read
                using (var fs = new System.IO.FileStream(log, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                using (var outFs = new System.IO.FileStream(System.IO.Path.Combine(dir, "LogOutput.log"), System.IO.FileMode.Create))
                    fs.CopyTo(outFs);
            }
            if (System.IO.Directory.Exists(Paths.ConfigPath))
                foreach (string cfg in System.IO.Directory.GetFiles(Paths.ConfigPath, "*.cfg"))
                    System.IO.File.Copy(cfg, System.IO.Path.Combine(dir, System.IO.Path.GetFileName(cfg)), true);
            string verFile = System.IO.Path.Combine(Paths.BepInExRootPath, "tgcoop_version.txt");
            string kitVer = System.IO.File.Exists(verFile) ? System.IO.File.ReadAllText(verFile).Trim() : "(none)";
            string me = ""; try { me = SteamFriends.GetPersonaName() + " (" + SteamUser.GetSteamID().m_SteamID + ")"; } catch { }
            string role = !Coop.InLobby ? "not connected" : (Coop.IsHost ? "HOST" : "CLIENT");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("TGCoop diagnostics  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("Player: " + me);
            sb.AppendLine("Role: " + role + "   Connected: " + Coop.Connected);
            sb.AppendLine("TGCoopPlus: " + Plugin.Version + "   kit commit: " + kitVer);
            sb.AppendLine("Game version: " + Application.version + "   Unity: " + Application.unityVersion);
            sb.AppendLine("Errors: " + Errors + "   Warnings: " + Warnings);
            sb.AppendLine("Recent errors:");
            lock (Recent) foreach (string r in Recent) sb.AppendLine("  " + r);
            System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "summary.txt"), sb.ToString());
            Plugin.Log.LogInfo("[ErrorWatcher] diagnostics exported to " + dir);
            return dir;
        }
    }

    // =====================================================================================
    //  3. Panel — IMGUI window for host / client control
    // =====================================================================================
    internal static class Panel
    {
        private static bool _open;
        private static Rect _rect = new Rect(40, 100, 520, 560);
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
            EnsureFont();
            Font prev = GUI.skin.font;
            if (_font != null) GUI.skin.font = _font;
            if (!_open)
            {
                // small banner so errors are noticed even with the panel closed
                bool fresh = Time.realtimeSinceStartup - ErrorWatcher.LastErrorAt < 12f;
                if (ErrorWatcher.Errors > 0 && (fresh || Time.realtimeSinceStartup % 30f < 6f))
                {
                    GUI.color = new Color(1f, 0.45f, 0.4f);
                    string txt = T("TGCoop 錯誤 ", "TGCoop errors ") + ErrorWatcher.Errors + (fresh ? "  |  " + ErrorWatcher.LastError : "") + T("   （" + Plugin.PanelKey.Value + " 開面板）", "   (" + Plugin.PanelKey.Value + " for panel)");
                    GUI.Label(new Rect(10, 8, Screen.width - 20, 24), txt);
                    GUI.color = Color.white;
                }
                GUI.skin.font = prev;
                return;
            }
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

            // ---- errors / diagnostics ----
            GUILayout.Space(6);
            if (ErrorWatcher.Errors > 0)
            {
                GUI.color = new Color(1f, 0.6f, 0.55f);
                GUILayout.Label(T("錯誤 ", "Errors ") + ErrorWatcher.Errors + T("、警告 ", ", warnings ") + ErrorWatcher.Warnings + T("。最近：", ". Recent:"));
                lock (ErrorWatcher.Recent) foreach (string r in ErrorWatcher.Recent) GUILayout.Label("  " + r);
                GUI.color = Color.white;
            }
            else GUILayout.Label(T("目前沒有 TGCoop 錯誤。", "No TGCoop errors so far."));
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(T("匯出診斷到桌面（log + 設定）", "Export diagnostics to Desktop"), GUILayout.Height(28)))
            {
                try { string dir = ErrorWatcher.ExportDiagnostics(); Flash(T("已匯出：", "Exported: ") + dir); }
                catch (Exception e) { Flash(T("匯出失敗：", "Export failed: ") + e.Message); }
            }
            if (ErrorWatcher.Errors > 0 && GUILayout.Button(T("清除", "Clear"), GUILayout.Width(70), GUILayout.Height(28))) ErrorWatcher.Clear();
            GUILayout.EndHorizontal();

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
