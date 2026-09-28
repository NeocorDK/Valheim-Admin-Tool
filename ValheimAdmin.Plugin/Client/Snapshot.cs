using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace ValheimAdmin
{
    /// <summary>
    /// Serializes the local character's inventory and skills without knowing any mod.
    ///
    /// Every item is stored twice: as readable fields for the panel (including the whole
    /// m_customData, where mods such as Epic Loot, Jewelcrafting or Adventure Backpacks keep
    /// their data) and as the game's own item bytes (ItemDrop.ItemData.Save). A restore loads
    /// those bytes through Inventory.Load, the same path the game uses for a saved character, so
    /// mods that hook item loading see a normal load.
    /// </summary>
    public static class Snapshot
    {
        /// <summary>1: fields only. 2: adds item bytes, extra inventories, player custom data, skill names.</summary>
        public const int FormatVersion = 2;

        public static Dictionary<string, object> Build(Player player, string trigger)
        {
            Inventory inventory = player.GetInventory();
            var items = new List<object>();
            foreach (ItemDrop.ItemData item in inventory.GetAllItems())
                AddItem(items, player, item, null);

            var containers = new List<object>();
            foreach (var extra in ExtraInventories.Find(player))
            {
                containers.Add(new Dictionary<string, object>
                {
                    { "key", extra.Key },
                    { "w", extra.Inventory.GetWidth() },
                    { "h", extra.Inventory.GetHeight() },
                });
                foreach (ItemDrop.ItemData item in extra.Inventory.GetAllItems())
                    AddItem(items, player, item, extra.Key);
            }

            var skills = new List<object>();
            foreach (Skills.Skill skill in player.GetSkills().GetSkillList())
            {
                if (skill?.m_info == null) continue;
                Skills.SkillType type = skill.m_info.m_skill;
                skills.Add(new Dictionary<string, object>
                {
                    { "type", (int)type },
                    { "name", type.ToString() },
                    { "displayName", SkillName(type) },
                    { "level", skill.m_level },
                    { "acc", skill.m_accumulator },
                });
            }

            var playerData = new Dictionary<string, object>();
            if (player.m_customData != null)
                foreach (var kv in player.m_customData)
                    playerData[kv.Key] = kv.Value;

            Vector3 pos = player.transform.position;
            return new Dictionary<string, object>
            {
                { "v", FormatVersion },
                { "trigger", trigger },
                { "name", player.GetPlayerName() },
                { "characterId", Game.instance.GetPlayerProfile().GetPlayerID() },
                { "takenAt", DateTime.UtcNow.ToString("o") },
                { "itemVersion", ItemBytes.Version },
                { "inventory", new Dictionary<string, object> { { "w", inventory.GetWidth() }, { "h", inventory.GetHeight() } } },
                { "containers", containers },
                { "items", items },
                { "skills", skills },
                { "playerData", playerData },
                { "health", Math.Round(player.GetHealth(), 1) },
                { "pos", new[] { Math.Round(pos.x), Math.Round(pos.y), Math.Round(pos.z) } },
            };
        }

        /// <summary>The name the skills dialog shows; works for skills added by mods too.</summary>
        public static string SkillName(Skills.SkillType type)
        {
            try
            {
                return Localization.instance.Localize("$skill_" + type.ToString().ToLower());
            }
            catch
            {
                return type.ToString();
            }
        }

        private static void AddItem(List<object> items, Player player, ItemDrop.ItemData item, string container)
        {
            GameObject prefab = item.m_dropPrefab != null ? item.m_dropPrefab : ItemBytes.FindPrefab(item);
            if (prefab == null)
            {
                BepInExPlugin.Dbgl("Snapshot skips an item without a prefab: " + item.m_shared?.m_name);
                return;
            }
            items.Add(Item(player, item, prefab, container));
        }

        private static Dictionary<string, object> Item(Player player, ItemDrop.ItemData item, GameObject prefab, string container)
        {
            string tooltip = null;
            try
            {
                // Mods that patch the tooltip (Epic Loot, Jewelcrafting, ...) show their lines here too.
                tooltip = Localization.instance.Localize(item.GetTooltip(-1));
            }
            catch (Exception e)
            {
                BepInExPlugin.Dbgl("Tooltip of " + prefab.name + " failed: " + e.Message);
            }

            var data = new Dictionary<string, object>();
            if (item.m_customData != null)
                foreach (var kv in item.m_customData)
                    data[kv.Key] = kv.Value;

            string raw = null;
            try
            {
                raw = ItemBytes.Save(item, prefab);
            }
            catch (Exception e)
            {
                BepInExPlugin.Warn("Saving " + prefab.name + " failed: " + e.Message);
            }

            var json = new Dictionary<string, object>
            {
                { "prefab", prefab.name },
                { "token", item.m_shared.m_name },
                { "label", Localization.instance.Localize(item.m_shared.m_name) },
                { "tooltip", tooltip },
                { "itemType", item.m_shared.m_itemType.ToString() },
                { "maxStack", item.m_shared.m_maxStackSize },
                { "stack", item.m_stack },
                { "durability", item.m_durability },
                { "maxDurability", item.GetMaxDurability() },
                { "x", item.m_gridPos.x },
                { "y", item.m_gridPos.y },
                { "equipped", player.IsItemEquiped(item) },
                { "quality", item.m_quality },
                { "variant", item.m_variant },
                { "crafterId", item.m_crafterID },
                { "crafterName", item.m_crafterName },
                { "worldLevel", item.m_worldLevel },
                { "pickedUp", item.m_pickedUp },
                { "cheated", item.m_cheated },
                { "data", data },
                { "raw", raw },
            };
            if (container != null) json["container"] = container;
            return json;
        }
    }

    /// <summary>The game's own item serialization, used to snapshot and rebuild items exactly.</summary>
    public static class ItemBytes
    {
        private static int version;

        /// <summary>Item format version the running game writes (Inventory.Save's header).</summary>
        public static int Version
        {
            get
            {
                if (version == 0)
                {
                    var pkg = new ZPackage();
                    new Inventory("ValheimAdmin", null, 1, 1).Save(pkg);
                    pkg.SetPos(0);
                    version = pkg.ReadInt();
                }
                return version;
            }
        }

        public static string Save(ItemDrop.ItemData item, GameObject prefab)
        {
            GameObject old = item.m_dropPrefab;
            item.m_dropPrefab = prefab;
            try
            {
                var pkg = new ZPackage();
                item.Save(pkg);
                return Convert.ToBase64String(pkg.GetArray());
            }
            finally
            {
                item.m_dropPrefab = old;
            }
        }

        /// <summary>Rebuilds an item by loading its bytes into a scratch inventory, as the game loads a character.</summary>
        public static ItemDrop.ItemData Load(string raw, int itemVersion)
        {
            byte[] item = Convert.FromBase64String(raw);
            var header = new ZPackage();
            header.Write(itemVersion);
            header.Write((ushort)1);
            byte[] head = header.GetArray();
            var all = new byte[head.Length + item.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(item, 0, all, head.Length, item.Length);

            var scratch = new Inventory("ValheimAdmin restore", null, 8, 4);
            scratch.Load(new ZPackage(all));
            return scratch.GetAllItems().FirstOrDefault();
        }

        /// <summary>For items a mod created without m_dropPrefab: the ObjectDB item with the same name token.</summary>
        public static GameObject FindPrefab(ItemDrop.ItemData item)
        {
            string token = item.m_shared?.m_name;
            if (string.IsNullOrEmpty(token) || ObjectDB.instance == null) return null;
            foreach (GameObject go in ObjectDB.instance.m_items)
            {
                ItemDrop drop = go != null ? go.GetComponent<ItemDrop>() : null;
                if (drop != null && drop.m_itemData.m_shared.m_name == token)
                    return go;
            }
            return null;
        }
    }

    /// <summary>
    /// Inventories that mods attach to the player besides the main one (equipment or quick slots
    /// kept in a separate Inventory). Found by reflection, so no mod is referenced; mods that
    /// only enlarge the main inventory need nothing here.
    /// </summary>
    public static class ExtraInventories
    {
        public sealed class Found
        {
            public string Key;
            public Inventory Inventory;
        }

        public static List<Found> Find(Player player)
        {
            var result = new List<Found>();
            var seen = new HashSet<Inventory> { player.GetInventory() };
            Assembly game = typeof(Player).Assembly;
            foreach (Component component in player.GetComponents<Component>())
            {
                if (component == null) continue;
                Type type = component.GetType();
                if (type.Assembly == game || type.Assembly == typeof(Component).Assembly) continue;
                try
                {
                    foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (field.FieldType != typeof(Inventory)) continue;
                        if (field.GetValue(component) is Inventory inventory && seen.Add(inventory))
                            result.Add(new Found { Key = type.FullName + "." + field.Name, Inventory = inventory });
                    }
                }
                catch (Exception e)
                {
                    BepInExPlugin.Dbgl("Looking for inventories on " + type.FullName + " failed: " + e.Message);
                }
            }
            return result;
        }
    }

    /// <summary>Applies a restore request built by the agent from a snapshot.</summary>
    public static class Restorer
    {
        public class Report
        {
            public int Added;
            public int Dropped;
            public int Skills;
            public readonly List<object> Failed = new List<object>();

            public Dictionary<string, object> ToJson()
            {
                return new Dictionary<string, object>
                {
                    { "added", Added },
                    { "dropped", Dropped },
                    { "skills", Skills },
                    { "failed", Failed },
                };
            }
        }

        /// <summary>
        /// Request: { mode: "add" | "replace", itemVersion, containers: [{key}], items: [snapshot
        /// items, stack may be reduced], skills: [snapshot skills], skillMode: "none" | "raise" | "set" }.
        /// </summary>
        public static Report Apply(Player player, Dictionary<string, object> request)
        {
            var report = new Report();
            bool replace = request.Str("mode", "add") == "replace";
            int itemVersion = request.Int("itemVersion");
            Inventory inventory = player.GetInventory();
            var extras = ExtraInventories.Find(player).ToDictionary(f => f.Key, f => f.Inventory);

            if (replace)
            {
                player.UnequipAllItems();
                inventory.RemoveAll();
                // Only mod inventories the snapshot knows about are emptied.
                foreach (object o in request.List("containers") ?? new List<object>())
                {
                    string key = (o as Dictionary<string, object>).Str("key");
                    if (key != null && extras.TryGetValue(key, out Inventory extra))
                        extra.RemoveAll();
                }
            }

            var toEquip = new List<ItemDrop.ItemData>();
            foreach (object o in request.List("items") ?? new List<object>())
            {
                var j = o as Dictionary<string, object>;
                ItemDrop.ItemData item = null;
                try
                {
                    item = Make(j, itemVersion);
                }
                catch (Exception e)
                {
                    BepInExPlugin.Warn("Rebuilding " + j.Str("prefab") + " failed: " + e);
                }
                if (item == null)
                {
                    report.Failed.Add(new Dictionary<string, object> { { "prefab", j.Str("prefab") }, { "reason", "unknown prefab (is its mod installed?)" } });
                    continue;
                }

                bool wasEquipped = j.Bool("equipped");
                item.m_equipped = false;
                Inventory target = inventory;
                string container = j.Str("container");
                if (container != null && !(replace && extras.TryGetValue(container, out target)))
                    target = inventory;

                bool placed = false;
                if (replace && (container == null || target != inventory))
                {
                    int x = j.Int("x", -1), y = j.Int("y", -1);
                    if (x >= 0 && y >= 0 && x < target.GetWidth() && y < target.GetHeight() && target.GetItemAt(x, y) == null)
                        placed = target.AddItem(item, new Vector2i(x, y));
                    if (placed)
                    {
                        report.Added++;
                        if (wasEquipped && target == inventory) toEquip.Add(item);
                    }
                }
                if (!placed)
                {
                    AddOrDrop(player, item, report);
                    if (replace && wasEquipped && inventory.ContainsItem(item)) toEquip.Add(item);
                }
            }

            foreach (ItemDrop.ItemData item in toEquip)
                player.EquipItem(item, false);

            string skillMode = request.Str("skillMode", "none");
            if (skillMode != "none")
                RestoreSkills(player, request.List("skills"), skillMode == "set", report);

            player.Message(MessageHud.MessageType.Center, Texts.Restored(report.Added + report.Dropped, report.Dropped, report.Skills));
            BepInExPlugin.Log("Restore: added " + report.Added + ", dropped " + report.Dropped + ", skills " + report.Skills + ", failed " + report.Failed.Count);
            return report;
        }

        /// <summary>Rebuilds a snapshot item: from the game's bytes when the snapshot has them, else from the fields.</summary>
        public static ItemDrop.ItemData Make(Dictionary<string, object> j, int itemVersion)
        {
            if (j == null) return null;
            string raw = j.Str("raw");
            if (!string.IsNullOrEmpty(raw) && itemVersion > 0)
            {
                ItemDrop.ItemData loaded = ItemBytes.Load(raw, itemVersion);
                if (loaded == null) return null;
                int stack = j.Int("stack", loaded.m_stack);
                if (stack > 0 && stack < loaded.m_stack) loaded.m_stack = stack;
                return loaded;
            }

            GameObject go = ObjectDB.instance.GetItemPrefab(j.Str("prefab", ""));
            ItemDrop drop = go != null ? go.GetComponent<ItemDrop>() : null;
            if (drop == null) return null;

            ItemDrop.ItemData item = drop.m_itemData.Clone();
            item.m_dropPrefab = go;
            item.m_stack = Math.Max(1, j.Int("stack", 1));
            item.m_quality = Math.Max(1, j.Int("quality", 1));
            item.m_variant = j.Int("variant");
            item.m_durability = (float)j.Double("durability", item.GetMaxDurability());
            item.m_crafterID = j.Long("crafterId");
            item.m_crafterName = j.Str("crafterName", "");
            item.m_worldLevel = j.Int("worldLevel");
            item.m_pickedUp = j.Bool("pickedUp", true);
            item.m_cheated = j.Bool("cheated");
            item.m_equipped = false;
            item.m_customData = new Dictionary<string, string>();
            var data = j.Obj("data");
            if (data != null)
                foreach (var kv in data)
                    item.m_customData[kv.Key] = kv.Value as string ?? Json.Serialize(kv.Value);
            return item;
        }

        /// <summary>Puts the item into the inventory, or at the player's feet when it does not fit.</summary>
        public static void AddOrDrop(Player player, ItemDrop.ItemData item, Report report)
        {
            Inventory inventory = player.GetInventory();
            if (inventory.CanAddItem(item, item.m_stack) && inventory.AddItem(item))
            {
                report.Added++;
                return;
            }
            Transform t = player.transform;
            ItemDrop.DropItem(item, item.m_stack, t.position + t.forward + Vector3.up, t.rotation);
            report.Dropped++;
        }

        private static void RestoreSkills(Player player, List<object> skills, bool exact, Report report)
        {
            if (skills == null) return;
            Skills playerSkills = player.GetSkills();
            foreach (object o in skills)
            {
                var j = o as Dictionary<string, object>;
                if (j == null) continue;
                var type = (Skills.SkillType)j.Int("type");
                try
                {
                    // GetSkill would store a skill without definition when its mod is missing and break the character.
                    if (playerSkills.GetSkillDef(type) == null)
                    {
                        report.Failed.Add(new Dictionary<string, object>
                        {
                            { "skill", j.Str("displayName") ?? j.Str("name") },
                            { "reason", "skill not available (is its mod installed?)" },
                        });
                        continue;
                    }
                    Skills.Skill skill = playerSkills.GetSkill(type);
                    float level = (float)j.Double("level");
                    if (!exact && level <= skill.m_level) continue;
                    skill.m_level = level;
                    skill.m_accumulator = (float)j.Double("acc");
                    report.Skills++;
                }
                catch (Exception e)
                {
                    report.Failed.Add(new Dictionary<string, object> { { "skill", j.Str("displayName") ?? j.Str("name") }, { "reason", e.Message } });
                }
            }
        }
    }
}
