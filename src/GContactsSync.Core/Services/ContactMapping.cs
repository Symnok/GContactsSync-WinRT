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
// Moving values between a VCardContact and a StoredContact's property map, both ways.
//
// A StoredContact's properties are an IDictionary<string, object>, and every type that goes in one
// is a plain string or a DateTimeOffset - so all of this can be written, and tested, without any
// reference to WinRT. That is the point of the file: tests\ compiles it straight from here and
// exercises the placement rules on the desktop, which is the only way to check them without a
// phone in hand. PhoneContacts holds the parts that genuinely need the platform: opening the
// store, reading and saving a StoredContact, the display picture, and addresses (whose value in
// the map is a WinRT ContactAddress).
using System;
using System.Collections.Generic;
using GContactsSync.Core.Models;

namespace GContactsSync.Core.Services
{
    /// <summary>What writing one contact to the phone could not fit.</summary>
    public sealed class WriteResult
    {
        /// <summary>vCard lines for values that had no slot. Empty when everything fitted.</summary>
        public List<string> UnplacedLines = new List<string>();
    }

    internal static class ContactMapping
    {
        // ================================================================
        // VCardContact -> property map
        // ================================================================
        public static void PlacePhones(
            IDictionary<string, object> props, VCardContact vc, WriteResult result)
        {
            foreach (var phone in vc.Phones)
            {
                if (string.IsNullOrEmpty(phone.Number)) continue;

                string slot = ContactSlots.ForPhone(props, phone.Type);
                if (slot != null) props[slot] = phone.Number;
                else Unplaced(result, VCardWriter.PhoneLine(phone));
            }
        }

        public static void PlaceEmails(
            IDictionary<string, object> props, VCardContact vc, WriteResult result)
        {
            foreach (var email in vc.Emails)
            {
                if (string.IsNullOrEmpty(email.Address)) continue;

                string slot = ContactSlots.ForEmail(props, email.Type);
                if (slot != null) props[slot] = email.Address;
                else Unplaced(result, VCardWriter.EmailLine(email));
            }
        }

        public static void PlaceWebsites(
            IDictionary<string, object> props, VCardContact vc, WriteResult result)
        {
            // One website slot, so the first survives on the phone and the rest are preserved only
            // for the upload. The slot carries no type, so a work or home label on the website
            // that does fit is not round-tripped.
            foreach (var website in vc.Websites)
            {
                if (string.IsNullOrEmpty(website.Url)) continue;

                if (!props.ContainsKey(ContactSlots.WebsiteSlot))
                    props[ContactSlots.WebsiteSlot] = website.Url;
                else
                    Unplaced(result, VCardWriter.WebsiteLine(website));
            }
        }

        public static void PlaceScalars(IDictionary<string, object> props, VCardContact vc)
        {
            Set(props, "CompanyName", vc.Organization);
            Set(props, "JobTitle", vc.JobTitle);
            Set(props, "Notes", vc.Notes);
            Set(props, "Nickname", vc.Nickname);
            Set(props, "AdditionalName", vc.MiddleName);

            DateTimeOffset birthday;
            if (TryParseBirthday(vc.Birthday, out birthday)) props["Birthdate"] = birthday;
        }

        public static void Unplaced(WriteResult result, string line)
        {
            if (!string.IsNullOrEmpty(line)) result.UnplacedLines.Add(line);
        }

        // ================================================================
        // Property map -> VCardContact
        // ================================================================
        public static void ReadPhones(VCardContact vc, IDictionary<string, object> props)
        {
            foreach (var entry in ContactSlots.PhoneReadOrder)
            {
                string number = GetString(props, entry.Slot);
                if (number.Length > 0)
                    vc.Phones.Add(new ContactPhone { Number = number, Type = entry.Type });
            }
        }

        public static void ReadEmails(VCardContact vc, IDictionary<string, object> props)
        {
            foreach (var entry in ContactSlots.EmailReadOrder)
            {
                string address = GetString(props, entry.Slot);
                if (address.Length > 0)
                    vc.Emails.Add(new ContactEmail { Address = address, Type = entry.Type });
            }
        }

        public static void ReadWebsite(VCardContact vc, IDictionary<string, object> props)
        {
            string url = GetString(props, ContactSlots.WebsiteSlot);
            if (url.Length > 0) vc.Websites.Add(new ContactWebsite { Url = url, Type = "other" });
        }

        public static void ReadScalars(VCardContact vc, IDictionary<string, object> props)
        {
            vc.Organization = GetString(props, "CompanyName");
            vc.JobTitle     = GetString(props, "JobTitle");
            vc.Notes        = GetString(props, "Notes");
            vc.Nickname     = GetString(props, "Nickname");
            vc.MiddleName   = GetString(props, "AdditionalName");

            object birthdate;
            if (props.TryGetValue("Birthdate", out birthdate) && birthdate is DateTimeOffset)
                vc.Birthday = ((DateTimeOffset)birthdate).ToString("yyyy-MM-dd");
        }

        // ================================================================
        // Shared helpers
        // ================================================================
        /// <summary>
        /// What the People hub should show. Setting it explicitly is how a contact with only an
        /// organisation name, or only an email address, still reads sensibly.
        /// </summary>
        public static string DisplayNameFor(VCardContact vc)
        {
            string name = vc.DisplayName;
            if (string.IsNullOrEmpty(name)) name = (vc.FirstName + " " + vc.LastName).Trim();
            if (string.IsNullOrEmpty(name)) name = vc.Organization;
            if (string.IsNullOrEmpty(name) && vc.Emails.Count > 0) name = vc.Emails[0].Address;
            return name ?? string.Empty;
        }

        /// <summary>
        /// True when a contact read back from the phone looks empty enough that uploading it would
        /// destroy the Google copy. A guard, not an expected case: the store should always return
        /// the fields that were written to it.
        /// </summary>
        public static bool LooksUnreadable(VCardContact vc)
        {
            return string.IsNullOrEmpty(vc.FirstName)
                && string.IsNullOrEmpty(vc.LastName)
                && string.IsNullOrEmpty(vc.DisplayName)
                && string.IsNullOrEmpty(vc.Organization)
                && vc.Phones.Count == 0
                && vc.Emails.Count == 0;
        }

        public static void Set(IDictionary<string, object> props, string key, string value)
        {
            if (!string.IsNullOrEmpty(value)) props[key] = value;
        }

        public static string GetString(IDictionary<string, object> props, string key)
        {
            object value;
            if (!props.TryGetValue(key, out value)) return string.Empty;
            var text = value as string;
            return text == null ? string.Empty : text.Trim();
        }

        public static bool TryParseBirthday(string raw, out DateTimeOffset result)
        {
            result = DateTimeOffset.MinValue;
            if (string.IsNullOrEmpty(raw)) return false;

            // Google sends yyyy-MM-dd or yyyyMMdd. A contact with no birth year comes as --MMdd,
            // which a DateTimeOffset cannot hold, so it stays on Google only - and because an
            // upload patches the stored card rather than rebuilding it, staying is enough.
            string digits = raw.Trim().Replace("-", "");
            if (digits.Length < 8) return false;

            int year, month, day;
            if (!int.TryParse(digits.Substring(0, 4), out year)) return false;
            if (!int.TryParse(digits.Substring(4, 2), out month)) return false;
            if (!int.TryParse(digits.Substring(6, 2), out day)) return false;

            try
            {
                result = new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero);
                return true;
            }
            catch (Exception)
            {
                return false;   // 31 February and friends
            }
        }
    }
}
