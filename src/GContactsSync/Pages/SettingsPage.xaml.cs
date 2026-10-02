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
// Whether the periodic download runs and how often, plus a way to make the next sync a full one.
using System;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using GContactsSync.Core.Storage;
using GContactsSync.Core.Sync;

namespace GContactsSync.Pages
{
    public sealed partial class SettingsPage : Page
    {
        // Set while the controls are being filled in from settings, so their change handlers do
        // not write the value straight back and re-register the background task on every visit.
        bool _loading;

        public SettingsPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _loading = true;
            AutoSyncSwitch.IsOn = AppSettings.AutoSyncEnabled;
            SelectInterval(AppSettings.AutoSyncIntervalMinutes);
            _loading = false;

            AboutText.Text =
                "GContactsSync " + App.BuildVersion + "\n" +
                "Google Contacts over CardDAV, with a Google App Password. No Google Cloud " +
                "project and no OAuth client needed.\n" +
                "TLS is handled by a bundled OpenSSL build, because Windows Phone 8.1's own " +
                "certificate store predates the roots www.google.com uses today.";

            await ShowStateAsync();
        }

        async void OnAutoSyncToggled(object sender, RoutedEventArgs e)
        {
            if (_loading) return;

            AppSettings.AutoSyncEnabled = AutoSyncSwitch.IsOn;
            await ApplyAsync();
        }

        async void OnIntervalChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;

            var item = IntervalBox.SelectedItem as ComboBoxItem;
            if (item == null) return;

            uint minutes;
            if (!uint.TryParse(item.Tag as string, out minutes)) return;

            AppSettings.AutoSyncIntervalMinutes = minutes;
            await ApplyAsync();
        }

        async void OnResetClick(object sender, RoutedEventArgs e)
        {
            var dialog = new MessageDialog(
                "Forget the etags, the stored cards and the change fingerprints? The next sync " +
                "downloads everything from Google again. Contacts on the phone are kept, and the " +
                "account stays signed in.",
                "forget sync state");
            dialog.Commands.Add(new UICommand("forget"));
            dialog.Commands.Add(new UICommand("cancel"));
            dialog.DefaultCommandIndex = 1;
            dialog.CancelCommandIndex = 1;

            var chosen = await dialog.ShowAsync();
            if (chosen == null || chosen.Label != "forget") return;

            await SyncState.ClearAsync();
            AppSettings.LastDownload = null;
            AppSettings.LastUpload = null;
            await ShowStateAsync();
        }

        async System.Threading.Tasks.Task ApplyAsync()
        {
            string problem = await BackgroundSync.ApplyAsync();
            await ShowStateAsync();

            if (problem != null)
                await new MessageDialog(problem, "automatic sync").ShowAsync();
        }

        async System.Threading.Tasks.Task ShowStateAsync()
        {
            var state = await SyncState.LoadAsync();

            StateText.Text =
                (state.HasSynced
                    ? state.Etags.Count + " contacts tracked from Google"
                    : "nothing synced yet - the first sync will be a full download") + "\n" +
                "last sync from Google: " + (AppSettings.LastDownload ?? "never") + "\n" +
                "last upload: " + (AppSettings.LastUpload ?? "never") + "\n" +
                "automatic sync scheduled: " + (BackgroundSync.IsRegistered ? "yes" : "no");
        }

        void SelectInterval(uint minutes)
        {
            foreach (var entry in IntervalBox.Items)
            {
                var item = entry as ComboBoxItem;
                if (item == null) continue;

                uint value;
                if (uint.TryParse(item.Tag as string, out value) && value == minutes)
                {
                    IntervalBox.SelectedItem = item;
                    return;
                }
            }
            // A stored interval with no matching entry (an older build, say) falls back to the
            // shortest the platform allows.
            if (IntervalBox.Items.Count > 0) IntervalBox.SelectedIndex = 0;
        }
    }
}
