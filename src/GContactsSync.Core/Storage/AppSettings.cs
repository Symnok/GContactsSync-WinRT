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
// Preferences and sync timestamps. Small scalar values, so LocalSettings is the right place
// for these - unlike the per-contact maps, which live in files (see LocalFiles).
using System;
using Windows.Storage;

namespace GContactsSync.Core.Storage
{
    public static class AppSettings
    {
        /// <summary>
        /// Windows Phone refuses a TimeTrigger under 15 minutes, so this is a floor the UI
        /// cannot go below rather than a preference.
        /// </summary>
        public const uint MinimumIntervalMinutes = 15;

        const string AutoSyncKey     = "AutoSyncEnabled";
        const string IntervalKey     = "AutoSyncIntervalMinutes";
        const string LastDownloadKey = "LastDownload";
        const string LastUploadKey   = "LastUpload";
        const string LastAutoKey     = "LastAutoSync";
        const string LastAutoErrKey  = "LastAutoSyncError";
        const string SyncBusyKey     = "SyncStartedAtUtc";

        static ApplicationDataContainer Values
        {
            get { return ApplicationData.Current.LocalSettings; }
        }

        static string GetString(string key)
        {
            object value = Values.Values[key];
            return value as string;
        }

        static void SetString(string key, string value)
        {
            if (value == null) Values.Values.Remove(key);
            else Values.Values[key] = value;
        }

        /// <summary>Whether the periodic Google-to-phone download is wanted. On by default.</summary>
        public static bool AutoSyncEnabled
        {
            get
            {
                object value = Values.Values[AutoSyncKey];
                return !(value is bool) || (bool)value;
            }
            set { Values.Values[AutoSyncKey] = value; }
        }

        public static uint AutoSyncIntervalMinutes
        {
            get
            {
                // Not a plain cast: LocalSettings keeps whatever type was written to it, so a value
                // left behind by an earlier build would throw on the way out rather than simply
                // being wrong.
                object value = Values.Values[IntervalKey];
                if (!(value is uint)) return MinimumIntervalMinutes;

                uint minutes = (uint)value;
                return minutes < MinimumIntervalMinutes ? MinimumIntervalMinutes : minutes;
            }
            set
            {
                Values.Values[IntervalKey] =
                    value < MinimumIntervalMinutes ? MinimumIntervalMinutes : value;
            }
        }

        public static string LastDownload
        {
            get { return GetString(LastDownloadKey); }
            set { SetString(LastDownloadKey, value); }
        }

        public static string LastUpload
        {
            get { return GetString(LastUploadKey); }
            set { SetString(LastUploadKey, value); }
        }

        /// <summary>What the last background run did, for the status line on the main page.</summary>
        public static string LastAutoSync
        {
            get { return GetString(LastAutoKey); }
            set { SetString(LastAutoKey, value); }
        }

        public static string LastAutoSyncError
        {
            get { return GetString(LastAutoErrKey); }
            set { SetString(LastAutoErrKey, value); }
        }

        // ================================================================
        // Keeping two syncs from running at once
        // ================================================================
        // The background task can fire while the app is in the foreground, and both would write
        // the same state files. A timestamp rather than a flag, so a sync killed mid-run (the
        // background host is given seconds, not minutes) cannot block syncing for ever.
        static readonly TimeSpan BusyExpiry = TimeSpan.FromMinutes(10);

        public static bool TryBeginSync()
        {
            string started = GetString(SyncBusyKey);
            if (started != null)
            {
                DateTimeOffset when;
                if (DateTimeOffset.TryParse(started, out when) &&
                    DateTimeOffset.UtcNow - when < BusyExpiry)
                    return false;
            }
            SetString(SyncBusyKey, DateTimeOffset.UtcNow.ToString("o"));
            return true;
        }

        public static void EndSync()
        {
            SetString(SyncBusyKey, null);
        }
    }
}
