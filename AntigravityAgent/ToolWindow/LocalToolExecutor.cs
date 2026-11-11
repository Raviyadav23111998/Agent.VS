using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Agent.ToolWindow
{
    /// <summary>
    /// Executes agent tools locally on the user's machine (Visual Studio
    /// "Local Server" mode). When the VPS agent sends a tool_request over the
    /// WebSocket, this runs it with System.IO / Process against the open
    /// solution's folder. Mirrors the VS Code LocalToolExecutor 1:1 (same tool
    /// names + arg keys), so the server-side RemoteToolExecutor contract is
    /// identical across both editors.
    /// </summary>
    internal sealed class LocalToolExecutor
    {
        private readonly string _workspaceRoot;
        private const int MaxOutputLines = 100;
        private static readonly HashSet<string> Skip = new HashSet<string>(
            new[] { ".git", ".DS_Store", "__pycache__", ".ag_data", "node_modules", "bin", "obj" });

        public LocalToolExecutor(string workspaceRoot)
        {
            _workspaceRoot = string.IsNullOrWhiteSpace(workspaceRoot)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : workspaceRoot;
        }

        public string Execute(string toolName, JObject args)
        {
            args = args ?? new JObject();
            try
            {
                switch (toolName)
                {
                    case "view_file": return ViewFile(args);
                    case "write_to_file": return WriteToFile(args);
                    case "list_dir": return ListDir(args);
                    case "grep_search": return GrepSearch(args);
                    case "replace_file_content": return ReplaceFileContent(args);
                    case "multi_replace_file_content": return MultiReplaceFileContent(args);
                    case "run_command": return RunCommand(args);
                    default: return $"Error: Unknown local tool '{toolName}'";
                }
            }
            catch (Exception e)
            {
                return $"Error executing {toolName}: {e.Message}";
            }
        }

        // ── view_file ───────────────────────────────────────────────────
        private string ViewFile(JObject a)
        {
            var path = Resolve(Str(a, "absolute_path"));
            if (!File.Exists(path))
            {
                if (Directory.Exists(path)) return $"Error: {path} is a directory. Use list_dir instead.";
                return $"Error: File not found: {path}";
            }
            var allLines = File.ReadAllLines(path);
            int total = allLines.Length;
            int start = a["start_line"] != null ? Math.Max(1, (int)a["start_line"]) : 1;
            int end = a["end_line"] != null ? Math.Min(total, (int)a["end_line"]) : total;
            if (end - start + 1 > 800) end = start + 799;
            if (end > total) end = total;

            var sb = new StringBuilder();
            sb.AppendLine($"File: {path} | Total: {total} lines | Showing: {start}-{end}");
            for (int i = start; i <= end && i <= total; i++)
                sb.AppendLine($"{i}: {allLines[i - 1]}");
            return sb.ToString().TrimEnd('\n');
        }

        // ── write_to_file ─────────────────────────────────────────────────
        private string WriteToFile(JObject a)
        {
            var path = Resolve(Str(a, "target_file"));
            bool overwrite = Bool(a, "overwrite");
            if (File.Exists(path) && !overwrite)
                return $"Error: {path} already exists. Set overwrite=true to replace.";
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var content = Str(a, "code_content");
            File.WriteAllText(path, content);
            int lines = (content ?? "").Split('\n').Length;
            return $"Successfully wrote {lines} lines to {path}";
        }

        // ── list_dir ──────────────────────────────────────────────────────
        private string ListDir(JObject a)
        {
            var dir = Resolve(Str(a, "directory_path"));
            if (!Directory.Exists(dir))
            {
                if (File.Exists(dir)) return $"Error: {dir} is a file, not a directory.";
                return $"Error: Directory not found: {dir}";
            }
            var dirs = new List<string>();
            var files = new List<string>();
            foreach (var entry in Directory.GetFileSystemEntries(dir).OrderBy(x => x))
            {
                var name = Path.GetFileName(entry);
                if (Skip.Contains(name)) continue;
                try
                {
                    if (Directory.Exists(entry))
                    {
                        int children = -1;
                        try { children = Directory.GetFileSystemEntries(entry).Length; } catch { }
                        dirs.Add($"  {name}/ ({children} children)");
                    }
                    else
                    {
                        long size = new FileInfo(entry).Length;
                        string s = size < 1024 ? $"{size}B"
                            : size < 1048576 ? $"{size / 1024.0:0.0}KB"
                            : $"{size / 1048576.0:0.0}MB";
                        files.Add($"  {name} ({s})");
                    }
                }
                catch { files.Add($"  {name} (unreadable)"); }
            }
            var outp = new List<string> { $"Directory: {dir}" };
            if (dirs.Count > 0) { outp.Add($"Directories ({dirs.Count}):"); outp.AddRange(dirs); }
            if (files.Count > 0) { outp.Add($"Files ({files.Count}):"); outp.AddRange(files); }
            if (dirs.Count == 0 && files.Count == 0) outp.Add("(empty)");
            return string.Join("\n", outp);
        }

        // ── grep_search ─────────────────────────────────────────────────
        private string GrepSearch(JObject a)
        {
            var root = Resolve(Str(a, "search_path"));
            if (!File.Exists(root) && !Directory.Exists(root))
                return $"Error: Path not found: {root}";

            var query = Str(a, "query") ?? "";
            bool ci = Bool(a, "case_insensitive");
            var includes = (a["includes"] is JArray arr)
                ? arr.Select(x => x.ToString()).ToList()
                : (!string.IsNullOrEmpty(Str(a, "includes"))
                    ? Str(a, "includes").Split(',').Select(s => s.Trim()).ToList()
                    : new List<string>());

            var cmp = ci ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var matches = new List<string>();
            var paths = File.Exists(root) ? new[] { root } : EnumerateFiles(root, includes);
            foreach (var f in paths)
            {
                if (matches.Count >= 50) break;
                string[] lines;
                try { lines = File.ReadAllLines(f); } catch { continue; }
                for (int i = 0; i < lines.Length && matches.Count < 50; i++)
                    if (lines[i].IndexOf(query, cmp) >= 0)
                        matches.Add($"{f}:{i + 1}:{lines[i]}");
            }
            if (matches.Count == 0) return "No matches found.";
            string header = $"Found {matches.Count} matches{(matches.Count >= 50 ? " (capped at 50)" : "")}";
            return header + ":\n" + string.Join("\n", matches);
        }

        private IEnumerable<string> EnumerateFiles(string root, List<string> includes)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                string[] subdirs = Array.Empty<string>();
                try { subdirs = Directory.GetDirectories(dir); } catch { }
                foreach (var d in subdirs)
                    if (!Skip.Contains(Path.GetFileName(d))) stack.Push(d);

                string[] files = Array.Empty<string>();
                try { files = Directory.GetFiles(dir); } catch { }
                foreach (var f in files)
                {
                    if (includes.Count > 0 &&
                        !includes.Any(g => MatchGlob(Path.GetFileName(f), g))) continue;
                    yield return f;
                }
            }
        }

        private static bool MatchGlob(string name, string glob)
        {
            // Minimal glob: "*.ext" / "*" — enough for the agent's include hints.
            if (string.IsNullOrEmpty(glob) || glob == "*") return true;
            if (glob.StartsWith("*.")) return name.EndsWith(glob.Substring(1), StringComparison.OrdinalIgnoreCase);
            return string.Equals(name, glob, StringComparison.OrdinalIgnoreCase);
        }

        // ── replace_file_content ──────────────────────────────────────────
        private string ReplaceFileContent(JObject a)
        {
            var path = Resolve(Str(a, "target_file"));
            if (!File.Exists(path)) return $"Error: File not found: {path}";
            var content = File.ReadAllText(path);
            var target = Str(a, "target_content") ?? "";
            var replacement = Str(a, "replacement_content") ?? "";
            bool allowMultiple = Bool(a, "allow_multiple");
            if (target.Length == 0) return "Error: target_content is empty.";

            int count = CountOccurrences(content, target);
            if (count == 0) return "Error: target_content not found in file.";
            if (count > 1 && !allowMultiple)
                return $"Error: target_content found {count} times. Set allow_multiple=true or narrow the target.";

            content = allowMultiple ? content.Replace(target, replacement) : ReplaceFirst(content, target, replacement);
            File.WriteAllText(path, content);
            return $"Successfully replaced content in {path}";
        }

        // ── multi_replace_file_content ───────────────────────────────────
        private string MultiReplaceFileContent(JObject a)
        {
            var path = Resolve(Str(a, "target_file"));
            if (!File.Exists(path)) return $"Error: File not found: {path}";

            JArray chunks;
            var raw = a["replacement_chunks"] ?? a["replacements"];
            try { chunks = raw is JArray ja ? ja : JArray.Parse(raw?.ToString() ?? "[]"); }
            catch (Exception e) { return $"Error: Invalid JSON in replacement_chunks: {e.Message}"; }
            if (chunks == null || chunks.Count == 0) return "Error: No replacement chunks provided.";

            var content = File.ReadAllText(path);
            for (int i = 0; i < chunks.Count; i++)
            {
                var t = chunks[i]["target_content"]?.ToString() ?? chunks[i]["target"]?.ToString() ?? "";
                if (!content.Contains(t)) return $"Error: Chunk {i} target_content not found in file.";
            }
            // Apply in reverse position order to preserve indices.
            var ordered = chunks
                .Select(c => new { pos = content.IndexOf(c["target_content"]?.ToString() ?? c["target"]?.ToString() ?? "", StringComparison.Ordinal), chunk = c })
                .OrderByDescending(x => x.pos);
            foreach (var x in ordered)
            {
                var t = x.chunk["target_content"]?.ToString() ?? x.chunk["target"]?.ToString() ?? "";
                var r = x.chunk["replacement_content"]?.ToString() ?? x.chunk["replacement"]?.ToString() ?? "";
                content = ReplaceFirst(content, t, r);
            }
            File.WriteAllText(path, content);
            return $"Successfully applied {chunks.Count} replacements to {path}";
        }

        // ── run_command ─────────────────────────────────────────────────
        private string RunCommand(JObject a)
        {
            var cwd = Resolve(Str(a, "cwd") ?? _workspaceRoot);
            var cmd = Str(a, "command_line") ?? "";
            if (System.Text.RegularExpressions.Regex.IsMatch(
                    cmd, @"(?:^|[\s|;&])(nano|vim|vi|emacs|less|more)(?:\s|$)"))
                return "Error: Interactive command not supported.";

            int timeoutMs = a["wait_ms_before_async"] != null ? (int)a["wait_ms_before_async"] : 30000;
            timeoutMs = Math.Min(timeoutMs, 120000);

            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c " + cmd)
                {
                    WorkingDirectory = Directory.Exists(cwd) ? cwd : _workspaceRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd();
                    string stderr = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        try { p.Kill(); } catch { }
                        return "Error: Command timed out.";
                    }
                    var combined = (stdout + "\n" + stderr).Trim();
                    return Truncate(combined.Length == 0 ? "Command completed with no output." : combined);
                }
            }
            catch (Exception e) { return $"Error: {e.Message}"; }
        }

        // ── helpers ──────────────────────────────────────────────────────
        private string Truncate(string output)
        {
            var lines = output.Split('\n');
            if (lines.Length <= MaxOutputLines) return output;
            int omitted = lines.Length - MaxOutputLines;
            return string.Join("\n", lines.Take(MaxOutputLines))
                + $"\n\n... ({omitted} lines omitted, showing first {MaxOutputLines})";
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return 0;
            int count = 0, idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
            return count;
        }

        private static string ReplaceFirst(string text, string search, string replace)
        {
            int pos = text.IndexOf(search, StringComparison.Ordinal);
            return pos < 0 ? text : text.Substring(0, pos) + replace + text.Substring(pos + search.Length);
        }

        private string Resolve(string p)
        {
            if (string.IsNullOrWhiteSpace(p)) return _workspaceRoot;
            return Path.IsPathRooted(p) ? p : Path.GetFullPath(Path.Combine(_workspaceRoot, p));
        }

        private static string Str(JObject a, string key) => a[key]?.ToString();
        private static bool Bool(JObject a, string key) => a[key] != null && a[key].Type == JTokenType.Boolean && (bool)a[key];
    }
}
