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
// Which named property of a WP8.1 StoredContact a value goes in, and what a value found in one
// means when it is read back.
//
// A StoredContact is not a list of labelled values the way a Windows 10 Mobile Contact is: it is
// a fixed set of named slots (MobileTelephone, Telephone, WorkFax, WorkEmail, OtherAddress...),
// and the slot IS the label. So mapping a vCard onto it is a placement problem, and it has to be
// reversible - if a number written to Telephone read back as anything but "home", every download
// would look like an edit and the app would upload the whole address book back to Google.
//
// The slot names are STRINGS, not an enumeration, so a name that does not exist compiles happily
// and fails only on the phone. KnownNames below is the real list, and a test checks these tables
// against it.
//
// The tables live here, apart from the WinRT calls in PhoneContacts, so the placement rules can
// be exercised without a phone. ReadOrder is the write tables read backwards, and being one table
// is what keeps the two directions from drifting.
//
// The one rule that is not obvious: a voice number never falls back into a fax slot. Filling
// HomeFax with a spare mobile number would show the user a fax they do not have and send it back
// to Google as TYPE=FAX - worse than not showing the number at all.
using System.Collections.Generic;

namespace GContactsSync.Core.Services
{
    /// <summary>One slot and the vCard label a value in it carries.</summary>
    internal struct SlotType
    {
        public readonly string Slot;
        public readonly string Type;

        public SlotType(string slot, string type)
        {
            Slot = slot;
            Type = type;
        }
    }

    internal static class ContactSlots
    {
        // ---- phones -----------------------------------------------------------
        //
        // The home phone is "Telephone". There is NO "HomeTelephone" - the name reads so plausibly
        // next to WorkTelephone and MobileTelephone that this file used it for months, and it
        // compiles, because these are strings and not an enumeration. The phone answers an unknown
        // property name with ArgumentException "The parameter is incorrect.", which killed the
        // write of every contact that had a home number. KnownNames below is now checked by a test.
        //
        // There is likewise no slot for an "other" number: nine slots exist, seven voice and two
        // fax, and none of them is generic. An unlabelled number takes the home slot, and so reads
        // back as a home number - see the note on PhoneReadOrder.
        static readonly string[] PhoneMobile  = { "MobileTelephone", "AlternateMobileTelephone" };
        static readonly string[] PhoneHome    = { "Telephone", "AlternateTelephone" };
        static readonly string[] PhoneWork    = { "WorkTelephone", "AlternateWorkTelephone", "CompanyTelephone" };
        static readonly string[] PhoneOther   = { "Telephone", "AlternateTelephone" };
        static readonly string[] PhoneHomeFax = { "HomeFax" };
        static readonly string[] PhoneWorkFax = { "WorkFax" };

        static readonly string[] AnyVoice =
        {
            "MobileTelephone", "Telephone", "WorkTelephone",
            "AlternateMobileTelephone", "AlternateTelephone", "AlternateWorkTelephone",
            "CompanyTelephone"
        };
        static readonly string[] AnyFax = { "HomeFax", "WorkFax" };

        /// <summary>
        /// Every phone slot with the label it stands for, in the order an upload should list them.
        /// Mobile first, then home, work, fax - which is how a person reads a contact.
        ///
        /// Nothing reads back as "other", because nothing on the phone can hold that label. A
        /// number Google calls Other comes back as home, and an upload would relabel it on Google.
        /// That is the honest cost of a fixed set of slots; the alternative - refusing to place it
        /// at all - would keep the label by hiding the number from the phone, which is worse.
        /// </summary>
        public static readonly SlotType[] PhoneReadOrder =
        {
            new SlotType("MobileTelephone", "mobile"),
            new SlotType("AlternateMobileTelephone", "mobile"),
            new SlotType("Telephone", "home"),
            new SlotType("AlternateTelephone", "home"),
            new SlotType("WorkTelephone", "work"),
            new SlotType("AlternateWorkTelephone", "work"),
            new SlotType("CompanyTelephone", "work"),
            new SlotType("HomeFax", "homefax"),
            new SlotType("WorkFax", "workfax")
        };

        // ---- email ------------------------------------------------------------
        static readonly string[] EmailHome  = { "Email", "OtherEmail" };
        static readonly string[] EmailWork  = { "WorkEmail" };
        static readonly string[] EmailOther = { "OtherEmail", "Email" };
        static readonly string[] AnyEmail   = { "Email", "WorkEmail", "OtherEmail" };

        public static readonly SlotType[] EmailReadOrder =
        {
            new SlotType("Email", "home"),
            new SlotType("WorkEmail", "work"),
            new SlotType("OtherEmail", "other")
        };

        // ---- addresses --------------------------------------------------------
        static readonly string[] AddressHome  = { "Address" };
        static readonly string[] AddressWork  = { "WorkAddress" };
        static readonly string[] AddressOther = { "OtherAddress" };
        static readonly string[] AnyAddress   = { "Address", "WorkAddress", "OtherAddress" };

        public static readonly SlotType[] AddressReadOrder =
        {
            new SlotType("Address", "home"),
            new SlotType("WorkAddress", "work"),
            new SlotType("OtherAddress", "other")
        };

        /// <summary>The single website slot. Carrying no type, it always reads back as "other".</summary>
        public const string WebsiteSlot = "Url";

