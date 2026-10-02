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
// Google Contacts over CardDAV, with a Google App Password as Basic auth. No OAuth client,
// no Cloud project, no People API - which is the whole point of the app this one is a port of.
//
// Every request goes through TlsHttpHandler (OpenSSL), because WP8.1's own TLS cannot validate
// www.google.com any more. That handler opens one connection per request, so the number of
// requests matters far more here than it does on Windows 10 Mobile: fetching 500 contacts one
// GET at a time would mean 500 TLS handshakes. Hence FetchBatchAsync batches with an
// addressbook-multiget REPORT (RFC 6352 section 8.7) and only falls back to per-contact GETs
// if the server refuses the REPORT.
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using GContactsSync.Core.Models;
using GContactsSync.Core.Net;

namespace GContactsSync.Core.Services
{
    /// <summary>What a server-side listing found: one etag per contact path.</summary>
    public sealed class ServerListing
    {
        public Dictionary<string, string> Etags = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>Server state compared against what the last sync recorded.</summary>
    public sealed class ServerDiff
    {
        public List<string> Changed = new List<string>();
        public List<string> Deleted = new List<string>();
        public Dictionary<string, string> ServerEtags =
            new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>One downloaded contact, with the card text kept for later patching.</summary>
    public sealed class FetchedCard
    {
        public string Href = string.Empty;
        public string Etag = string.Empty;
        public string Card = string.Empty;
        public VCardContact Contact;
    }

    public enum UploadOutcome
    {
        Created,
        Updated,
        /// <summary>The server's copy moved on; the caller must download before uploading again.</summary>
        Conflict,
        Failed
    }

    public sealed class UploadResult
    {
        public UploadOutcome Outcome;
        public string Uid   = string.Empty;
        public string Href  = string.Empty;
        public string Etag  = string.Empty;
        public string Error = string.Empty;
    }

    public sealed class CardDavClient : IDisposable
    {
        const string Origin      = "https://www.google.com";
        const string CollectionPath = "/carddav/v1/principals/{0}/lists/default/";

        // How many contacts to ask for in one multiget. Google accepts far more, but the whole
        // response is buffered as a string, and cards with photos run to tens of kilobytes each.
        public const int MultigetBatch = 25;

        static readonly XNamespace Dav     = "DAV:";
        static readonly XNamespace CardDav = "urn:ietf:params:xml:ns:carddav";

        readonly HttpClient _http;
        readonly string _collectionPath;   // path only, no origin
        readonly string _collectionUrl;

        bool _multigetWorks = true;

        public CardDavClient(string gmailAddress, string appPassword, byte[] trustBundlePem)
        {
            _collectionPath = string.Format(CollectionPath, Uri.EscapeDataString(gmailAddress));
            _collectionUrl  = Origin + _collectionPath;

            var handler = new TlsHttpHandler(trustBundlePem);
            handler.TimeoutMs = 60000;

            _http = new HttpClient(handler);
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(gmailAddress + ":" + appPassword)));
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("GContactsSync/1.0");
            _http.Timeout = TimeSpan.FromMinutes(5);
        }

        /// <summary>Path of a contact in the collection, for a UID we are creating.</summary>
        public string HrefForNewUid(string uid)
        {
            return _collectionPath + Uri.EscapeDataString(uid) + ".vcf";
        }

        // ================================================================
        // LIST - every contact path and its etag
        // ================================================================
        public async Task<ServerListing> ListAsync(CancellationToken ct)
        {
            const string body =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<d:propfind xmlns:d=\"DAV:\">" +
                  "<d:prop><d:getetag/><d:resourcetype/></d:prop>" +
                "</d:propfind>";

            var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), new Uri(_collectionUrl));
            request.Content = new StringContent(body, Encoding.UTF8, "application/xml");
            request.Headers.Add("Depth", "1");

            var response = await SendAsync(request, ct).ConfigureAwait(false);
            EnsureSuccess(response, "listing contacts");

