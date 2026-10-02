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
// VCardContact -> vCard 3.0 text, for uploading a phone-side change back to Google.
//
// A CardDAV PUT replaces the whole card, so writing a fresh card from what the phone holds
// would silently delete everything the phone cannot represent - the contact photo, categories,
// IMPP handles, every X- property Google keeps. So the normal path is Patch(): the card Google
// last sent is kept on disk (see CardCache), and an upload rewrites only the properties the
// People hub can actually express, leaving every other line exactly as it came.
//
// The other half of the same problem is capacity: a StoredContact has ten phone slots, three
// email slots, three address slots and one website slot, and a Google contact can hold more.
// Guessing from counts would be fragile, so nothing is guessed - PhoneContacts reports exactly
// which values it could not place when it writes a contact, those lines are stored, and Patch
// puts them back. Anything else missing from the phone really was deleted there.
using System;
using System.Collections.Generic;
using System.Text;
using GContactsSync.Core.Models;

namespace GContactsSync.Core.Services
{
    public static class VCardWriter
    {
        // Properties this app owns: on a patch they are removed and re-emitted from the phone's
        // state. Everything else in the stored card is passed through untouched.
        static readonly string[] Managed =
        {
            "FN", "N", "NICKNAME", "ORG", "TITLE", "NOTE", "BDAY", "TEL", "EMAIL", "ADR", "URL"
        };

        // Never copied from the stored card: VERSION is re-emitted, and REV and PRODID are the
        // server's to set, where a stale value is worse than none.
        static readonly string[] Dropped = { "BEGIN", "END", "VERSION", "REV", "PRODID" };

        // ================================================================
        // A fresh card, for a contact that exists only on the phone
        // ================================================================
        public static string BuildNew(VCardContact c, string uid)
        {
            var sb = new StringBuilder();
            sb.Append("BEGIN:VCARD\r\n");
            sb.Append("VERSION:3.0\r\n");
            VCardText.AppendFolded(sb, "UID:" + uid);
            foreach (var line in ManagedLines(c))
                VCardText.AppendFolded(sb, line);
            sb.Append("END:VCARD\r\n");
            return sb.ToString();
        }

        // ================================================================
        // The stored card, with this app's properties brought up to date
        // ================================================================
        /// <param name="storedCard">The card Google last sent for this contact.</param>
        /// <param name="phone">The contact as the phone now holds it.</param>
        /// <param name="unplacedLines">
        /// Lines for values that never reached the phone because its contact store had no slot
        /// left for them. Re-emitted verbatim, because their absence from the phone says nothing
        /// about the user's intent.
        /// </param>
        public static string Patch(string storedCard, VCardContact phone, List<string> unplacedLines)
        {
            var original = VCardText.Unfold(storedCard);
            var preserved = unplacedLines ?? new List<string>();

            var passthrough = new List<string>();
            bool orgPassedThrough = false;

            foreach (var line in original)
            {
                string prop = VCardText.PropertyName(line);
                if (prop.Length == 0) continue;
                if (IsIn(Dropped, prop)) continue;

                if (prop.StartsWith("X-AB", StringComparison.Ordinal))
                {
                    // A grouped X-ABLabel is the label of one value ("item1.EMAIL"); its value
                    // is about to be re-emitted without the group, so the label goes with it.
                    // An ungrouped X-AB property (X-ABShowAs, say) is contact data of its own.
                    if (VCardText.GroupName(line).Length == 0) passthrough.Add(line);
                    continue;
                }

                if (IsIn(Managed, prop))
                {
                    // ORG can carry a department ("Acme;Sales") that the phone cannot hold. When
                    // the company name still matches, keep the original line and do not re-emit.
                    if (prop == "ORG" && OrgMatches(line, phone))
                    {
                        passthrough.Add(line);
                        orgPassedThrough = true;
                    }
                    continue;
                }

                passthrough.Add(line);
            }

            var sb = new StringBuilder();
            sb.Append("BEGIN:VCARD\r\n");
            sb.Append("VERSION:3.0\r\n");

            foreach (var line in ManagedLines(phone))
            {
                if (orgPassedThrough && VCardText.PropertyName(line) == "ORG") continue;
                VCardText.AppendFolded(sb, line);
            }
            foreach (var line in preserved)
                VCardText.AppendFolded(sb, line);
            foreach (var line in passthrough)
                VCardText.AppendFolded(sb, line);

            sb.Append("END:VCARD\r\n");
            return sb.ToString();
        }

