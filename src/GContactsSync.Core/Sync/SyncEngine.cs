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
// The two directions of sync, with exactly the shape the Windows 10 Mobile version has:
//
//   Download (Google -> phone)   manual, and on a timer of at least 15 minutes. The first run
//                                fetches everything; every run after that compares stored etags
//                                against the server's and touches only what moved.
//
//   Upload (phone -> Google)     manual only, never on a timer. Contacts whose fingerprint has
//                                not changed since the last download are left alone.
//
// Both live here rather than in the pages, because the background task has to run the download
// with no UI at all - the W10M version duplicates its whole sync in the background component,
// and the two copies have already drifted apart there.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Phone.PersonalInformation;
using GContactsSync.Core.Models;
using GContactsSync.Core.Net;
using GContactsSync.Core.Services;
using GContactsSync.Core.Storage;

namespace GContactsSync.Core.Sync
{
    public sealed class SyncOutcome
    {
        public bool Success;
        public bool WasFullSync;

        public int Written;      // contacts created or updated on the phone
        public int Removed;      // contacts deleted from the phone
        public int Uploaded;     // contacts sent to Google
        public int Conflicts;    // contacts Google had changed in the meantime
        public int Failed;
        public int Skipped;

        // A download that writes nothing used to say only "0 contacts downloaded", which is the
        // one answer that tells the user nothing at all: it cannot be told apart from an empty
        // address book, from cards that never arrived, or from a phone refusing every write.
        // These three record which of those it was, and they are reported.
        public int Listed;              // contacts Google's listing named
        public int Attempted;           // of those, the ones actually asked for
        public int Received;            // cards that actually came back
        public string FirstFailure = string.Empty;   // why the first unwritten contact failed
        public bool GaveUp;             // stopped early; every write was failing the same way

        /// <summary>One line, ready to show to the user.</summary>
        public string Message = string.Empty;
        /// <summary>Set when Success is false; safe to show as it is.</summary>
        public string Error = string.Empty;

        public bool NothingToDo;
    }

    public static class SyncEngine
    {
        /// <summary>Text for the "another sync is already running" case, shared by both directions.</summary>
        const string BusyMessage =
            "Another sync is already running. Try again in a moment.";

        /// <summary>
        /// How many contacts may fail, with none succeeding, before a download stops trying.
        /// Two full multiget batches: enough that a handful of odd cards cannot trigger it.
        /// </summary>
        const int GiveUpAfter = 50;

        // ================================================================
        // GOOGLE -> PHONE
        // ================================================================
        public static async Task<SyncOutcome> DownloadAsync(
            string email, string password, IProgress<string> progress, CancellationToken ct)
        {
            var outcome = new SyncOutcome();

            if (!AppSettings.TryBeginSync())
            {
                outcome.Error = BusyMessage;
                return outcome;
            }

            CardDavClient client = null;
            try
            {
                Report(progress, "Loading certificates...");
                var trustBundle = await TrustBundle.LoadAsync().ConfigureAwait(false);

                client = new CardDavClient(email, password, trustBundle);
                var state = await SyncState.LoadAsync().ConfigureAwait(false);
                var store = await PhoneContacts.OpenAsync().ConfigureAwait(false);

                outcome.WasFullSync = !state.HasSynced;

                if (outcome.WasFullSync)
                    await FullDownloadAsync(client, store, state, outcome, progress, ct)
                        .ConfigureAwait(false);
                else
                    await IncrementalDownloadAsync(client, store, state, outcome, progress, ct)
                        .ConfigureAwait(false);

                await state.SaveAsync().ConfigureAwait(false);

                outcome.Success = true;
                AppSettings.LastDownload = Timestamp();

                outcome.Message = DescribeDownload(outcome);
                return outcome;
            }
            catch (OperationCanceledException)
            {
                outcome.Error = "Sync cancelled.";
                return outcome;
            }
            catch (CardDavException ex)
            {
                outcome.Error = ex.Message;
                return outcome;
            }
            catch (Exception ex)
            {
                outcome.Error = ex.Message;
                return outcome;
            }
            finally
            {
                if (client != null) client.Dispose();
                AppSettings.EndSync();
            }
        }