            string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            var listing = new ServerListing();
            foreach (var entry in ReadMultistatus(xml))
            {
                if (entry.Href.Length == 0 || entry.Href.EndsWith("/", StringComparison.Ordinal)) continue;
                if (entry.Etag.Length == 0) continue;
                listing.Etags[entry.Href] = entry.Etag;
            }
            return listing;
        }

        /// <summary>
        /// Server listing against the etags the last sync stored: what to fetch and what to
        /// remove from the phone.
        /// </summary>
        public async Task<ServerDiff> GetChangesAsync(
            Dictionary<string, string> knownEtags, CancellationToken ct)
        {
            var listing = await ListAsync(ct).ConfigureAwait(false);

            var diff = new ServerDiff();
            diff.ServerEtags = listing.Etags;

            foreach (var kv in listing.Etags)
            {
                string known;
                if (!knownEtags.TryGetValue(kv.Key, out known) || known != kv.Value)
                    diff.Changed.Add(kv.Key);
            }
            foreach (var href in knownEtags.Keys)
                if (!listing.Etags.ContainsKey(href)) diff.Deleted.Add(href);

            return diff;
        }

        // ================================================================
        // FETCH - the cards for a set of paths
        // ================================================================
        /// <summary>
        /// The cards for one batch of at most <see cref="MultigetBatch"/> paths.
        ///
        /// One batch at a time rather than the whole address book, so the caller can write each
        /// batch to the phone as it arrives. A thousand contacts fetched first and written second
        /// means a thousand cards held in memory at once, and means nothing at all reaches the
        /// phone if the run is interrupted part way through.
        /// </summary>
        public async Task<List<FetchedCard>> FetchBatchAsync(
            List<string> hrefs, Dictionary<string, string> serverEtags, CancellationToken ct)
        {
            var result = new List<FetchedCard>();
            if (hrefs == null || hrefs.Count == 0) return result;

            ct.ThrowIfCancellationRequested();

            List<FetchedCard> fetched = null;
            if (_multigetWorks)
            {
                fetched = await MultigetAsync(hrefs, ct).ConfigureAwait(false);
                // An empty answer for a non-empty batch means the REPORT was accepted but
                // useless; stop paying for it and use plain GETs from here on.
                if (fetched == null || fetched.Count == 0)
                {
                    _multigetWorks = false;
                    fetched = null;
                }
            }
            if (fetched == null)
                fetched = await GetEachAsync(hrefs, ct).ConfigureAwait(false);

            foreach (var card in fetched)
            {
                if (card.Etag.Length == 0 && serverEtags != null)
                {
                    string etag;
                    if (serverEtags.TryGetValue(card.Href, out etag)) card.Etag = etag;
                }
                result.Add(card);
            }

            return result;
        }

        async Task<List<FetchedCard>> MultigetAsync(List<string> hrefs, CancellationToken ct)
        {
            var body = new StringBuilder();
            body.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            body.Append("<C:addressbook-multiget xmlns:D=\"DAV:\" ")
                .Append("xmlns:C=\"urn:ietf:params:xml:ns:carddav\">");
            body.Append("<D:prop><D:getetag/><C:address-data/></D:prop>");
            foreach (var href in hrefs)
                body.Append("<D:href>").Append(XmlEscape(href)).Append("</D:href>");
            body.Append("</C:addressbook-multiget>");

            var request = new HttpRequestMessage(new HttpMethod("REPORT"), new Uri(_collectionUrl));
            request.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/xml");
            request.Headers.Add("Depth", "0");   // RFC 6352 section 8.7

            HttpResponseMessage response;
            try
            {
                response = await SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                return null;   // fall back to GETs
            }

            if (!response.IsSuccessStatusCode) return null;

            string xml = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            var cards = new List<FetchedCard>();
            foreach (var entry in ReadMultistatus(xml))
            {
                if (entry.Card.Length == 0) continue;

                var contact = VCardParser.ParseFirst(entry.Card);
                if (contact == null) continue;

                string card = FirstCardText(entry.Card);
                contact.Href = entry.Href;
                contact.Etag = entry.Etag;

                cards.Add(new FetchedCard
                {
                    Href = entry.Href,
                    Etag = entry.Etag,
                    Card = card,
                    Contact = contact
                });
            }
            return cards;
        }

        async Task<List<FetchedCard>> GetEachAsync(List<string> hrefs, CancellationToken ct)
        {
            var cards = new List<FetchedCard>();
            foreach (var href in hrefs)
            {
                ct.ThrowIfCancellationRequested();
                var card = await GetAsync(href, ct).ConfigureAwait(false);
                if (card != null) cards.Add(card);
            }
            return cards;
        }

        async Task<FetchedCard> GetAsync(string href, CancellationToken ct)
        {
            try
            {
                var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Origin + href));
                var response = await SendAsync(request, ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;

                string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var contact = VCardParser.ParseFirst(text);
                if (contact == null) return null;

                string etag = response.Headers.ETag != null
                    ? response.Headers.ETag.Tag.Trim('"')
                    : string.Empty;

                contact.Href = href;
                contact.Etag = etag;

                return new FetchedCard
                {
                    Href = href,
                    Etag = etag,
                    Card = FirstCardText(text),
                    Contact = contact
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { return null; }
        }

        // ================================================================
        // UPLOAD - PUT one card
        // ================================================================
        /// <param name="href">
        /// Where the contact lives on the server, or null to create it at a path derived from
        /// the UID.
        /// </param>
        /// <param name="knownEtag">
        /// The etag of the card this upload was built from. Sent as If-Match so a contact that
        /// changed on Google in the meantime comes back as a conflict instead of being
        /// overwritten with a card patched from a stale copy.
        /// </param>
        public async Task<UploadResult> UploadAsync(
            string uid, string href, string knownEtag, string cardText, CancellationToken ct)
        {
            var result = new UploadResult { Uid = uid };
            bool creating = string.IsNullOrEmpty(href);
            string target = creating ? HrefForNewUid(uid) : href;
            result.Href = target;

            try
            {
                var request = new HttpRequestMessage(HttpMethod.Put, new Uri(Origin + target));
                request.Content = new StringContent(cardText, Encoding.UTF8, "text/vcard");
                request.Content.Headers.ContentType =
                    new MediaTypeHeaderValue("text/vcard") { CharSet = "utf-8" };

                if (creating)
                    request.Headers.TryAddWithoutValidation("If-None-Match", "*");
                else if (!string.IsNullOrEmpty(knownEtag))
                    request.Headers.TryAddWithoutValidation("If-Match", "\"" + knownEtag + "\"");

                var response = await SendAsync(request, ct).ConfigureAwait(false);
                int code = (int)response.StatusCode;

                if (code == 412 || code == 409)
                {
                    result.Outcome = UploadOutcome.Conflict;
                    result.Error = "changed on Google since the last download";
                    return result;
                }

                if (!response.IsSuccessStatusCode)
                {
                    result.Outcome = UploadOutcome.Failed;
                    result.Error = code + " " + response.ReasonPhrase;
                    return result;
                }

                if (response.Headers.ETag != null)
                    result.Etag = response.Headers.ETag.Tag.Trim('"');

                result.Outcome = creating ? UploadOutcome.Created : UploadOutcome.Updated;
                return result;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result.Outcome = UploadOutcome.Failed;
                result.Error = ex.Message;
                return result;
            }
        }

        // ================================================================
        // Plumbing
        // ================================================================
        async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            try
            {
                return await _http.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                throw new CardDavException("Network error: " + ex.Message, ex);
            }
        }

        static void EnsureSuccess(HttpResponseMessage response, string what)
        {
            // A PROPFIND or REPORT answers 207 Multi-Status, which is already a 2xx.
            if (response.IsSuccessStatusCode) return;

            int code = (int)response.StatusCode;
            if (code == 401)
                throw new CardDavException(
                    "Google rejected the sign-in (401).\n\n" +
                    "Use a 16-character Google App Password, not your account password. " +
                    "Create one at myaccount.google.com/apppasswords - it needs 2-Step " +
                    "Verification to be switched on.");
            if (code == 403)
                throw new CardDavException(
                    "Google refused access (403).\n\n" +
                    "Check that the account allows CardDAV and that the App Password has not " +
                    "been revoked.");
            if (code == 404)
                throw new CardDavException(
                    "The address book was not found (404).\n\n" +
                    "Check the Gmail address is spelt correctly.");

            throw new CardDavException("Google returned " + code + " " + response.ReasonPhrase +
                                       " while " + what + ".");
        }

        sealed class MultistatusEntry
        {
            public string Href = string.Empty;
            public string Etag = string.Empty;
            public string Card = string.Empty;
        }

        /// <summary>
        /// The href / getetag / address-data of every response in a DAV multistatus body.
        /// A real XML parser rather than string scanning: multiget responses carry whole vCards
        /// as escaped element text, and getting the entity decoding wrong corrupts contacts.
        /// </summary>
        static IEnumerable<MultistatusEntry> ReadMultistatus(string xml)
        {
            var entries = new List<MultistatusEntry>();
            if (string.IsNullOrEmpty(xml)) return entries;

            XDocument document;
            try
            {
                document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            }
            catch (Exception ex)
            {
                throw new CardDavException(
                    "Google's reply could not be understood (" + ex.Message + ").", ex);
            }

            if (document.Root == null) return entries;

            foreach (var response in document.Root.Elements(Dav + "response"))
            {
                var entry = new MultistatusEntry();

                var href = response.Element(Dav + "href");
                if (href != null) entry.Href = href.Value.Trim();

                foreach (var propstat in response.Elements(Dav + "propstat"))
                {
                    // A propstat can report a property as not found; only read the 200 ones.
                    var status = propstat.Element(Dav + "status");
                    if (status != null && status.Value.IndexOf(" 200 ", StringComparison.Ordinal) < 0)
                        continue;

                    var prop = propstat.Element(Dav + "prop");
                    if (prop == null) continue;

                    var etag = prop.Element(Dav + "getetag");
                    if (etag != null) entry.Etag = etag.Value.Trim().Trim('"');

                    var data = prop.Element(CardDav + "address-data");
                    if (data != null) entry.Card = data.Value;
                }

                entries.Add(entry);
            }
            return entries;
        }

        static string FirstCardText(string body)
        {
            var cards = VCardParser.SplitCards(body);
            return cards.Count > 0 ? cards[0] : body;
        }

        static string XmlEscape(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }

    /// <summary>A failure with a message meant to be shown to the user as it is.</summary>
    public sealed class CardDavException : Exception
    {
        public CardDavException(string message) : base(message) { }
        public CardDavException(string message, Exception inner) : base(message, inner) { }
    }
}
