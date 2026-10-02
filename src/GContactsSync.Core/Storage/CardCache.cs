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
// The last card Google sent for each contact, kept verbatim, one file per contact.
//
// This exists because a CardDAV PUT replaces the whole card and a WP8.1 StoredContact cannot
// hold everything a Google contact can - the photo, categories, IMPP handles, the X- properties
// Google keeps for its own use, and any phone number past the tenth. Uploading a card built
// only from what the phone holds would delete all of it. Instead VCardWriter.Patch rewrites
// just the properties the phone owns, inside the card stored here.
//
// One file per contact rather than one big file: a full address book with photos is megabytes,
// and rewriting all of it to record a single changed contact would be slow enough to notice.
using System;
using System.Threading.Tasks;
using Windows.Storage;

namespace GContactsSync.Core.Storage
{
    public static class CardCache
    {
        const string FolderName = "cards";

        static async Task<StorageFolder> FolderAsync()
        {
            return await ApplicationData.Current.LocalFolder
                .CreateFolderAsync(FolderName, CreationCollisionOption.OpenIfExists)
                .AsTask().ConfigureAwait(false);
        }

        /// <summary>The stored card for a UID, or null when there is none.</summary>
        public static async Task<string> ReadAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return null;
            try
            {
                var folder = await FolderAsync().ConfigureAwait(false);
                var file = await folder.GetFileAsync(NameFor(uid)).AsTask().ConfigureAwait(false);
                return await FileIO.ReadTextAsync(file).AsTask().ConfigureAwait(false);
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static async Task WriteAsync(string uid, string card)
        {
            if (string.IsNullOrEmpty(uid) || string.IsNullOrEmpty(card)) return;
            try
            {
                var folder = await FolderAsync().ConfigureAwait(false);
                var file = await folder
                    .CreateFileAsync(NameFor(uid), CreationCollisionOption.ReplaceExisting)
                    .AsTask().ConfigureAwait(false);
                await FileIO.WriteTextAsync(file, card).AsTask().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A card that cannot be cached only costs fidelity on the next upload, which is
                // not worth failing a sync over.
            }
        }

        public static async Task DeleteAsync(string uid)
        {
            if (string.IsNullOrEmpty(uid)) return;
            try
            {
                var folder = await FolderAsync().ConfigureAwait(false);
                var file = await folder.GetFileAsync(NameFor(uid)).AsTask().ConfigureAwait(false);
                await file.DeleteAsync().AsTask().ConfigureAwait(false);
            }
            catch (Exception) { }
        }

        public static async Task ClearAsync()
        {
            try
            {
                var folder = await ApplicationData.Current.LocalFolder
                    .GetFolderAsync(FolderName).AsTask().ConfigureAwait(false);
                await folder.DeleteAsync(StorageDeleteOption.PermanentDelete)
                    .AsTask().ConfigureAwait(false);
            }
            catch (Exception) { }
        }

        static string NameFor(string uid)
        {
            return LocalFiles.SafeFileName(uid, ".vcf");
        }
    }
}