        /// <summary>
        /// The keys a rewrite clears out of the property map before writing, so a field deleted on
        /// Google disappears from the phone instead of lingering.
        ///
        /// GivenName, FamilyName, HonorificPrefix, HonorificSuffix and DisplayName are deliberately
        /// NOT here. They are the same storage as StoredContact's properties of those names, which
        /// is what PhoneContacts assigns them through - so listing them here removed the name that
        /// had just been set, and every contact arrived on the phone nameless. They need no clearing
        /// anyway: those five are assigned on every write, empty string included.
        ///
        /// Anything reachable only through the map does belong here, because PlaceScalars writes a
        /// scalar only when it has a value.
        /// </summary>
        public static readonly string[] ClearedKeys =
        {
            "MobileTelephone", "Telephone", "WorkTelephone", "AlternateMobileTelephone",
            "AlternateTelephone", "AlternateWorkTelephone", "CompanyTelephone",
            "HomeFax", "WorkFax",
            "Email", "WorkEmail", "OtherEmail",
            "Address", "WorkAddress", "OtherAddress",
            "Url", "CompanyName", "JobTitle", "Notes", "Nickname", "Birthdate",
            "AdditionalName"
        };

        /// <summary>
        /// The properties PhoneContacts sets through StoredContact itself rather than through the
        /// property map. Clearing one of these out of the map clears the property, so none of them
        /// may appear in ClearedKeys.
        /// </summary>
        public static readonly string[] TypedProperties =
        {
            "GivenName", "FamilyName", "HonorificPrefix", "HonorificSuffix", "DisplayName"
        };

        /// <summary>
        /// Every property a WP8.1 StoredContact recognises: the members of
        /// Windows.Phone.PersonalInformation.KnownContactProperties, verbatim.
        ///
        /// It is repeated here rather than referenced because the real thing is WinRT, and the one
        /// place these tables can be checked without a phone is the desktop test run. A name that
        /// is not in this list is not a property: it compiles, and the phone rejects the whole
        /// contact at run time with "The parameter is incorrect."
        ///
        /// Regenerate with:
        ///   ildasm /text /item:Windows.Phone.PersonalInformation.KnownContactProperties
        ///          "%ProgramFiles(x86)%\Windows Phone Kits\8.1\References\CommonConfiguration\Neutral\Windows.winmd"
        /// </summary>
        public static readonly string[] KnownNames =
        {
            "AdditionalName", "Address", "AlternateMobileTelephone", "AlternateTelephone",
            "AlternateWorkTelephone", "Anniversary", "Birthdate", "Children", "CompanyName",
            "CompanyTelephone", "DisplayName", "Email", "FamilyName", "GivenName", "HomeFax",
            "HonorificPrefix", "HonorificSuffix", "JobTitle", "Manager", "MobileTelephone",
            "Nickname", "Notes", "OfficeLocation", "OtherAddress", "OtherEmail",
            "SignificantOther", "Telephone", "Url", "WorkAddress", "WorkEmail", "WorkFax",
            "WorkTelephone", "YomiCompanyName", "YomiFamilyName", "YomiGivenName"
        };

        /// <summary>Every slot name these tables can ever hand to the contact store.</summary>
        public static List<string> AllUsedNames()
        {
            var names = new List<string>();

            foreach (var group in new[] { PhoneMobile, PhoneHome, PhoneWork, PhoneOther,
                                          PhoneHomeFax, PhoneWorkFax, AnyVoice, AnyFax,
                                          EmailHome, EmailWork, EmailOther, AnyEmail,
                                          AddressHome, AddressWork, AddressOther, AnyAddress,
                                          ClearedKeys, TypedProperties })
                names.AddRange(group);

            foreach (var table in new[] { PhoneReadOrder, EmailReadOrder, AddressReadOrder })
                foreach (var entry in table) names.Add(entry.Slot);

            names.Add(WebsiteSlot);
            return names;
        }

        // ================================================================
        // Placement
        // ================================================================
        /// <summary>The slot a phone number of this type should take, or null when none is free.</summary>
        public static string ForPhone(IDictionary<string, object> taken, string type)
        {
            bool isFax = type == "homefax" || type == "workfax";

            string[] preferred;
            switch (type)
            {
                case "mobile":  preferred = PhoneMobile; break;
                case "home":    preferred = PhoneHome; break;
                case "work":    preferred = PhoneWork; break;
                case "homefax": preferred = PhoneHomeFax; break;
                case "workfax": preferred = PhoneWorkFax; break;
                default:        preferred = PhoneOther; break;
            }

            return FirstFree(taken, preferred) ?? FirstFree(taken, isFax ? AnyFax : AnyVoice);
        }

        public static string ForEmail(IDictionary<string, object> taken, string type)
        {
            string[] preferred;
            switch (type)
            {
                case "work": preferred = EmailWork; break;
                case "home": preferred = EmailHome; break;
                default:     preferred = EmailOther; break;
            }
            return FirstFree(taken, preferred) ?? FirstFree(taken, AnyEmail);
        }

        public static string ForAddress(IDictionary<string, object> taken, string type)
        {
            string[] preferred;
            switch (type)
            {
                case "home": preferred = AddressHome; break;
                case "work": preferred = AddressWork; break;
                default:     preferred = AddressOther; break;
            }
            return FirstFree(taken, preferred) ?? FirstFree(taken, AnyAddress);
        }

        static string FirstFree(IDictionary<string, object> taken, string[] slots)
        {
            foreach (var slot in slots)
                if (!taken.ContainsKey(slot)) return slot;
            return null;
        }
    }
}