        static async Task FullDownloadAsync(
            CardDavClient client, ContactStore store, SyncState state,
            SyncOutcome outcome, IProgress<string> progress, CancellationToken ct)
        {
            Report(progress, "Asking Google for the contact list...");
            var listing = await client.ListAsync(ct).ConfigureAwait(false);

            if (listing.Etags.Count == 0)
            {
                outcome.NothingToDo = true;
                return;
            }

            Report(progress, "Found " + listing.Etags.Count + " contacts. Downloading...");

            var hrefs = new List<string>(listing.Etags.Keys);

            // Every UID Google currently has, whether or not its card downloaded cleanly. Used
            // below to decide what on the phone is a stray - a contact that failed to download must
            // not be mistaken for one Google deleted. Three sources because a card's UID need not
            // equal the file name Google chose: the cards that did arrive say so themselves, any
            // path recorded by an earlier sync says so, and the file name is the last resort.
            var onGoogle = new HashSet<string>(StringComparer.Ordinal);
            foreach (var href in hrefs) onGoogle.Add(SyncState.UidFromHref(href));
            var listed = new HashSet<string>(hrefs, StringComparer.Ordinal);
            foreach (var kv in state.Hrefs)
                if (listed.Contains(kv.Value)) onGoogle.Add(kv.Key);

            var stored = new HashSet<string>(StringComparer.Ordinal);
            await FetchAndStoreAsync(client, store, state, hrefs, listing.Etags,
                                     outcome, stored, onGoogle, progress, ct).ConfigureAwait(false);

            // Only record etags for contacts that actually reached the phone, so anything that
            // failed is picked up again by the next sync instead of being considered current.
            state.Etags = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in listing.Etags)
                if (stored.Contains(kv.Key)) state.Etags[kv.Key] = kv.Value;

            // A full download is also a reset: anything left on the phone that Google no longer
            // has must go, or contacts deleted while this app was not syncing would live on for
            // ever. The W10M version empties its whole contact list first; removing only the
            // strays keeps contact identities, and their People hub links, intact.
            Report(progress, "Checking for contacts Google no longer has...");
            outcome.Removed += await RemoveStraysAsync(store, onGoogle).ConfigureAwait(false);
        }

