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
// Everything the app has to remember between syncs:
//
//   Etags   path on Google      -> etag, so a download can ask only for what changed
//   Hrefs   Google UID          -> path, so an upload PUTs over the right card
//   Hashes  Google UID          -> hash of the contact as it reads back off the phone, so an
//                                  upload knows which contacts the user actually edited
//   Unplaced Google UID         -> vCard lines the phone's contact store had no slot for, so an
//                                  upload can put them back instead of deleting them
//
// The presence of any etags is also what "have we ever synced?" means, exactly as on Windows 10
// Mobile: no etags, so the next download is a full one.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace GContactsSync.Core.Storage
{
    public sealed class SyncState
    {
        const string EtagsFile  = "etags.tsv";
        const string HrefsFile  = "hrefs.tsv";
        const string HashesFile = "hashes.tsv";
        const string UnplacedFile = "unplaced.tsv";

        // Unit separator: cannot occur in a vCard line, and survives the tab-separated map file.
        const char LineSeparator = '\u001f';

        public Dictionary<string, string> Etags  = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> Hrefs  = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> Hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, string> Unplaced = new Dictionary<string, string>(StringComparer.Ordinal);

        public bool HasSynced { get { return Etags.Count > 0; } }

        public static async Task<SyncState> LoadAsync()
        {
            return new SyncState
            {
                Etags  = await LocalFiles.ReadMapAsync(EtagsFile).ConfigureAwait(false),
                Hrefs  = await LocalFiles.ReadMapAsync(HrefsFile).ConfigureAwait(false),
                Hashes = await LocalFiles.ReadMapAsync(HashesFile).ConfigureAwait(false),
                Unplaced = await LocalFiles.ReadMapAsync(UnplacedFile).ConfigureAwait(false)
            };
        }

        public async Task SaveAsync()
        {
            await LocalFiles.WriteMapAsync(EtagsFile, Etags).ConfigureAwait(false);
            await LocalFiles.WriteMapAsync(HrefsFile, Hrefs).ConfigureAwait(false);
            await LocalFiles.WriteMapAsync(HashesFile, Hashes).ConfigureAwait(false);
            await LocalFiles.WriteMapAsync(UnplacedFile, Unplaced).ConfigureAwait(false);
        }

        /// <summary>Records the lines that had no slot on the phone for one contact.</summary>
        public void SetUnplaced(string uid, List<string> lines)
        {
            if (string.IsNullOrEmpty(uid)) return;
            if (lines == null || lines.Count == 0) { Unplaced.Remove(uid); return; }
            Unplaced[uid] = string.Join(LineSeparator.ToString(), lines.ToArray());
        }

        public List<string> GetUnplaced(string uid)
        {
            string packed;
            if (string.IsNullOrEmpty(uid) || !Unplaced.TryGetValue(uid, out packed) ||
                string.IsNullOrEmpty(packed))
                return new List<string>();

            return new List<string>(packed.Split(LineSeparator));
        }

        /// <summary>Drops every record for a contact that no longer exists on Google.</summary>
        public void Forget(string uid, string href)
        {
            if (!string.IsNullOrEmpty(href)) Etags.Remove(href);
            if (string.IsNullOrEmpty(uid)) return;
            Hrefs.Remove(uid);
            Hashes.Remove(uid);
            Unplaced.Remove(uid);
        }

        /// <summary>Forgets every sync record, so the next download starts from scratch.</summary>
        public static async Task ClearAsync()
        {
            await LocalFiles.DeleteAsync(EtagsFile).ConfigureAwait(false);
            await LocalFiles.DeleteAsync(HrefsFile).ConfigureAwait(false);
            await LocalFiles.DeleteAsync(HashesFile).ConfigureAwait(false);
            await LocalFiles.DeleteAsync(UnplacedFile).ConfigureAwait(false);
            await CardCache.ClearAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// The UID a contact path belongs to. Google names every card &lt;uid&gt;.vcf, and the
        /// deleted-contact case has nothing but the path to work from.
        /// </summary>
        public static string UidFromHref(string href)
        {
            if (string.IsNullOrEmpty(href)) return string.Empty;

            int slash = href.LastIndexOf('/');
            string name = slash >= 0 ? href.Substring(slash + 1) : href;
            if (name.EndsWith(".vcf", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);

            try { return Uri.UnescapeDataString(name); }
            catch (Exception) { return name; }
        }
    }
}
