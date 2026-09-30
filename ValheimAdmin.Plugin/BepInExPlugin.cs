using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System.Reflection;

[assembly: AssemblyTitle(ValheimAdmin.BepInExPlugin.pluginName)]
[assembly: AssemblyProduct(ValheimAdmin.BepInExPlugin.pluginName)]
[assembly: AssemblyVersion(ValheimAdmin.BepInExPlugin.pluginVersion + ".0")]
[assembly: AssemblyFileVersion(ValheimAdmin.BepInExPlugin.pluginVersion + ".0")]

namespace ValheimAdmin
{
    /// <summary>
    /// One DLL, two roles.
    ///
    /// On the dedicated server it connects to the Valheim Admin agent (a separate process that
    /// starts and watches the server) and carries out its commands: saves, kicks, broadcasts,
    /// global keys, and so on. It also reports joins, leaves, deaths, chat, raids and world saves.
    ///
    /// On a player's machine it answers the server: takes a snapshot of the local character
    /// (inventory including m_customData, which holds Epic Loot enchantments, plus skills),
    /// restores items and skills from a snapshot, and runs console commands sent by the admin.
    /// The client role is optional; the server works with players who do not have it.
    /// </summary>
    [BepInPlugin(pluginGuid, pluginName, pluginVersion)]
    public class BepInExPlugin : BaseUnityPlugin
    {
        public const string pluginGuid = "neocor.ValheimAdmin";
        public const string pluginName = "Valheim Admin";
        public const string pluginVersion = "0.4.0";

        private static ManualLogSource logger;

        public static BepInExPlugin Instance { get; private set; }

        public static ConfigEntry<int> AgentPort;
        public static ConfigEntry<string> AgentSecretFile;
        public static ConfigEntry<bool> AllowServerCommands;
        public static ConfigEntry<bool> AllowRestore;
        public static ConfigEntry<bool> SharePins;
        public static ConfigEntry<bool> IsDebug;

        public static ConfigEntry<bool> WebEnabled;
        public static ConfigEntry<int> WebPort;
        public static ConfigEntry<string> WebBind;
        public static ConfigEntry<string> WebAdminPassword;
        public static ConfigEntry<string> WebAdminPasswordHash;
        public static ConfigEntry<string> WebLanguage;
        public static ConfigEntry<string> WebDataDir;
        public static ConfigEntry<int> WebKeepAllDays;
        public static ConfigEntry<int> WebKeepDailyDays;
        public static ConfigEntry<string> WebIgnoreDataKeys;

        public static ConfigEntry<bool> MapEnabled;
        public static ConfigEntry<int> MapTextureSize;
        public static ConfigEntry<float> MapPixelSize;
        public static ConfigEntry<bool> MapDrawInBackground;
        public static ConfigEntry<bool> MapPublicFog;
        public static ConfigEntry<string> MapPublicPlayers;
        public static ConfigEntry<bool> MapPublicPortals;
        public static ConfigEntry<bool> MapPublicLocations;
        public static ConfigEntry<bool> MapPublicSpawners;
        public static ConfigEntry<bool> MapPublicGamePins;

        public static void Log(string str)
        {
            if (logger != null)
                logger.LogInfo(str);
        }

        public static void Warn(string str)
        {
            if (logger != null)
                logger.LogWarning(str);
        }

        public static void Dbgl(string str)
        {
            if (logger != null && IsDebug != null && IsDebug.Value)
                logger.LogInfo(str);
        }

