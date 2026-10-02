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
// The phone half of the sync: VCardContact <-> the app's own contact store, which the People hub
// shows alongside every other account.
//
// This is where the port diverges most from the Windows 10 Mobile original. W10M has
// Windows.ApplicationModel.Contacts, where a Contact carries unbounded lists of phone numbers and
// email addresses, each with a free-text label. WP8.1 has Windows.Phone.PersonalInformation, where
// a StoredContact is a fixed set of named slots. ContactSlots holds that mapping and the reasoning
// behind it, ContactMapping moves the values, and this file is the WinRT around them.
//
// Values that find no slot are not silently dropped: they are handed back to the caller as vCard
// lines, stored, and put back by VCardWriter.Patch on upload. Nothing Google holds is lost by
// passing through the phone.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Phone.PersonalInformation;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;
using GContactsSync.Core.Models;
using PhoneAddress = Windows.Phone.PersonalInformation.ContactAddress;
using VCardAddress = GContactsSync.Core.Models.ContactAddress;

namespace GContactsSync.Core.Services
{
    public static class PhoneContacts
    {
        // ================================================================
        // The store
        // ================================================================
        /// <summary>
        /// Opens (creating on first use) this app's contact store.
        ///
        /// ReadWrite system access is what lets the user edit these contacts in the People hub,
        /// which is the whole basis of the phone-to-Google direction. ReadOnly application access
        /// lets other apps read the contacts in full, matching what the W10M version grants with
        /// ContactListOtherAppReadAccess.Full.
        ///
        /// The modes are fixed when the store is created; passing the same pair every time is what
        /// keeps a later open from being rejected.
        /// </summary>
        public static async Task<ContactStore> OpenAsync()
        {
            var store = await ContactStore.CreateOrOpenAsync(
                ContactStoreSystemAccessMode.ReadWrite,
                ContactStoreApplicationAccessMode.ReadOnly).AsTask().ConfigureAwait(false);

            if (store == null)
                throw new InvalidOperationException(
                    "The contact store could not be opened. Check that this app is allowed " +
                    "access to contacts in the phone's settings.");
            return store;
        }

        // ================================================================
        // Google -> phone
        // ================================================================
        /// <summary>
        /// Creates or updates the phone contact for a downloaded card. Matching is by RemoteId,
        /// which holds Google's UID, so a contact renamed on Google is updated rather than
        /// duplicated.
        /// </summary>
        public static async Task<WriteResult> WriteAsync(ContactStore store, VCardContact vc)
        {
            var result = new WriteResult();

            StoredContact contact = null;
            if (!string.IsNullOrEmpty(vc.Uid))
                contact = await store.FindContactByRemoteIdAsync(vc.Uid).AsTask().ConfigureAwait(false);

            bool isNew = contact == null;
            if (isNew)
            {
                contact = new StoredContact(store);
                contact.RemoteId = vc.Uid;
            }

            var props = await contact.GetPropertiesAsync().AsTask().ConfigureAwait(false);

            // Clear what this app owns before writing, so a field deleted on Google disappears from
            // the phone instead of lingering. ClearedKeys holds no name property, and the names are
            // assigned below rather than above: StoredContact.GivenName and the map's "GivenName"
            // are one and the same, so clearing after assigning wiped the name off every contact.
            foreach (var key in ContactSlots.ClearedKeys) props.Remove(key);

            contact.GivenName       = vc.FirstName ?? string.Empty;
            contact.FamilyName      = vc.LastName ?? string.Empty;
            contact.HonorificPrefix = vc.NamePrefix ?? string.Empty;
            contact.HonorificSuffix = vc.NameSuffix ?? string.Empty;
            contact.DisplayName     = ContactMapping.DisplayNameFor(vc);

            ContactMapping.PlacePhones(props, vc, result);
            ContactMapping.PlaceEmails(props, vc, result);
            PlaceAddresses(props, vc, result);
            ContactMapping.PlaceWebsites(props, vc, result);
            ContactMapping.PlaceScalars(props, vc);

            await contact.SaveAsync().AsTask().ConfigureAwait(false);

            // The picture goes on after the save: a brand-new StoredContact has no stored identity
            // to attach one to until it has been saved once.
            if (!string.IsNullOrEmpty(vc.PhotoBase64))
                await TrySetPictureAsync(contact, vc.PhotoBase64).ConfigureAwait(false);

            return result;
        }

        /// <summary>Removes the phone contact for a Google UID. False when there was none.</summary>
        public static async Task<bool> DeleteByUidAsync(ContactStore store, string uid)
        {
            if (string.IsNullOrEmpty(uid)) return false;

            var contact = await store.FindContactByRemoteIdAsync(uid).AsTask().ConfigureAwait(false);
            if (contact == null) return false;

            await store.DeleteContactAsync(contact.Id).AsTask().ConfigureAwait(false);
            return true;
        }

