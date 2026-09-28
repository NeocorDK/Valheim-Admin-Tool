using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimAdmin
{
    /// <summary>
    /// Serializes the local character's inventory and skills. Every item keeps its whole
    /// m_customData, which is where Epic Loot stores enchantments and Adventure Backpacks
    /// stores backpack contents, so a restore needs neither mod's API.
    /// </summary>
    public static class Snapshot
    {
        public const int FormatVersion = 1;

        public static Dictionary<string, object> Build(Player player, string trigger)
        {
            Inventory inventory = player.GetInventory();
            var items = new List<object>();
            foreach (ItemDrop.ItemData item in inventory.GetAllItems())
            {
                if (item.m_dropPrefab == null) continue;
                items.Add(Item(player, item));
            }

            var skills = new List<object>();
            foreach (Skills.Skill skill in player.GetSkills().GetSkillList())
            {
                skills.Add(new Dictionary<string, object>
                {
                    { "type", (int)skill.m_info.m_skill },
                    { "name", skill.m_info.m_skill.ToString() },
                    { "level", skill.m_level },
                    { "acc", skill.m_accumulator },
                });
            }

            Vector3 pos = player.transform.position;
            return new Dictionary<string, object>
            {
                { "v", FormatVersion },
                { "trigger", trigger },
                { "name", player.GetPlayerName() },
                { "characterId", Game.instance.GetPlayerProfile().GetPlayerID() },
                { "takenAt", DateTime.UtcNow.ToString("o") },
                { "inventory", new Dictionary<string, object> { { "w", inventory.GetWidth() }, { "h", inventory.GetHeight() } } },
                { "items", items },
                { "skills", skills },
                { "health", Math.Round(player.GetHealth(), 1) },
                { "pos", new[] { Math.Round(pos.x), Math.Round(pos.y), Math.Round(pos.z) } },
            };
        }

        private static Dictionary<string, object> Item(Player player, ItemDrop.ItemData item)
        {
            string tooltip = null;
            try
            {
                // Epic Loot patches the tooltip, so the panel shows the same enchantment lines as the game.
                tooltip = Localization.instance.Localize(item.GetTooltip(-1));
            }
            catch (Exception e)
            {
                BepInExPlugin.Dbgl("Tooltip of " + item.m_dropPrefab.name + " failed: " + e.Message);
            }

            var data = new Dictionary<string, object>();
            if (item.m_customData != null)
                foreach (var kv in item.m_customData)
                    data[kv.Key] = kv.Value;

            return new Dictionary<string, object>
            {
                { "prefab", item.m_dropPrefab.name },
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
                { "data", data },
            };
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
        /// Request: { mode: "add" | "replace", items: [snapshot items, stack may be reduced],
        /// skills: [snapshot skills], skillMode: "none" | "raise" | "set" }.
        /// </summary>
        public static Report Apply(Player player, Dictionary<string, object> request)
        {
            var report = new Report();
            bool replace = request.Str("mode", "add") == "replace";
            Inventory inventory = player.GetInventory();

            if (replace)
            {
                player.UnequipAllItems();
                inventory.RemoveAll();
            }

            var toEquip = new List<ItemDrop.ItemData>();
            foreach (object o in request.List("items") ?? new List<object>())
            {
                var j = o as Dictionary<string, object>;
                ItemDrop.ItemData item = Make(j);
                if (item == null)
                {
                    report.Failed.Add(new Dictionary<string, object> { { "prefab", j.Str("prefab") }, { "reason", "unknown prefab" } });
                    continue;
                }

                bool placed = false;
                if (replace)
                {
                    int x = j.Int("x", -1), y = j.Int("y", -1);
                    if (x >= 0 && y >= 0 && x < inventory.GetWidth() && y < inventory.GetHeight() && inventory.GetItemAt(x, y) == null)
                        placed = inventory.AddItem(item, new Vector2i(x, y));
                    if (placed)
                    {
                        report.Added++;
                        if (j.Bool("equipped")) toEquip.Add(item);
                    }
                }
                if (!placed)
                {
                    AddOrDrop(player, item, report);
                    if (replace && j.Bool("equipped") && inventory.ContainsItem(item)) toEquip.Add(item);
                }
            }

            foreach (ItemDrop.ItemData item in toEquip)
                player.EquipItem(item, false);

            string skillMode = request.Str("skillMode", "none");
            if (skillMode != "none")
                RestoreSkills(player, request.List("skills"), skillMode == "set", report);

            player.Message(MessageHud.MessageType.Center, Texts.Restored(report.Added + report.Dropped, report.Dropped, report.Skills));
            BepInExPlugin.Log("Restore: added " + report.Added + ", dropped " + report.Dropped + ", skills " + report.Skills);
            return report;
        }

        public static ItemDrop.ItemData Make(Dictionary<string, object> j)
        {
            if (j == null) return null;
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
                try
                {
                    Skills.Skill skill = playerSkills.GetSkill((Skills.SkillType)j.Int("type"));
                    if (skill == null) continue;
                    float level = (float)j.Double("level");
                    if (!exact && level <= skill.m_level) continue;
                    skill.m_level = level;
                    skill.m_accumulator = (float)j.Double("acc");
                    report.Skills++;
                }
                catch (Exception e)
                {
                    report.Failed.Add(new Dictionary<string, object> { { "skill", j.Str("name") }, { "reason", e.Message } });
                }
            }
        }
    }
}