        public void Awake()
        {
            logger = Logger;
            Instance = this;

            AgentPort = Config.Bind("Server", "AgentPort", 0,
                "Port of the Valheim Admin agent on 127.0.0.1. Only needed when the server is started without the agent; " +
                "the agent passes it through the VA_AGENT_PORT environment variable. 0 = disabled.");
            AgentSecretFile = Config.Bind("Server", "AgentSecretFile", "",
                "File holding the agent's bridge secret (data\\bridge.secret next to the agent). Only needed together with AgentPort.");
            AllowServerCommands = Config.Bind("Client", "AllowServerCommands", true,
                "Let the server admin run console commands (god, fly, spawn, ...) on this character through the admin panel.");
            AllowRestore = Config.Bind("Client", "AllowRestore", true,
                "Let the server admin restore items and skills of this character from a snapshot.");
            SharePins = Config.Bind("Client", "SharePins", true,
                "Show the pins of your in-game map to the server admin on the admin panel's map (never on the public map).");
            IsDebug = Config.Bind("General", "Debug", false, "Verbose logging.");

            WebEnabled = Config.Bind("Web", "Enabled", true,
                "Standalone mode: when the server runs without the Valheim Admin agent (rented hosts, Linux), serve the web panel " +
                "from the game itself. Ignored when the agent is present; it serves the panel then.");
            WebPort = Config.Bind("Web", "Port", 8095, "TCP port of the web panel in standalone mode. Your host must allow incoming connections to it.");
            WebBind = Config.Bind("Web", "Bind", "*", "Address to listen on: * for all, or one IP address.");
            WebAdminPassword = Config.Bind("Web", "AdminPassword", "",
                "Put a new admin password here; on start (or the next sign-in) it is replaced by a hash in AdminPasswordHash. " +
                "When neither is set, a random password is written to the BepInEx log.");
            WebAdminPasswordHash = Config.Bind("Web", "AdminPasswordHash", "", "Hash of the admin password (PBKDF2). Clear it and set AdminPassword to change the password.");
            WebLanguage = Config.Bind("Web", "Language", "en", new ConfigDescription("Language of event log texts.", new AcceptableValueList<string>("en", "ru")));
            WebDataDir = Config.Bind("Web", "DataDir", "", "Folder for the event log, snapshots and icons in standalone mode. Empty = BepInEx/config/ValheimAdmin.");
            WebKeepAllDays = Config.Bind("Web", "SnapshotKeepAllDays", 3, "Keep every snapshot this many days (standalone mode).");
            WebKeepDailyDays = Config.Bind("Web", "SnapshotKeepDailyDays", 60, "Then keep one snapshot per day for this many days (standalone mode).");
            WebIgnoreDataKeys = Config.Bind("Web", "IgnoreDataKeys", "",
                "Comma-separated item custom data keys (or prefix*) that Compare ignores (standalone mode), for mods that change item data during play.");

            MapEnabled = Config.Bind("Map", "Enabled", true,
                "Draw the world map on the dedicated server for the web panel. The admin always sees the whole map, also with the nomap world key.");
            MapTextureSize = Config.Bind("Map", "TextureSize", 2048,
                "Map picture size in pixels (power of two). With PixelSize 12 the default covers the vanilla world; raise it for worlds enlarged by mods.");
            MapPixelSize = Config.Bind("Map", "PixelSize", 12f, "Metres per map pixel (the game's minimap uses 12).");
            MapDrawInBackground = Config.Bind("Map", "DrawInBackground", true,
                "Draw the map on a background thread. Turn off if a world generation mod misbehaves; it is then drawn a few milliseconds per frame.");
            MapPublicFog = Config.Bind("Map", "PublicFog", true,
                "The public map (no login) shows only areas that players have been near. The admin sees everything.");
            MapPublicPlayers = Config.Bind("Map", "PublicPlayers", "respect",
                new ConfigDescription("Players on the public map: respect = only those with \"Visible on map\" on in the game, all, none.",
                    new AcceptableValueList<string>("respect", "all", "none")));
            MapPublicPortals = Config.Bind("Map", "PublicPortals", false, "Show portals (with their tags) on the public map, in explored areas.");
            MapPublicLocations = Config.Bind("Map", "PublicLocations", false,
                "Show the world's locations (boss altars, dungeons, traders, runestones, camps, ...) on the public map, in explored areas.");
            MapPublicSpawners = Config.Bind("Map", "PublicSpawners", false,
                "Show mob spawners (greydwarf nests, bone piles, ...) on the public map, in explored areas.");
            MapPublicGamePins = Config.Bind("Map", "PublicGamePins", false,
                "Show the pins players wrote to cartography tables on the public map, in explored areas. Personal pins are never public.");

            new Harmony(pluginGuid).PatchAll(typeof(BepInExPlugin).Assembly);
            AgentLink.Start();
            Log("Ready.");
        }

        public void Update()
        {
            MainThread.Pump();
            ServerRole.Update();
        }

        public void OnDestroy()
        {
            AgentLink.Stop();
            Web.StandaloneServer.Stop();
        }
    }
}
