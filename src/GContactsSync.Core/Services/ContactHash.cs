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
// A fingerprint of a contact as the phone holds it, which is how "the user edited this one"
// is detected for the phone-to-Google direction: after a download the fingerprint of every
// contact is recorded, and an upload only considers contacts whose fingerprint has moved.
//
// The one rule that matters: both sides of the comparison must be computed from a contact
// *read back out of the phone's store*, never from the card that was written to it. Slot
// capacity, ordering and the store's own normalisation all mean the two differ, and hashing the
// written form would make every contact look edited on the very next upload. SyncEngine
// therefore re-reads what it has just written.
using System;
using System.Text;
using GContactsSync.Core.Models;

namespace GContactsSync.Core.Services
{
    public static class ContactHash
    {
        public static string Compute(VCardContact vc)
        {
            var sb = new StringBuilder();

            Append(sb, vc.FirstName);
            Append(sb, vc.LastName);
            Append(sb, vc.MiddleName);
            Append(sb, vc.NamePrefix);
            Append(sb, vc.NameSuffix);
            Append(sb, vc.Nickname);
            Append(sb, vc.Organization);
            Append(sb, vc.JobTitle);
            Append(sb, vc.Notes);
            Append(sb, vc.Birthday);

            foreach (var phone in vc.Phones)
            {
                sb.Append("|T:").Append(phone.Type).Append(':');
                // Formatting differences alone ("+1 800-555" against "+18005550") are not edits.
                sb.Append(VCardWriter.NormalisePhone(phone.Number));
            }
            foreach (var email in vc.Emails)
            {
                sb.Append("|E:").Append(email.Type).Append(':');
                sb.Append((email.Address ?? string.Empty).Trim().ToLowerInvariant());
            }
            foreach (var address in vc.Addresses)
            {
                sb.Append("|A:").Append(address.Type).Append(':');
                Append(sb, address.Street);
                Append(sb, address.City);
                Append(sb, address.Region);
                Append(sb, address.PostalCode);
                Append(sb, address.Country);
            }
            foreach (var website in vc.Websites)
            {
                sb.Append("|U:");
                Append(sb, website.Url);
            }

            return Fnv64(sb.ToString());
        }

        static void Append(StringBuilder sb, string value)
        {
            sb.Append('|');
            if (!string.IsNullOrEmpty(value)) sb.Append(value.Trim().ToLowerInvariant());
        }

        /// <summary>
        /// FNV-1a, 64 bit. Not a cryptographic hash and does not need to be - the only question
        /// it answers is "is this byte-for-byte what we recorded last time".
        /// </summary>
        static string Fnv64(string text)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char ch in text)
            {
                hash ^= (byte)(ch & 0xFF);
                hash *= 1099511628211UL;
                hash ^= (byte)(ch >> 8);
                hash *= 1099511628211UL;
            }
            return hash.ToString("x16");
        }
    }
}