        static async Task IncrementalDownloadAsync(
            CardDavClient client, ContactStore store, SyncState state,
            SyncOutcome outcome, IProgress<string> progress, CancellationToken ct)
        {
            Report(progress, "Checking Google for changes...");
            var diff = await client.GetChangesAsync(state.Etags, ct).ConfigureAwait(false);

            if (diff.Changed.Count == 0 && diff.Deleted.Count == 0)
            {
                state.Etags = diff.ServerEtags;
                outcome.NothingToDo = true;
                return;
            }

            Report(progress, diff.Changed.Count + " changed, " + diff.Deleted.Count + " removed.");

            var previousEtags = state.Etags;
            var changedHrefs = new HashSet<string>(diff.Changed, StringComparer.Ordinal);
            var stored = new HashSet<string>(StringComparer.Ordinal);

            if (diff.Changed.Count > 0)
                await FetchAndStoreAsync(client, store, state, diff.Changed, diff.ServerEtags,
                                         outcome, stored, null, progress, ct).ConfigureAwait(false);

            foreach (var href in diff.Deleted)
            {
                ct.ThrowIfCancellationRequested();

                string uid = UidForHref(state, href);
                if (await PhoneContacts.DeleteByUidAsync(store, uid).ConfigureAwait(false))
                    outcome.Removed++;

                await CardCache.DeleteAsync(uid).ConfigureAwait(false);
                state.Forget(uid, href);
            }

            // Take the server's etags, but keep the previous one for any contact that did not make
            // it onto the phone - otherwise a contact that failed once would look current for ever
            // and never be retried.
            var updated = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in diff.ServerEtags)
            {
                bool isChanged = changedHrefs.Contains(kv.Key);
                if (!isChanged || stored.Contains(kv.Key))
                {
                    updated[kv.Key] = kv.Value;
                    continue;
                }

                string previous;
                if (previousEtags.TryGetValue(kv.Key, out previous)) updated[kv.Key] = previous;
                // A brand-new contact that failed is simply left out, so the next sync sees it
                // as new again.
            }
            state.Etags = updated;
        }

        /// <summary>
        /// Downloads the given paths a batch at a time, writing each batch to the phone before
        /// asking for the next. Fills in <paramref name="stored"/> with the paths that reached the
        /// phone, and <paramref name="onGoogle"/> - when the caller keeps one - with the UID of
        /// every card that arrived.
        /// </summary>
        static async Task FetchAndStoreAsync(
            CardDavClient client, ContactStore store, SyncState state,
            List<string> hrefs, Dictionary<string, string> serverEtags,
            SyncOutcome outcome, HashSet<string> stored, HashSet<string> onGoogle,
            IProgress<string> progress, CancellationToken ct)
        {
            int total = hrefs.Count;
            outcome.Listed += total;

            for (int offset = 0; offset < total; offset += CardDavClient.MultigetBatch)
            {
                ct.ThrowIfCancellationRequested();

                int count = Math.Min(CardDavClient.MultigetBatch, total - offset);
                var batch = hrefs.GetRange(offset, count);

                var cards = await client.FetchBatchAsync(batch, serverEtags, ct)
                    .ConfigureAwait(false);
                outcome.Attempted += count;
                outcome.Received  += cards.Count;

                foreach (var card in cards)
                {
                    ct.ThrowIfCancellationRequested();
                    if (onGoogle != null) onGoogle.Add(UidOf(card));

                    if (await StoreCardAsync(store, state, card, outcome).ConfigureAwait(false))
                    {
                        outcome.Written++;
                        stored.Add(card.Href);
                    }
                }

                // Contacts written, not paths attempted. The old message counted its way to
                // "Downloaded 1000 of 1000" whether or not a single card had arrived.
                Report(progress, "Saved " + outcome.Written + " of " + total + "...");

                // Nothing has been written and nothing is going right, so whatever is wrong is
                // wrong for the whole address book rather than for one awkward contact. Grinding
                // through another nine hundred would cost the user ten minutes and tell them
                // exactly what the first fifty already have. Both counts matter: cards that never
                // arrive fail here without ever reaching a write.
                int attempted = offset + count;
                bool hopeless = outcome.Failed >= GiveUpAfter
                             || (outcome.Received == 0 && attempted >= GiveUpAfter);

                if (outcome.Written == 0 && hopeless && attempted < total)
                {
                    outcome.GaveUp = true;
                    return;
                }
            }
        }

        /// <summary>
        /// Writes one downloaded card to the phone and records everything the next sync in either
        /// direction will need: the card itself, its etag and path, what would not fit, and the
        /// fingerprint of the result as the phone actually stored it.
        ///
        /// False means the contact did not reach the phone, and why is recorded on the outcome:
        /// a sync that quietly wrote nothing is the one failure the user cannot act on.
        /// </summary>
        static async Task<bool> StoreCardAsync(
            ContactStore store, SyncState state, FetchedCard card, SyncOutcome outcome)
        {
            var contact = card.Contact;
            if (contact == null) return Failed(outcome, "the card could not be read");

            // Google always sends a UID, but a card without one cannot be tracked at all: there
            // would be no way to match it again, and every sync would create a duplicate.
            if (string.IsNullOrEmpty(contact.Uid))
                contact.Uid = SyncState.UidFromHref(card.Href);
            if (string.IsNullOrEmpty(contact.Uid))
                return Failed(outcome, "the card had no UID and its path gave none");

            WriteResult written;
            try
            {
                written = await PhoneContacts.WriteAsync(store, contact).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One unwriteable contact must not abandon the rest of the address book - but it
                // must not vanish without trace either.
                return Failed(outcome, Describe(ex));
            }

            // Etags are the callers' business: they alone know whether this contact's etag should
            // be treated as current (see the comments in the two download paths).
            state.Hrefs[contact.Uid]  = card.Href;
            state.SetUnplaced(contact.Uid, written.UnplacedLines);

            await CardCache.WriteAsync(contact.Uid, card.Card).ConfigureAwait(false);

            var readBack = await PhoneContacts.ReadByUidAsync(store, contact.Uid).ConfigureAwait(false);
            if (readBack != null)
                state.Hashes[contact.Uid] = ContactHash.Compute(readBack);
            else
                state.Hashes.Remove(contact.Uid);

            return true;
        }

        static async Task<int> RemoveStraysAsync(ContactStore store, HashSet<string> keepUids)
        {
            int removed = 0;
            var onPhone = await PhoneContacts.ReadAllAsync(store, null).ConfigureAwait(false);

            foreach (var contact in onPhone)
            {
                // A contact with no RemoteId never came from Google - it is the user's own, and
                // the upload direction is what deals with it.
                if (string.IsNullOrEmpty(contact.Uid)) continue;
                if (keepUids.Contains(contact.Uid)) continue;

                if (await PhoneContacts.DeleteByUidAsync(store, contact.Uid).ConfigureAwait(false))
                    removed++;
                await CardCache.DeleteAsync(contact.Uid).ConfigureAwait(false);
            }
            return removed;
        }

        // ================================================================
        // PHONE -> GOOGLE
        // ================================================================
        public static async Task<SyncOutcome> UploadAsync(
            string email, string password, IProgress<string> progress, CancellationToken ct)
        {
            var outcome = new SyncOutcome();

            if (!AppSettings.TryBeginSync())
            {
                outcome.Error = BusyMessage;
                return outcome;
            }

            CardDavClient client = null;
            try
            {
                Report(progress, "Loading certificates...");
                var trustBundle = await TrustBundle.LoadAsync().ConfigureAwait(false);

                client = new CardDavClient(email, password, trustBundle);
                var state = await SyncState.LoadAsync().ConfigureAwait(false);
                var store = await PhoneContacts.OpenAsync().ConfigureAwait(false);

                Report(progress, "Reading contacts from the phone...");
                var onPhone = await PhoneContacts.ReadAllAsync(store, progress).ConfigureAwait(false);

                if (onPhone.Count == 0)
                {
                    outcome.Success = true;
                    outcome.NothingToDo = true;
                    outcome.Message = "No contacts on the phone yet. Download from Google first.";
                    return outcome;
                }

                var changed = new List<VCardContact>();
                foreach (var contact in onPhone)
                {
                    // A contact that reads back empty is a symptom of a failed read, not an
                    // edit. Uploading it would wipe the Google copy, so it is left alone.
                    if (PhoneContacts.LooksUnreadable(contact)) { outcome.Skipped++; continue; }

                    string known;
                    if (!string.IsNullOrEmpty(contact.Uid) &&
                        state.Hashes.TryGetValue(contact.Uid, out known) &&
                        known == ContactHash.Compute(contact))
                        continue;   // untouched since the last download

                    changed.Add(contact);
                }

                if (changed.Count == 0)
                {
                    outcome.Success = true;
                    outcome.NothingToDo = true;
                    outcome.Message = "No changes on the phone to upload.";
                    AppSettings.LastUpload = Timestamp();
                    return outcome;
                }

                Report(progress, "Uploading " + changed.Count + " changed contact(s)...");

                int done = 0;
                foreach (var contact in changed)
                {
                    ct.ThrowIfCancellationRequested();
                    await UploadOneAsync(client, store, state, contact, outcome, ct)
                        .ConfigureAwait(false);

                    done++;
                    if (done % 5 == 0)
                        Report(progress, "Uploaded " + done + " of " + changed.Count + "...");
                }

                await state.SaveAsync().ConfigureAwait(false);

                outcome.Success = true;
                AppSettings.LastUpload = Timestamp();

                outcome.Message = outcome.Uploaded + " of " + changed.Count +
                                  " changed contact(s) uploaded to Google.";
                if (outcome.Conflicts > 0)
                    outcome.Message += "\n" + outcome.Conflicts +
                        " changed on Google too - sync from Google, then upload again.";
                if (outcome.Failed > 0)
                    outcome.Message += "\n" + outcome.Failed + " failed.";

                return outcome;
            }
            catch (OperationCanceledException)
            {
                outcome.Error = "Upload cancelled.";
                return outcome;
            }
            catch (CardDavException ex)
            {
                outcome.Error = ex.Message;
                return outcome;
            }
            catch (Exception ex)
            {
                outcome.Error = ex.Message;
                return outcome;
            }
            finally
            {
                if (client != null) client.Dispose();
                AppSettings.EndSync();
            }
        }

        static async Task UploadOneAsync(
            CardDavClient client, ContactStore store, SyncState state,
            VCardContact contact, SyncOutcome outcome, CancellationToken ct)
        {
            bool isNew = string.IsNullOrEmpty(contact.Uid);
            string uid = isNew ? Guid.NewGuid().ToString() : contact.Uid;

            string storedCard = await CardCache.ReadAsync(uid).ConfigureAwait(false);
            string cardText = storedCard != null
                ? VCardWriter.Patch(storedCard, contact, state.GetUnplaced(uid))
                : VCardWriter.BuildNew(contact, uid);

            string href;
            if (!state.Hrefs.TryGetValue(uid, out href)) href = null;

            string knownEtag = null;
            if (href != null) state.Etags.TryGetValue(href, out knownEtag);

            var result = await client.UploadAsync(uid, href, knownEtag, cardText, ct)
                .ConfigureAwait(false);

            switch (result.Outcome)
            {
                case UploadOutcome.Created:
                case UploadOutcome.Updated:
                    outcome.Uploaded++;
                    state.Hrefs[uid] = result.Href;
                    state.Hashes[uid] = ContactHash.Compute(contact);
                    await CardCache.WriteAsync(uid, cardText).ConfigureAwait(false);

                    // The etag deliberately is NOT recorded from the PUT response. Google
                    // rewrites parts of a card it accepts (REV, and its own X- properties), so
                    // letting the next download see a mismatch and re-fetch is what keeps the
                    // stored card the same as the server's - which is what patching depends on.
                    if (href != null) state.Etags.Remove(href);

                    if (isNew)
                        await PhoneContacts
                            .SetRemoteIdAsync(store, contact.LocalId, uid).ConfigureAwait(false);
                    break;

                case UploadOutcome.Conflict:
                    // Left entirely alone: the stored etag still describes the card this patch was
                    // built from, so the next download fetches the server's version and the user
                    // can redo the edit on top of it.
                    outcome.Conflicts++;
                    break;

                default:
                    outcome.Failed++;
                    break;
            }
        }

        // ================================================================
        // Helpers
        // ================================================================
        /// <summary>Counts one contact that did not reach the phone, keeping the first reason.</summary>
        static bool Failed(SyncOutcome outcome, string reason)
        {
            outcome.Failed++;
            if (outcome.FirstFailure.Length == 0 && !string.IsNullOrEmpty(reason))
                outcome.FirstFailure = reason;
            return false;
        }

        /// <summary>
        /// An exception as a line the user can repeat to someone. The type name is kept because
        /// the message alone is often the useless half - a bare "Element not found." says nothing,
        /// while "COMException: Element not found." at least says where to look.
        /// </summary>
        static string Describe(Exception ex)
        {
            string message = (ex.Message ?? string.Empty).Trim();
            if (message.Length > 200) message = message.Substring(0, 200) + "...";

            string name = ex.GetType().Name;
            return message.Length == 0 ? name : name + ": " + message;
        }

        static string UidOf(FetchedCard card)
        {
            if (card.Contact != null && !string.IsNullOrEmpty(card.Contact.Uid))
                return card.Contact.Uid;
            return SyncState.UidFromHref(card.Href);
        }

        /// <summary>
        /// The UID a deleted path belonged to. The recorded map is authoritative, because a UID
        /// need not match the file name Google chose; the path is only a fallback.
        /// </summary>
        static string UidForHref(SyncState state, string href)
        {
            foreach (var kv in state.Hrefs)
                if (kv.Value == href) return kv.Key;
            return SyncState.UidFromHref(href);
        }

        static string DescribeDownload(SyncOutcome outcome)
        {
            if (outcome.NothingToDo)
                return "Already up to date - nothing changed on Google.";

            // Nothing reached the phone. This is the case the old message described as
            // "0 contacts downloaded from Google", which reads like an empty address book and
            // hides every other possibility, so it says which one it was instead.
            if (outcome.Written == 0 && outcome.Removed == 0)
                return "No contacts were written to the phone.\n\n" + Diagnosis(outcome);

            var parts = new List<string>();
            if (outcome.WasFullSync)
            {
                parts.Add(Contacts(outcome.Written) + " downloaded");
            }
            else
            {
                if (outcome.Written > 0) parts.Add(Contacts(outcome.Written) + " updated");
                if (outcome.Removed > 0) parts.Add(Contacts(outcome.Removed) + " removed");
            }

            string message = string.Join(", ", parts.ToArray()) + ".";
            string trouble = Trouble(outcome);
            return trouble.Length == 0 ? message : message + "\n" + trouble;
        }

        /// <summary>What went wrong alongside a download that did write something, or nothing.</summary>
        static string Trouble(SyncOutcome outcome)
        {
            var parts = new List<string>();

            int missing = outcome.Attempted - outcome.Received;
            if (missing > 0)
                parts.Add(Contacts(missing) + " were asked for but did not arrive from Google.");

            if (outcome.Failed > 0)
            {
                string line = Contacts(outcome.Failed) + " could not be written to the phone";
                if (outcome.FirstFailure.Length > 0) line += ": " + outcome.FirstFailure;
                parts.Add(line + ".");
            }

            if (outcome.GaveUp)
                parts.Add("Stopped there, since every contact was failing the same way. " +
                          "The next sync starts again from the beginning.");

            return string.Join("\n", parts.ToArray());
        }

        /// <summary>
        /// Why a download wrote nothing. Every branch names a different thing to check, which is
        /// the whole point: the three causes look identical from the outside.
        /// </summary>
        static string Diagnosis(SyncOutcome outcome)
        {
            string trouble = Trouble(outcome);
            if (trouble.Length > 0)
            {
                string head = "Google listed " + Contacts(outcome.Listed);
                if (outcome.Attempted < outcome.Listed)
                    head += ", of which " + outcome.Attempted + " were asked for";
                return head + ".\n" + trouble;
            }

            if (outcome.Listed == 0)
                return outcome.WasFullSync
                    ? "Google listed no contacts for this account."
                    : "Google reported only removals, and none of those contacts was on the phone.";

            return "Google listed " + Contacts(outcome.Listed) + " and all of them arrived, " +
                   "but none was written. This is a bug - please report it.";
        }

        static string Contacts(int count)
        {
            return count == 1 ? "1 contact" : count + " contacts";
        }

        static void Report(IProgress<string> progress, string message)
        {
            if (progress != null) progress.Report(message);
        }

        static string Timestamp()
        {
            return DateTime.Now.ToString("dd MMM yyyy HH:mm");
        }
    }
}
