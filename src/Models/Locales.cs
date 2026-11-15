using System;
using System.Collections.Generic;

namespace SourceGit.Models
{
    public class Locale
    {
        public string Name { get; set; }
        public string Key { get; set; }

        public static readonly List<Locale> Supported = new List<Locale>() {
            new Locale("English", "en_US"),
            new Locale("简体中文", "zh_CN"),
        };

        public static string ResolveKey(string key) =>
            Supported.Find(locale => string.Equals(locale.Key, key, StringComparison.OrdinalIgnoreCase))?.Key ?? "zh_CN";

        public Locale(string name, string key)
        {
            Name = name;
            Key = key;
        }
    }
}
