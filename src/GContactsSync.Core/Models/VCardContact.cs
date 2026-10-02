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
// One contact, in the only shape this app keeps in memory. Both directions go through it:
//
//     Google  ->  vCard text  ->  VCardContact  ->  StoredContact (People hub)
//     phone   ->  StoredContact -> VCardContact ->  vCard text -> Google
//
// so the parser, the serializer and the phone bridge all agree on one model and none of them
// has to know about the others' platform.
using System.Collections.Generic;

namespace GContactsSync.Core.Models
{
    public class VCardContact
    {
        public string DisplayName = string.Empty;
        public string FirstName   = string.Empty;
        public string LastName    = string.Empty;
        public string MiddleName  = string.Empty;
        public string NamePrefix  = string.Empty;
        public string NameSuffix  = string.Empty;

        public List<ContactPhone>   Phones    = new List<ContactPhone>();
        public List<ContactEmail>   Emails    = new List<ContactEmail>();
        public List<ContactAddress> Addresses = new List<ContactAddress>();
        public List<ContactWebsite> Websites  = new List<ContactWebsite>();

        public string Organization = string.Empty;
        public string JobTitle     = string.Empty;
        public string Notes        = string.Empty;
        public string Nickname     = string.Empty;
        public string Birthday     = string.Empty;   // as sent: yyyyMMdd or yyyy-MM-dd

        /// <summary>Inline photo bytes, base64 as the vCard carried them (PHOTO;ENCODING=b).</summary>
        public string PhotoBase64  = string.Empty;
        /// <summary>JPEG / PNG / GIF, from the PHOTO line's TYPE parameter.</summary>
        public string PhotoType    = string.Empty;
        /// <summary>Set instead of PhotoBase64 when the card references a photo by URL.</summary>
        public string PhotoUrl     = string.Empty;

        /// <summary>Google's UID. Also the phone contact's RemoteId, which is how the two are tied.</summary>
        public string Uid  = string.Empty;
        public string Etag = string.Empty;
        /// <summary>CardDAV path, e.g. /carddav/v1/principals/me@gmail.com/lists/default/&lt;uid&gt;.vcf</summary>
        public string Href = string.Empty;

        /// <summary>StoredContact.Id - only set for contacts read back off the phone.</summary>
        public string LocalId = string.Empty;
    }

    public class ContactPhone
    {
        public string Number = string.Empty;
        /// <summary>Normalised label: mobile / home / work / fax / pager / other.</summary>
        public string Type   = "mobile";
    }

    public class ContactEmail
    {
        public string Address = string.Empty;
        /// <summary>Normalised label: home / work / other.</summary>
        public string Type    = "home";
    }

    public class ContactAddress
    {
        public string Street     = string.Empty;
        public string City       = string.Empty;
        public string Region     = string.Empty;
        public string PostalCode = string.Empty;
        public string Country    = string.Empty;
        /// <summary>Normalised label: home / work / other.</summary>
        public string Type       = "other";

        public bool IsEmpty
        {
            get
            {
                return string.IsNullOrEmpty(Street) && string.IsNullOrEmpty(City)
                    && string.IsNullOrEmpty(Region) && string.IsNullOrEmpty(PostalCode)
                    && string.IsNullOrEmpty(Country);
            }
        }
    }

    public class ContactWebsite
    {
        public string Url  = string.Empty;
        public string Type = "other";
    }
}
