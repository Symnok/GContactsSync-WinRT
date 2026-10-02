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
// vCard 3.0 -> VCardContact. Only what Google actually sends is handled; the patcher keeps
// everything else in the stored card, so a property this parser ignores still survives a
// round trip.
using System;
using System.Collections.Generic;
using GContactsSync.Core.Models;

namespace GContactsSync.Core.Services
{
    public static class VCardParser
    {
        /// <summary>Every BEGIN:VCARD..END:VCARD block in a body, in order.</summary>
        public static List<string> SplitCards(string rawData)
        {
            var cards = new List<string>();
            if (string.IsNullOrEmpty(rawData)) return cards;

            int from = 0;
            while (true)
            {
                int begin = rawData.IndexOf("BEGIN:VCARD", from, StringComparison.OrdinalIgnoreCase);
                if (begin < 0) break;

                int end = rawData.IndexOf("END:VCARD", begin, StringComparison.OrdinalIgnoreCase);
                if (end < 0) break;

                end += "END:VCARD".Length;
                cards.Add(rawData.Substring(begin, end - begin));
                from = end;
            }
            return cards;
        }

        /// <summary>The first card in a body, or null when there is none.</summary>
        public static VCardContact ParseFirst(string rawData)
        {
            var cards = SplitCards(rawData);
            return cards.Count > 0 ? Parse(cards[0]) : null;
        }

        public static VCardContact Parse(string card)
        {
            var contact = new VCardContact();
            var lines = VCardText.Unfold(card);

            foreach (var line in lines)
            {
                string prop = VCardText.PropertyName(line);
                if (prop.Length == 0) continue;

                // X-ABLabel carries the display name of a grouped field ("item1.X-ABLabel:_$!<Home>!$_").
                // It is Apple/Google plumbing, never contact data.
                if (prop.StartsWith("X-AB", StringComparison.Ordinal)) continue;

                string head  = VCardText.Head(line);
                string value = VCardText.RawValue(line).Trim();

                switch (prop)
                {
                    case "FN":
                        contact.DisplayName = VCardText.Unescape(value);
                        break;

                    case "N":
                        ParseName(value, contact);
                        break;

                    case "EMAIL":
                    {
                        string address = VCardText.Unescape(value);
                        if (address.Length > 0)
                            contact.Emails.Add(new ContactEmail
                            {
                                Address = address,
                                Type    = NormaliseType(ExtractParam(head, "TYPE"), "home")
                            });
                        break;
                    }

                    case "TEL":
                    {
                        string number = VCardText.Unescape(value);
                        if (number.Length > 0)
                            contact.Phones.Add(new ContactPhone
                            {
                                Number = number,
                                Type   = NormaliseType(ExtractParam(head, "TYPE"), "mobile")
                            });
                        break;
                    }

                    case "ADR":
                    {
                        var address = ParseAddress(head, value);
                        if (!address.IsEmpty) contact.Addresses.Add(address);
                        break;
                    }

                    case "URL":
                    {
                        string url = VCardText.Unescape(value);
                        if (url.Length > 0)
                            contact.Websites.Add(new ContactWebsite
                            {
                                Url  = url,
                                Type = NormaliseType(ExtractParam(head, "TYPE"), "other")
                            });
                        break;
                    }

                    case "ORG":
                        // "Acme;Sales" - only the organisation name has anywhere to go on the phone.
                        contact.Organization = VCardText.Unescape(VCardText.SplitComponents(value)[0]);
                        break;

                    case "TITLE":
                        contact.JobTitle = VCardText.Unescape(value);
                        break;

                    case "NOTE":
                        contact.Notes = VCardText.Unescape(value);
                        break;

                    case "NICKNAME":
                        contact.Nickname = VCardText.Unescape(value);
                        break;

                    case "BDAY":
                        contact.Birthday = value;
                        break;

                    case "UID":
                        contact.Uid = value;
                        break;

                    case "PHOTO":
                        // Two forms exist. Inline base64 is what Google sends and what the
                        // People hub can be given; a URI form needs a second authenticated
                        // fetch, so it is recorded and otherwise left alone.
                        if (head.IndexOf("ENCODING=B", StringComparison.Ordinal) >= 0 ||
                            head.IndexOf("BASE64", StringComparison.Ordinal) >= 0)
                        {
                            contact.PhotoBase64 = value.Replace(" ", "");
                            contact.PhotoType   = ExtractParam(head, "TYPE");
                        }
                        else if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        {
                            contact.PhotoUrl = value;
                        }
                        break;
                }
            }

            if (contact.DisplayName.Length == 0)
                contact.DisplayName = (contact.FirstName + " " + contact.LastName).Trim();
            if (contact.DisplayName.Length == 0)
                contact.DisplayName = contact.Organization;

            return contact;
        }

