using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace SteamFiles.Processor {
    using RuleDictionary = Dictionary<string, List<Regex>>;

    public static class Ruleset {
        public static RuleDictionary Parse(string path) {
            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream);
            string? line;

            var ruleset = new Dictionary<string, List<Regex>>();

            var category = "";
            while ((line = reader.ReadLine()) != null) {
                if (line.Contains(';')) {
                    line = line[..line.IndexOf(';')];
                }

                line = line.Trim();

                if (line.Length == 0) {
                    continue;
                }

                if (line[0] == '[') {
                    category = line[1..^1];
                    continue;
                }

                var key = line[..line.IndexOf('=')].Trim();
                var value = line[(line.IndexOf('=') + 1)..].Trim();

                if (key.EndsWith("[]")) {
                    key = key[..^2];
                }

                key = $"{category}.{key}";

                if (!ruleset.TryGetValue(key, out var rules)) {
                    rules = new List<Regex>();
                    ruleset[key] = rules;
                }

                try {
                    rules.Add(new Regex(value, RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                } catch {
                    rules.Add(new Regex(value.Replace("\\_", "_"), RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace));
                }
            }

            return ruleset;
        }

        public static HashSet<string> Run(IEnumerable<string> filelist, RuleDictionary ruleset) {
            var detected = new HashSet<string>();
            var list = filelist.ToArray();
            foreach (var (name, tests) in ruleset) {
                if (list.Any(path => tests.Any(y => y.IsMatch(path)))) {
                    detected.Add(name);
                }
            }

            return detected;
        }
    }
}