        // ================================================================
        // Phone -> Google
        // ================================================================
        /// <summary>Every contact in this app's store, as VCardContacts.</summary>
        public static async Task<List<VCardContact>> ReadAllAsync(
            ContactStore store, IProgress<string> progress)
        {
            const uint pageSize = 100;

            var query = store.CreateContactQuery();
            uint total = await query.GetContactCountAsync().AsTask().ConfigureAwait(false);

            var result = new List<VCardContact>((int)total);
            for (uint start = 0; start < total; start += pageSize)
            {
                var page = await query.GetContactsAsync(start, pageSize)
                    .AsTask().ConfigureAwait(false);
                if (page == null || page.Count == 0) break;

                foreach (var contact in page)
                    result.Add(await ReadOneAsync(contact).ConfigureAwait(false));

                if (progress != null)
                    progress.Report("Read " + result.Count + " of " + total + " from the phone...");
            }
            return result;
        }

        /// <summary>
        /// One contact by Google UID, or null when the phone has none. Used right after writing a
        /// contact, to fingerprint what the store actually kept rather than what was handed to it.
        /// </summary>
        public static async Task<VCardContact> ReadByUidAsync(ContactStore store, string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;

            var contact = await store.FindContactByRemoteIdAsync(uid).AsTask().ConfigureAwait(false);
            if (contact == null) return null;
            return await ReadOneAsync(contact).ConfigureAwait(false);
        }

        static async Task<VCardContact> ReadOneAsync(StoredContact contact)
        {
            var vc = new VCardContact
            {
                LocalId     = contact.Id ?? string.Empty,
                Uid         = contact.RemoteId ?? string.Empty,
                FirstName   = contact.GivenName ?? string.Empty,
                LastName    = contact.FamilyName ?? string.Empty,
                NamePrefix  = contact.HonorificPrefix ?? string.Empty,
                NameSuffix  = contact.HonorificSuffix ?? string.Empty,
                DisplayName = contact.DisplayName ?? string.Empty
            };

            var props = await contact.GetPropertiesAsync().AsTask().ConfigureAwait(false);

            ContactMapping.ReadPhones(vc, props);
            ContactMapping.ReadEmails(vc, props);
            ReadAddresses(vc, props);
            ContactMapping.ReadWebsite(vc, props);
            ContactMapping.ReadScalars(vc, props);

            return vc;
        }

        /// <summary>See ContactMapping.LooksUnreadable.</summary>
        public static bool LooksUnreadable(VCardContact vc)
        {
            return ContactMapping.LooksUnreadable(vc);
        }

        /// <summary>
        /// Writes Google's UID onto a phone contact, for the case of a contact that existed only on
        /// the phone and has just been created on Google.
        /// </summary>
        public static async Task SetRemoteIdAsync(ContactStore store, string localId, string uid)
        {
            if (string.IsNullOrEmpty(localId) || string.IsNullOrEmpty(uid)) return;
            try
            {
                var contact = await store.FindContactByIdAsync(localId).AsTask().ConfigureAwait(false);
                if (contact == null) return;
                contact.RemoteId = uid;
                await contact.SaveAsync().AsTask().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Only costs a duplicate on the next upload, which the UID check then resolves.
            }
        }

        // ================================================================
        // Addresses - the one slot type whose value is a WinRT object
        // ================================================================
        static void PlaceAddresses(
            IDictionary<string, object> props, VCardContact vc, WriteResult result)
        {
            foreach (var address in vc.Addresses)
            {
                if (address.IsEmpty) continue;

                string slot = ContactSlots.ForAddress(props, address.Type);
                if (slot == null)
                {
                    ContactMapping.Unplaced(result, VCardWriter.AddressLine(address));
                    continue;
                }

                props[slot] = new PhoneAddress
                {
                    StreetAddress = address.Street ?? string.Empty,
                    Locality      = address.City ?? string.Empty,
                    Region        = address.Region ?? string.Empty,
                    PostalCode    = address.PostalCode ?? string.Empty,
                    Country       = address.Country ?? string.Empty
                };
            }
        }

        static void ReadAddresses(VCardContact vc, IDictionary<string, object> props)
        {
            foreach (var entry in ContactSlots.AddressReadOrder)
            {
                object value;
                if (!props.TryGetValue(entry.Slot, out value)) continue;

                var address = value as PhoneAddress;
                if (address == null) continue;

                var mapped = new VCardAddress
                {
                    Street     = address.StreetAddress ?? string.Empty,
                    City       = address.Locality ?? string.Empty,
                    Region     = address.Region ?? string.Empty,
                    PostalCode = address.PostalCode ?? string.Empty,
                    Country    = address.Country ?? string.Empty,
                    Type       = entry.Type
                };
                if (!mapped.IsEmpty) vc.Addresses.Add(mapped);
            }
        }

        // ================================================================
        // Display picture
        // ================================================================
        static async Task TrySetPictureAsync(StoredContact contact, string base64)
        {
            try
            {
                var buffer = CryptographicBuffer.DecodeFromBase64String(base64);
                using (var stream = new InMemoryRandomAccessStream())
                {
                    await stream.WriteAsync(buffer).AsTask().ConfigureAwait(false);
                    stream.Seek(0);
                    await contact.SetDisplayPictureAsync(stream).AsTask().ConfigureAwait(false);
                }
                await contact.SaveAsync().AsTask().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A malformed or unsupported photo must not cost us the contact.
            }
        }
    }
}
