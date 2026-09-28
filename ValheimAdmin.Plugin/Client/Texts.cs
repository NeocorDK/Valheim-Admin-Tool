namespace ValheimAdmin
{
    /// <summary>Messages shown to the player, in Russian when the game runs in Russian and English otherwise.</summary>
    public static class Texts
    {
        private static bool Russian
        {
            get
            {
                try
                {
                    return Localization.instance != null && Localization.instance.GetSelectedLanguage() == "Russian";
                }
                catch
                {
                    return false;
                }
            }
        }

        public static string ServerTag => Russian ? "[Сервер]" : "[Server]";

        public static string Given(string item, int count) =>
            (Russian ? "Администратор выдал: " : "The admin gave you: ") + item + " x" + count;

        public static string Restored(int items, int dropped, int skills)
        {
            string text = (Russian ? "Администратор восстановил персонажа: предметов " : "The admin restored your character: items ") + items;
            if (dropped > 0) text += Russian ? " (" + dropped + " на земле)" : " (" + dropped + " on the ground)";
            if (skills > 0) text += (Russian ? ", навыков " : ", skills ") + skills;
            return text;
        }
    }
}
