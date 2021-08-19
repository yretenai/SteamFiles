using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SteamFiles.Processor;

namespace SteamFiles.Test
{
    using RuleDictionary = Dictionary<string, List<Regex>>;
    
    public class FilelistTest {
        private RuleDictionary Rules { get; set; } = new();
        private string? Root { get; set; }
        
        [SetUp]
        public void Setup() {
            Root = Environment.GetEnvironmentVariable("FILE_DETECTION_RULE_SETS_PATH");
            if (!Directory.Exists(Root)) {
                return;
            }

            var rules = Path.Combine(Root!, "rules.ini");
            if (!File.Exists(rules)) {
                return;
            }

            Rules = Ruleset.Parse(rules);
        }

        [Test]
        public void Filelists() {
            var filelists = Path.Combine(Root!, "tests", "filelists");
            foreach (var path in Directory.EnumerateFiles(filelists, "*.txt", SearchOption.TopDirectoryOnly)) {
                var expected = string.Join('.', Path.GetFileName(path).Split('.').Take(2));
                if (!Rules.ContainsKey(expected)) {
                    continue;
                }
                var list = File.ReadAllLines(path);

                var result = Ruleset.Run(list, Rules);

                if (!result.Contains(expected) && expected != "Engine.Godot") {
                    Assert.Fail($"Failed to find {expected} in {path}");
                }
            }
        }
        
        [Test]
        public void Types() {
            var filelists = Path.Combine(Root!, "tests", "types");
            foreach (var path in Directory.EnumerateFiles(filelists, "*.txt", SearchOption.TopDirectoryOnly)) {
                var expected = string.Join('.', Path.GetFileName(path).Split('.').Take(2));
                if (!Rules.ContainsKey(expected)) {
                    continue;
                }
                var list = File.ReadAllLines(path);

                foreach (var line in list) {
                    var result = Ruleset.Run(new [] { line }, Rules);

                    if (!result.Contains(expected)) {
                        Assert.Fail($"Failed to find {expected} in {line}");
                    }
                }
            }
        }
        
        [Test]
        public void NonMatching() {
            var list = File.ReadAllLines(Path.Combine(Root!, "tests", "types", "_NonMatchingTests.txt"));

            foreach (var line in list) {
                if (line.Trim().Length == 0) {
                    continue;
                }
                var result = Ruleset.Run(new [] { line.Trim() }, Rules);

                if (result.Any(x => !x.StartsWith("Evidence."))) { 
                    Assert.Fail($"Matched {string.Join(", ", result)} in {line}");
                }
            }
        }
    }
}
