using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimAdmin
{
    /// <summary>
    /// Renders item icons to PNG on a player's game, which has a GPU (the dedicated server runs
    /// with -nographics). Any item in ObjectDB works, including items added by mods. Icons live in
    /// sprite atlases that are not CPU-readable, so the sprite is blitted into a RenderTexture and
    /// read back.
    /// </summary>
    public static class Icons
    {
        public const int Size = 64;

        /// <summary>Request items: [{prefab, variant}]. Result: {"prefab|variant": base64 png}.</summary>
        public static Dictionary<string, object> Render(List<object> items)
        {
            var result = new Dictionary<string, object>();
            foreach (object o in items ?? new List<object>())
            {
                var j = o as Dictionary<string, object>;
                string prefab = j.Str("prefab");
                int variant = j.Int("variant");
                if (string.IsNullOrEmpty(prefab)) continue;
                string key = prefab + "|" + variant;
                if (result.ContainsKey(key)) continue;
                try
                {
                    string png = Render(prefab, variant);
                    if (png != null) result[key] = png;
                }
                catch (Exception e)
                {
                    BepInExPlugin.Dbgl("Icon of " + key + " failed: " + e.Message);
                }
            }
            return result;
        }

        private static string Render(string prefabName, int variant)
        {
            GameObject prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            ItemDrop drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            Sprite[] icons = drop?.m_itemData?.m_shared?.m_icons;
            if (icons == null || icons.Length == 0) return null;
            Sprite sprite = icons[Mathf.Clamp(variant, 0, icons.Length - 1)];
            if (sprite == null || sprite.texture == null) return null;

            Texture texture = sprite.texture;
            Rect rect = sprite.textureRect;
            var scale = new Vector2(rect.width / texture.width, rect.height / texture.height);
            var offset = new Vector2(rect.x / texture.width, rect.y / texture.height);

            RenderTexture previous = RenderTexture.active;
            RenderTexture target = RenderTexture.GetTemporary(Size, Size, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Texture2D readable = null;
            try
            {
                RenderTexture.active = target;
                GL.Clear(true, true, Color.clear);
                Graphics.Blit(texture, target, scale, offset);
                RenderTexture.active = target;
                readable = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
                readable.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                readable.Apply();
                return Convert.ToBase64String(readable.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                if (readable != null) UnityEngine.Object.Destroy(readable);
            }
        }
    }
}
