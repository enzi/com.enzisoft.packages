// <copyright project="Enzisoft.Packages.Editor" file="Markdown.cs">
// Copyright © 2026 Thomas Enzenebner. All rights reserved.
// </copyright>

using System.Text;

namespace Enzisoft.Packages.Editor
{
    /// <summary>Just enough Markdown for READMEs and changelogs in a UI Toolkit label: headings and bullets.</summary>
    internal static class Markdown
    {
        public static string ToRichText(string markdown)
        {
            var text = new StringBuilder();
            var inCode = false;
            foreach (var rawLine in markdown.Replace("\r\n", "\n").Split('\n'))
            {
                var line = rawLine.TrimEnd();
                if (line.TrimStart().StartsWith("```"))
                {
                    inCode = !inCode;
                    continue;
                }

                if (!inCode && line.StartsWith("#"))
                {
                    var level = 0;
                    while (level < line.Length && line[level] == '#')
                        level++;

                    var size = level == 1 ? 16 : level == 2 ? 14 : 13;
                    text.Append($"<size={size}><b>").Append(Escape(line.Substring(level).Trim())).Append("</b></size>\n");
                    continue;
                }

                if (!inCode)
                {
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                        line = new string(' ', line.Length - trimmed.Length + 2) + "• " + trimmed.Substring(2);
                }

                text.Append(Escape(line)).Append('\n');
            }

            return text.ToString().TrimEnd('\n');
        }

        /// <summary>Package text may contain angle brackets (generics, HTML); they must show literally, not as tags.</summary>
        private static string Escape(string line) =>
            line.Length == 0 ? line : "<noparse>" + line.Replace("</noparse>", "</ noparse>") + "</noparse>";
    }
}