        // ================================================================
        // The properties this app owns, as vCard lines
        // ================================================================
        static List<string> ManagedLines(VCardContact c)
        {
            var lines = new List<string>();

            string displayName = c.DisplayName;
            if (string.IsNullOrEmpty(displayName))
                displayName = (c.FirstName + " " + c.LastName).Trim();
            if (string.IsNullOrEmpty(displayName)) displayName = c.Organization;
            if (!string.IsNullOrEmpty(displayName))
                lines.Add("FN:" + VCardText.Escape(displayName));

            // N is mandatory in vCard 3.0, so it goes out even when every component is empty.
            lines.Add("N:" + VCardText.Escape(c.LastName) + ";" + VCardText.Escape(c.FirstName) + ";"
                           + VCardText.Escape(c.MiddleName) + ";" + VCardText.Escape(c.NamePrefix) + ";"
                           + VCardText.Escape(c.NameSuffix));

            if (!string.IsNullOrEmpty(c.Nickname))
                lines.Add("NICKNAME:" + VCardText.Escape(c.Nickname));

            foreach (var p in c.Phones)
            {
                string line = PhoneLine(p);
                if (line != null) lines.Add(line);
            }
            foreach (var e in c.Emails)
            {
                string line = EmailLine(e);
                if (line != null) lines.Add(line);
            }
            foreach (var a in c.Addresses)
            {
                string line = AddressLine(a);
                if (line != null) lines.Add(line);
            }
            foreach (var w in c.Websites)
            {
                string line = WebsiteLine(w);
                if (line != null) lines.Add(line);
            }

            if (!string.IsNullOrEmpty(c.Organization))
                lines.Add("ORG:" + VCardText.Escape(c.Organization));
            if (!string.IsNullOrEmpty(c.JobTitle))
                lines.Add("TITLE:" + VCardText.Escape(c.JobTitle));
            if (!string.IsNullOrEmpty(c.Notes))
                lines.Add("NOTE:" + VCardText.Escape(c.Notes));

            string birthday = NormaliseBirthday(c.Birthday);
            if (birthday != null) lines.Add("BDAY:" + birthday);

            return lines;
        }

        /// <summary>One TEL line, or null when there is no number. Public so the phone-side
        /// mapper can serialise the values it had no room for.</summary>
        public static string PhoneLine(ContactPhone p)
        {
            if (p == null || string.IsNullOrEmpty(p.Number)) return null;
            return "TEL;TYPE=" + PhoneTypeParam(p.Type) + ":" + VCardText.Escape(p.Number);
        }

        public static string EmailLine(ContactEmail e)
        {
            if (e == null || string.IsNullOrEmpty(e.Address)) return null;
            return "EMAIL;TYPE=" + EmailTypeParam(e.Type) + ":" + VCardText.Escape(e.Address);
        }

        public static string AddressLine(ContactAddress a)
        {
            if (a == null || a.IsEmpty) return null;
            return "ADR;TYPE=" + LabelParam(a.Type) + ":;;"
                 + VCardText.Escape(a.Street) + ";"
                 + VCardText.Escape(a.City) + ";"
                 + VCardText.Escape(a.Region) + ";"
                 + VCardText.Escape(a.PostalCode) + ";"
                 + VCardText.Escape(a.Country);
        }

        public static string WebsiteLine(ContactWebsite w)
        {
            if (w == null || string.IsNullOrEmpty(w.Url)) return null;
            string label = LabelParam(w.Type);
            return label == "OTHER"
                ? "URL:" + VCardText.Escape(w.Url)
                : "URL;TYPE=" + label + ":" + VCardText.Escape(w.Url);
        }

        // ================================================================
        // Small helpers
        // ================================================================
        /// <summary>Digits and a leading plus only, so "+1 (800) 555-0100" and "+18005550100" match.</summary>
        public static string NormalisePhone(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (char ch in s)
                if (char.IsDigit(ch) || (ch == '+' && sb.Length == 0)) sb.Append(ch);
            return sb.ToString();
        }

        static bool IsIn(string[] set, string value)
        {
            foreach (var s in set)
                if (string.Equals(s, value, StringComparison.Ordinal)) return true;
            return false;
        }

        static bool OrgMatches(string orgLine, VCardContact phone)
        {
            if (string.IsNullOrEmpty(phone.Organization)) return false;
            var parts = VCardText.SplitComponents(VCardText.RawValue(orgLine));
            if (parts.Length < 2) return false;   // no department to protect
            string company = VCardText.Unescape(parts[0]).Trim();
            return string.Equals(company, phone.Organization.Trim(), StringComparison.Ordinal);
        }

        static string PhoneTypeParam(string type)
        {
            switch (type)
            {
                case "home":    return "HOME,VOICE";
                case "work":    return "WORK,VOICE";
                case "mobile":  return "CELL,VOICE";
                case "pager":   return "PAGER";
                case "homefax": return "HOME,FAX";
                case "workfax": return "WORK,FAX";
                default:        return "VOICE";
            }
        }

        static string EmailTypeParam(string type)
        {
            switch (type)
            {
                case "home": return "INTERNET,HOME";
                case "work": return "INTERNET,WORK";
                default:     return "INTERNET";
            }
        }

        static string LabelParam(string type)
        {
            switch (type)
            {
                case "home": return "HOME";
                case "work": return "WORK";
                default:     return "OTHER";
            }
        }

        /// <summary>
        /// A BDAY value Google accepts (yyyy-MM-dd), or null when there is nothing usable.
        /// A contact with no birth year is stored as --MMdd and passes through unchanged.
        /// </summary>
        static string NormaliseBirthday(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return null;
            string s = raw.Trim();
            if (s.StartsWith("--", StringComparison.Ordinal)) return s;

            string digits = s.Replace("-", "");
            if (digits.Length < 8) return null;

            int year, month, day;
            if (!int.TryParse(digits.Substring(0, 4), out year)) return null;
            if (!int.TryParse(digits.Substring(4, 2), out month)) return null;
            if (!int.TryParse(digits.Substring(6, 2), out day)) return null;
            if (month < 1 || month > 12 || day < 1 || day > 31) return null;

            return string.Format("{0:D4}-{1:D2}-{2:D2}", year, month, day);
        }
    }
}
