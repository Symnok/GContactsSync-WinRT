// GContactsSync - a Google Contacts CardDAV sync client for Windows Phone 8.1
// (and Windows 10 Mobile, which runs the same package).
// Copyright (C) 2026
//
// This program is free software; you can redistribute it and/or modify it under
// the terms of the GNU General Public License as published by the Free Software
// Foundation; either version 2 of the License, or (at your option) any later
// version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS
// FOR A PARTICULAR PURPOSE. See the GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with
// this program; see the LICENSE file. If not, see <https://www.gnu.org/licenses/>.
//
// The line-level details of vCard text, in one place because three things depend on getting
// them identical: the parser, the serializer, and the patcher that rewrites a stored card in
// place. If unfolding differed between them, a round trip would corrupt cards.
using System;
using System.Collections.Generic;
using System.Text;

namespace GContactsSync.Core.Services
{
    internal static class VCardText
    {
        /// <summary>
        /// Splits a card into logical lines, undoing RFC 2425 folding (a CRLF followed by a
        /// space or tab continues the previous line). Google folds long values - photos in
        /// particular - so nothing works without this.
        /// </summary>
        public static List<string> Unfold(string card)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(card)) return lines;

            var current = new StringBuilder();
            bool have = false;

            foreach (var raw in card.Split('\n'))
            {
                var line = raw.EndsWith("\r", StringComparison.Ordinal)
                    ? raw.Substring(0, raw.Length - 1)
                    : raw;

                if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
                {
                    // Continuation: the folding whitespace is not part of the value.
                    if (have) current.Append(line.Substring(1));
                    continue;
                }

                if (have) lines.Add(current.ToString());
                current.Length = 0;
                current.Append(line);
                have = true;
            }
            if (have) lines.Add(current.ToString());

            // Blank lines carry no property and only complicate everything downstream.
            for (int i = lines.Count - 1; i >= 0; i--)
                if (lines[i].Trim().Length == 0) lines.RemoveAt(i);

            return lines;
        }

        /// <summary>
        /// The property name of a logical line, upper-cased, with Google's "itemN." grouping
        /// prefix and any parameters removed. "item2.TEL;TYPE=WORK:+1" gives "TEL".
        /// Returns an empty string for a line that carries no property.
        /// </summary>
        public static string PropertyName(string line)
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) return string.Empty;

            string head = line.Substring(0, colon);
            int semi = head.IndexOf(';');
            if (semi >= 0) head = head.Substring(0, semi);

            int dot = head.IndexOf('.');
            if (dot >= 0) head = head.Substring(dot + 1);

            return head.Trim().ToUpperInvariant();
        }

        /// <summary>Google's "itemN" group of a line, or an empty string when it has none.</summary>
        public static string GroupName(string line)
        {
            int colon = line.IndexOf(':');
            string head = colon > 0 ? line.Substring(0, colon) : line;
            int dot = head.IndexOf('.');
            if (dot <= 0) return string.Empty;
            string group = head.Substring(0, dot).Trim();
            return group.StartsWith("item", StringComparison.OrdinalIgnoreCase) ? group : string.Empty;
        }

        /// <summary>Everything after the first colon, untouched.</summary>
        public static string RawValue(string line)
        {
            int colon = line.IndexOf(':');
            return colon < 0 ? string.Empty : line.Substring(colon + 1);
        }

        /// <summary>The whole parameter section of a line, upper-cased ("TEL;TYPE=WORK" -&gt; "TEL;TYPE=WORK").</summary>
        public static string Head(string line)
        {
            int colon = line.IndexOf(':');
            return (colon > 0 ? line.Substring(0, colon) : line).ToUpperInvariant();
        }

        /// <summary>Undoes vCard value escaping (\n \, \; \\).</summary>
        public static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char next = s[++i];
                switch (next)
                {
                    case 'n':
                    case 'N': sb.Append('\n'); break;
                    case '\\': sb.Append('\\'); break;
                    case ',': sb.Append(','); break;
                    case ';': sb.Append(';'); break;
                    default: sb.Append(next); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Applies vCard value escaping. Carriage returns are dropped, not escaped.</summary>
        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("\\", "\\\\")
                    .Replace(";", "\\;")
                    .Replace(",", "\\,")
                    .Replace("\r", "")
                    .Replace("\n", "\\n");
        }

        /// <summary>
        /// Splits a structured value (N, ADR, ORG) on unescaped semicolons. A plain
        /// string.Split would break "Acme\; Inc" into two components.
        /// </summary>
        public static string[] SplitComponents(string value)
        {
            var parts = new List<string>();
            var sb = new StringBuilder();
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == '\\' && i + 1 < value.Length)
                {
                    sb.Append(value[i]).Append(value[i + 1]);
                    i++;
                    continue;
                }
                if (value[i] == ';') { parts.Add(sb.ToString()); sb.Length = 0; continue; }
                sb.Append(value[i]);
            }
            parts.Add(sb.ToString());
            return parts.ToArray();
        }

        /// <summary>
        /// Folds long lines at 75 octets, as RFC 2426 asks. Google accepts unfolded lines, but
        /// a base64 photo on a single multi-kilobyte line is rejected by some servers and makes
        /// a stored card unreadable, so we fold on the way out.
        /// </summary>
        public static void AppendFolded(StringBuilder sb, string line)
        {
            const int limit = 75;
            if (line.Length <= limit) { sb.Append(line).Append("\r\n"); return; }

            sb.Append(line.Substring(0, limit)).Append("\r\n");
            int pos = limit;
            while (pos < line.Length)
            {
                int take = Math.Min(limit - 1, line.Length - pos);
                sb.Append(' ').Append(line.Substring(pos, take)).Append("\r\n");
                pos += take;
            }
        }
    }
}