        static void ParseName(string value, VCardContact c)
        {
            // N:Family;Given;Additional;Prefix;Suffix
            var parts = VCardText.SplitComponents(value);
            if (parts.Length > 0) c.LastName   = VCardText.Unescape(parts[0]).Trim();
            if (parts.Length > 1) c.FirstName  = VCardText.Unescape(parts[1]).Trim();
            if (parts.Length > 2) c.MiddleName = VCardText.Unescape(parts[2]).Trim();
            if (parts.Length > 3) c.NamePrefix = VCardText.Unescape(parts[3]).Trim();
            if (parts.Length > 4) c.NameSuffix = VCardText.Unescape(parts[4]).Trim();
        }

        static ContactAddress ParseAddress(string head, string value)
        {
            // ADR:PostOfficeBox;Extended;Street;Locality;Region;PostalCode;Country
            var parts = VCardText.SplitComponents(value);
            Func<int, string> at = i => parts.Length > i ? VCardText.Unescape(parts[i]).Trim() : string.Empty;

            return new ContactAddress
            {
                Street     = at(2),
                City       = at(3),
                Region     = at(4),
                PostalCode = at(5),
                Country    = at(6),
                Type       = NormaliseType(ExtractParam(head, "TYPE"), "other")
            };
        }

        /// <summary>
        /// The value of one parameter from an already upper-cased property head. Handles both
        /// "TYPE=WORK" and the vCard 2.1 shorthand "TEL;WORK;VOICE", which Google still emits
        /// for some accounts.
        /// </summary>
        static string ExtractParam(string head, string name)
        {
            var found = new List<string>();
            var segments = head.Split(';');

            for (int i = 1; i < segments.Length; i++)   // segment 0 is the property name
            {
                string segment = segments[i].Trim();
                if (segment.Length == 0) continue;

                int eq = segment.IndexOf('=');
                if (eq < 0)
                {
                    // Bare parameter - vCard 2.1 style type.
                    if (name == "TYPE") found.Add(segment.Trim('"'));
                    continue;
                }
                if (string.Compare(segment, 0, name, 0, eq, StringComparison.Ordinal) == 0 &&
                    eq == name.Length)
                    found.Add(segment.Substring(eq + 1).Trim('"'));
            }

            return string.Join(",", found.ToArray());
        }

        /// <summary>
        /// Google sends TYPE values that mix a label with a medium: "CELL,VOICE", "HOME,VOICE",
        /// "INTERNET,WORK", "PREF". Only the label part means anything here; when none of the
        /// components is a label, the caller's default applies.
        /// </summary>
        static string NormaliseType(string raw, string fallback)
        {
            if (string.IsNullOrEmpty(raw)) return fallback;

            bool fax = false;
            string label = null;

            foreach (var part in raw.ToLowerInvariant().Split(','))
            {
                string p = part.Trim();
                switch (p)
                {
                    case "home":     label = label ?? "home"; break;
                    case "work":     label = label ?? "work"; break;
                    case "cell":
                    case "mobile":
                    case "x-mobile": label = label ?? "mobile"; break;
                    case "pager":    label = label ?? "pager"; break;
                    case "other":    label = label ?? "other"; break;
                    case "fax":
                    case "x-fax":    fax = true; break;
                    // internet / voice / pref / msg / x-* say nothing about the label.
                }
            }

            // A fax is a fax whatever else the line claims; home/work only picks which slot.
            if (fax) return label == "work" ? "workfax" : "homefax";
            return label ?? fallback;
        }
    }
}
