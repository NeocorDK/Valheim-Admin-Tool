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
        public const string pluginVersion = "0.3.0";

        private static ManualLogSource logger;

        public static ConfigEntry<int> AgentPort;
        public static ConfigEntry<string> AgentSecretFile;
        public static ConfigEntry<bool> AllowServerCommands;
        public static ConfigEntry<bool> AllowRestore;
        public static ConfigEntry<bool> IsDebug;

        public static ConfigEntry<bool> MapEnabled;
        public static ConfigEntry<int> MapTextureSize;
        public static ConfigEntry<float> MapPixelSize;
        public static ConfigEntry<bool> MapDrawInBackground;
        public static ConfigEntry<bool> MapPublicFog;
        public static ConfigEntry<string> MapPublicPlayers;
        public static ConfigEntry<bool> MapPublicPortals;
        public static ConfigEntry<bool> MapPublicLocations;

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

            AgentPort = Config.Bind("Server", "AgentPort", 0,
                "Port of the Valheim Admin agent on 127.0.0.1. Only needed when the server is started without the agent; " +
                "the agent passes it through the VA_AGENT_PORT environment variable. 0 = disabled.");
            AgentSecretFile = Config.Bind("Server", "AgentSecretFile", "",
                "File holding the agent's bridge secret (data\\bridge.secret next to the agent). Only needed together with AgentPort.");
            AllowServerCommands = Config.Bind("Client", "AllowServerCommands", true,
                "Let the server admin run console commands (god, fly, spawn, ...) on this character through the admin panel.");
            AllowRestore = Config.Bind("Client", "AllowRestore", true,
                "Let the server admin restore items and skills of this character from a snapshot.");
            IsDebug = Config.Bind("General", "Debug", false, "Verbose logging.");

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
            MapPublicLocations = Config.Bind("Map", "PublicLocations", false, "Show location icons (bosses, traders, start) on the public map, in explored areas.");

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
        }
    }
}
