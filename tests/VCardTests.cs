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
// Tests for the platform-neutral half of GContactsSync.Core: the vCard parser, the
// writer/patcher, the phone slot mapping and the change fingerprint.
//
// Run with tests\run-tests.bat. It compiles the real Core sources straight out of
// src\GContactsSync.Core with the desktop C# compiler - none of the files below are copies,
// so these tests cannot drift from what the app ships. That is possible because a
// StoredContact's properties are just an IDictionary<string, object>: everything except opening
// the store, saving a contact and the display picture is ordinary C#. What is left needs a phone,
// and is listed in docs\TESTING.md.
//
// The one thing mirrored rather than used is address placement, because the value a WP8.1
// StoredContact holds for an address is a WinRT object. The slot CHOICE still comes from the real
// ContactSlots; only the two lines that build and read the value object are restated here.
using System;
using System.Collections.Generic;
using System.Text;
using GContactsSync.Core.Models;
using GContactsSync.Core.Services;
using GContactsSync.Core.Storage;

static class Program
{
    static int _failures;

    static void Main()
    {
        ParsesAGoogleCard();
        UnfoldsAndKeepsPhoto();
        PatchKeepsEverythingUnmanaged();
        PatchRewritesManagedProperties();
        PatchDropsDeletedValues();
        PatchRestoresUnplacedLines();
        PatchKeepsOrgDepartment();
        PatchDropsOrphanedLabels();
        BuildNewIsSelfContained();
        HashIsStableAndSensitive();
        RoundTripsTwiceUnchanged();
        HandlesEscaping();
        HandlesVCard21StyleTypes();
        BirthdayForms();
        EverySlotNameIsARealContactProperty();
        SlotsFillInPreferenceOrder();
        FaxAndVoiceSlotsStaySeparate();
        OverflowIsReportedNotDropped();
        SlotsRoundTripTypes();
        NothingIsLostThroughThePhone();
        RepeatedDownloadsProduceTheSameHash();
        DisplayNameFallsBack();
        ClearedKeysCoverEverySlot();
        ClearedKeysLeaveTypedPropertiesAlone();
        ContactWithANameKeepsIt();
        CredentialsFileIsForgiving();
        ScalarsRoundTrip();

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL TESTS PASSED" : _failures + " CHECK(S) FAILED");
        Environment.Exit(_failures == 0 ? 0 : 1);
    }

    // ================================================================
    // A card shaped like the ones Google's CardDAV endpoint returns
    // ================================================================
    const string GoogleCard =
        "BEGIN:VCARD\r\n" +
        "VERSION:3.0\r\n" +
        "UID:3b1f9c2a4d5e6f70\r\n" +
        "FN:Jane Q. Doe\r\n" +
        "N:Doe;Jane;Quinn;Dr.;PhD\r\n" +
        "NICKNAME:Janey\r\n" +
        "ORG:Acme Corporation;Research\r\n" +
        "TITLE:Principal Engineer\r\n" +
        "item1.EMAIL;TYPE=INTERNET,HOME:jane@example.com\r\n" +
        "item1.X-ABLabel:_$!<Home>!$_\r\n" +
        "EMAIL;TYPE=INTERNET,WORK:jane.doe@acme.example\r\n" +
        "TEL;TYPE=CELL,VOICE:+1 (800) 555-0100\r\n" +
        "TEL;TYPE=HOME,VOICE:+18005550111\r\n" +
        "TEL;TYPE=WORK,VOICE:+18005550122\r\n" +
        "TEL;TYPE=HOME,FAX:+18005550133\r\n" +
        "ADR;TYPE=HOME:;;12 Elm Street;Springfield;IL;62704;USA\r\n" +
        "URL;TYPE=WORK:https://acme.example/jane\r\n" +
        "URL:https://jane.example\r\n" +
        "BDAY:1978-04-09\r\n" +
        "NOTE:Met at the 2019 conference\\nLikes strong coffee\r\n" +
        "CATEGORIES:myContacts,Colleagues\r\n" +
        "X-GOOGLE-TALK:jane@talk.example\r\n" +
        "PHOTO;ENCODING=b;TYPE=JPEG:/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJ\r\n" +
        " CQkKDAsJCQsMDQ0MDA0NDQwMDQ0NDQwNDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDQ0NDf/AABEI\r\n" +
        " AAEAAQMBIgACEQEDEQH/xAAfAAABBQEBAQEBAQAAAAAAAAAAAQIDBAUGBwgJCgv/\r\n" +
        "REV:2024-01-15T10:22:33Z\r\n" +
        "END:VCARD\r\n";

    static void ParsesAGoogleCard()
    {
        var c = VCardParser.ParseFirst(GoogleCard);

        Check("uid", "3b1f9c2a4d5e6f70", c.Uid);
        Check("display name", "Jane Q. Doe", c.DisplayName);
        Check("first name", "Jane", c.FirstName);
        Check("last name", "Doe", c.LastName);
        Check("middle name", "Quinn", c.MiddleName);
        Check("prefix", "Dr.", c.NamePrefix);
        Check("suffix", "PhD", c.NameSuffix);
        Check("nickname", "Janey", c.Nickname);
        Check("organisation (department dropped)", "Acme Corporation", c.Organization);
        Check("job title", "Principal Engineer", c.JobTitle);
        Check("birthday", "1978-04-09", c.Birthday);
        Check("notes keep the newline", "Met at the 2019 conference\nLikes strong coffee", c.Notes);

        Check("email count", "2", c.Emails.Count.ToString());
        Check("grouped email survives the itemN prefix", "jane@example.com", c.Emails[0].Address);
        Check("grouped email type", "home", c.Emails[0].Type);
        Check("work email type", "work", c.Emails[1].Type);

        Check("phone count", "4", c.Phones.Count.ToString());
        Check("mobile type", "mobile", c.Phones[0].Type);
        Check("home type", "home", c.Phones[1].Type);
        Check("work type", "work", c.Phones[2].Type);
        Check("home fax type", "homefax", c.Phones[3].Type);

        Check("address count", "1", c.Addresses.Count.ToString());
        Check("street", "12 Elm Street", c.Addresses[0].Street);
        Check("city", "Springfield", c.Addresses[0].City);
        Check("region", "IL", c.Addresses[0].Region);
        Check("postcode", "62704", c.Addresses[0].PostalCode);
        Check("country", "USA", c.Addresses[0].Country);
        Check("address type", "home", c.Addresses[0].Type);

        Check("website count", "2", c.Websites.Count.ToString());
        Check("website type", "work", c.Websites[0].Type);
        Check("untyped website", "other", c.Websites[1].Type);

        // X-ABLabel must never be mistaken for contact data.
        Check("no label leaked into notes", true, c.Notes.IndexOf("_$!") < 0);
    }

    static void UnfoldsAndKeepsPhoto()
    {
        var c = VCardParser.ParseFirst(GoogleCard);

        Check("photo was read", true, c.PhotoBase64.Length > 0);
        Check("photo type", "JPEG", c.PhotoType);
        Check("photo is unfolded into one run", true, c.PhotoBase64.IndexOf(' ') < 0);
        Check("photo starts with the JPEG marker", true, c.PhotoBase64.StartsWith("/9j/4AAQSkZJRg"));
        Check("photo carries all three folded lines",
            true, c.PhotoBase64.EndsWith("BwgJCgv/"));
    }

    // ================================================================
    // Patch: the properties the phone does not own must survive
    // ================================================================
    static void PatchKeepsEverythingUnmanaged()
    {
        var phone = VCardParser.ParseFirst(GoogleCard);
        string patched = VCardWriter.Patch(GoogleCard, phone, null);

        Check("UID kept", true, HasLine(patched, "UID:3b1f9c2a4d5e6f70"));
        Check("CATEGORIES kept", true, HasLine(patched, "CATEGORIES:myContacts,Colleagues"));
        Check("X- property kept", true, HasLine(patched, "X-GOOGLE-TALK:jane@talk.example"));
        Check("VERSION emitted once", 1, CountLines(patched, "VERSION:"));
        Check("REV dropped", 0, CountLines(patched, "REV:"));

        // The photo is the whole reason patching exists: a card rebuilt from what a WP8.1
        // StoredContact holds would have deleted it.
        var reparsed = VCardParser.ParseFirst(patched);
        Check("photo survives a patch", true, reparsed.PhotoBase64.Length > 0);
        Check("photo is byte-identical",
            VCardParser.ParseFirst(GoogleCard).PhotoBase64, reparsed.PhotoBase64);
        Check("patch is a single card", 1, VCardParser.SplitCards(patched).Count);
    }

    static void PatchRewritesManagedProperties()
    {
        var phone = VCardParser.ParseFirst(GoogleCard);
        phone.FirstName = "Janet";
        phone.Notes = "Now prefers tea";
        phone.Phones[0].Number = "+18005559999";

        string patched = VCardWriter.Patch(GoogleCard, phone, null);
        var after = VCardParser.ParseFirst(patched);

        Check("edited first name reaches the card", "Janet", after.FirstName);
        Check("edited note reaches the card", "Now prefers tea", after.Notes);
        Check("edited number reaches the card", "+18005559999", after.Phones[0].Number);
        Check("old note is gone", 0, CountContaining(patched, "NOTE:Met at"));
        Check("one N line", 1, CountLines(patched, "N:"));
        Check("one FN line", 1, CountLines(patched, "FN:"));
        Check("phones are not duplicated", 4, after.Phones.Count);
        Check("types are preserved across the rewrite", "homefax", after.Phones[3].Type);
    }

    static void PatchDropsDeletedValues()
    {
        var phone = VCardParser.ParseFirst(GoogleCard);
        // The user deleted the work number and the second website on the phone.
        phone.Phones.RemoveAt(2);
        phone.Websites.RemoveAt(1);

        string patched = VCardWriter.Patch(GoogleCard, phone, null);
        var after = VCardParser.ParseFirst(patched);

        Check("deleted phone is gone", 3, after.Phones.Count);
        Check("the right phone went", 0, CountContaining(patched, "+18005550122"));
        Check("deleted website is gone", 1, after.Websites.Count);
        Check("the other website stayed", "https://acme.example/jane", after.Websites[0].Url);
    }

    static void PatchRestoresUnplacedLines()
    {
        // What the phone could not hold: two extra numbers and an extra website. PhoneContacts
        // hands these back as lines; without putting them back, the upload would delete them.
        var unplaced = new List<string>
        {
            "TEL;TYPE=WORK,VOICE:+18005557777",
            "TEL;TYPE=VOICE:+18005558888",
            "URL:https://extra.example"
        };

        var phone = VCardParser.ParseFirst(GoogleCard);
        string patched = VCardWriter.Patch(GoogleCard, phone, unplaced);
        var after = VCardParser.ParseFirst(patched);

        Check("unplaced numbers come back", 6, after.Phones.Count);
        Check("first unplaced number present", 1, CountContaining(patched, "+18005557777"));
        Check("second unplaced number present", 1, CountContaining(patched, "+18005558888"));
        Check("unplaced website present", 1, CountContaining(patched, "https://extra.example"));
        Check("nothing is duplicated", 1, CountContaining(patched, "+1 (800) 555-0100"));
    }

    static void PatchKeepsOrgDepartment()
    {
        var phone = VCardParser.ParseFirst(GoogleCard);

        // Unchanged company: the department Google holds has nowhere to live on the phone, so the
        // original ORG line must be left alone rather than rewritten without it.
        string patched = VCardWriter.Patch(GoogleCard, phone, null);
        Check("department kept when the company is unchanged",
            true, HasLine(patched, "ORG:Acme Corporation;Research"));
        Check("exactly one ORG line", 1, CountLines(patched, "ORG:"));

        // Changed company: the user's edit wins, and the department goes with the old value.
        phone.Organization = "Globex";
        string changed = VCardWriter.Patch(GoogleCard, phone, null);
        Check("changed company is written", true, HasLine(changed, "ORG:Globex"));
        Check("stale department removed", 0, CountContaining(changed, "Research"));
        Check("still exactly one ORG line", 1, CountLines(changed, "ORG:"));
    }

    static void PatchDropsOrphanedLabels()
    {
        var phone = VCardParser.ParseFirst(GoogleCard);
        string patched = VCardWriter.Patch(GoogleCard, phone, null);

        // item1.EMAIL is re-emitted as a plain EMAIL, so the label that grouped with it has
        // nothing left to label. Leaving it behind gives Google a dangling label.
        Check("orphaned X-ABLabel removed", 0, CountContaining(patched, "X-ABLabel"));
        Check("its email is still there", 1, CountContaining(patched, "jane@example.com"));
    }

    static void BuildNewIsSelfContained()
    {
        var c = new VCardContact
        {
            FirstName = "Ada",
            LastName = "Lovelace",
            DisplayName = "Ada Lovelace",
            Organization = "Analytical Engines",
            Birthday = "18151210"
        };
        c.Phones.Add(new ContactPhone { Number = "+442079460000", Type = "work" });
        c.Emails.Add(new ContactEmail { Address = "ada@example.org", Type = "home" });

        string card = VCardWriter.BuildNew(c, "new-uid-1");

        Check("begins", true, card.StartsWith("BEGIN:VCARD\r\n"));
        Check("ends", true, card.TrimEnd().EndsWith("END:VCARD"));
        Check("has the uid", true, HasLine(card, "UID:new-uid-1"));
        Check("version 3.0", true, HasLine(card, "VERSION:3.0"));

        var back = VCardParser.ParseFirst(card);
        Check("name round-trips", "Ada", back.FirstName);
        Check("phone round-trips", "+442079460000", back.Phones[0].Number);
        Check("phone type round-trips", "work", back.Phones[0].Type);
        Check("email type round-trips", "home", back.Emails[0].Type);
        Check("compact birthday is normalised", "1815-12-10", back.Birthday);
    }

    static void HashIsStableAndSensitive()
    {
        var a = VCardParser.ParseFirst(GoogleCard);
        var b = VCardParser.ParseFirst(GoogleCard);
        Check("same input, same hash", ContactHash.Compute(a), ContactHash.Compute(b));

        // Reformatting a number is not an edit.
        b.Phones[0].Number = "+18005550100";
        Check("phone formatting is ignored", ContactHash.Compute(a), ContactHash.Compute(b));

        // Case and padding in a name are not edits either.
        b = VCardParser.ParseFirst(GoogleCard);
        b.FirstName = "  JANE  ";
        Check("name case and padding are ignored", ContactHash.Compute(a), ContactHash.Compute(b));

        // A real change must show.
        b = VCardParser.ParseFirst(GoogleCard);
        b.Phones[0].Number = "+18005559999";
        Check("a changed number shows", true, ContactHash.Compute(a) != ContactHash.Compute(b));

        b = VCardParser.ParseFirst(GoogleCard);
        b.Notes = "different";
        Check("a changed note shows", true, ContactHash.Compute(a) != ContactHash.Compute(b));

        // A number moved from one slot to another is a change the user made on purpose.
        b = VCardParser.ParseFirst(GoogleCard);
        b.Phones[0].Type = "work";
        Check("a changed label shows", true, ContactHash.Compute(a) != ContactHash.Compute(b));

        b = VCardParser.ParseFirst(GoogleCard);
        b.Phones.RemoveAt(1);
        Check("a removed number shows", true, ContactHash.Compute(a) != ContactHash.Compute(b));
    }

    static void RoundTripsTwiceUnchanged()
    {
        // Patching a card that was itself the result of a patch must not drift: this is what
        // happens every time the user edits the same contact twice.
        var phone = VCardParser.ParseFirst(GoogleCard);
        string first = VCardWriter.Patch(GoogleCard, phone, null);
        string second = VCardWriter.Patch(first, VCardParser.ParseFirst(first), null);

        Check("a second patch changes nothing", first, second);
    }

    static void HandlesEscaping()
    {
        var c = new VCardContact
        {
            LastName = "O'Brien; Jr.",
            FirstName = "Seán, the \\one\\",
            Notes = "line one\nline two; with a comma, and a backslash \\",
            DisplayName = "Seán O'Brien"
        };
        c.Addresses.Add(new ContactAddress
        {
            Street = "1 Semi; Colon Way",
            City = "Comma, Town",
            Country = "Back\\slashia"
        });

        string card = VCardWriter.BuildNew(c, "esc-1");
        var back = VCardParser.ParseFirst(card);

        Check("semicolon in a name", c.LastName, back.LastName);
        Check("comma and backslash in a name", c.FirstName, back.FirstName);
        Check("newline and punctuation in a note", c.Notes, back.Notes);
        Check("semicolon in a street", c.Addresses[0].Street, back.Addresses[0].Street);
        Check("comma in a city", c.Addresses[0].City, back.Addresses[0].City);
        Check("backslash in a country", c.Addresses[0].Country, back.Addresses[0].Country);
        Check("the address did not split into two", 1, back.Addresses.Count);
    }

    static void HandlesVCard21StyleTypes()
    {
        // Some Google accounts still hand back bare parameters rather than TYPE=.
        const string card =
            "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:x\r\nFN:Old Style\r\nN:Style;Old;;;\r\n" +
            "TEL;WORK;VOICE:+15550001\r\n" +
            "TEL;CELL:+15550002\r\n" +
            "TEL;HOME;FAX:+15550003\r\n" +
            "EMAIL;INTERNET;WORK:old@example.com\r\n" +
            "END:VCARD\r\n";

        var c = VCardParser.ParseFirst(card);
        Check("bare WORK", "work", c.Phones[0].Type);
        Check("bare CELL", "mobile", c.Phones[1].Type);
        Check("bare HOME + FAX", "homefax", c.Phones[2].Type);
        Check("bare INTERNET + WORK email", "work", c.Emails[0].Type);
    }

    static void BirthdayForms()
    {
        var c = new VCardContact { FirstName = "B", LastName = "Day" };

        c.Birthday = "--0409";
        Check("year-less birthday passes through",
            true, HasLine(VCardWriter.BuildNew(c, "b1"), "BDAY:--0409"));

        c.Birthday = "19780409";
        Check("compact birthday is normalised",
            true, HasLine(VCardWriter.BuildNew(c, "b2"), "BDAY:1978-04-09"));

        c.Birthday = "nonsense";
        Check("unusable birthday is dropped", 0, CountLines(VCardWriter.BuildNew(c, "b3"), "BDAY"));
    }

    // ================================================================
    // Slot placement - the WP8.1-specific half
    // ================================================================
    // Everything PhoneContacts.WriteAsync does to a StoredContact's property map, minus the WinRT.
    static Dictionary<string, object> Place(VCardContact c, out WriteResult unplaced)
    {
        var props = new Dictionary<string, object>();
        unplaced = new WriteResult();
        ContactMapping.PlacePhones(props, c, unplaced);
        ContactMapping.PlaceEmails(props, c, unplaced);
        PlaceAddresses(props, c, unplaced);
        ContactMapping.PlaceWebsites(props, c, unplaced);
        ContactMapping.PlaceScalars(props, c);
        return props;
    }

    static VCardContact ReadBack(Dictionary<string, object> props)
    {
        var c = new VCardContact();
        ContactMapping.ReadPhones(c, props);
        ContactMapping.ReadEmails(c, props);
        ReadAddresses(c, props);
        ContactMapping.ReadWebsite(c, props);
        ContactMapping.ReadScalars(c, props);
        return c;
    }

    // Mirrors PhoneContacts' address handling. The slot choice is the real ContactSlots; only the
    // value object differs, because the app puts a WinRT ContactAddress in the map and this cannot.
    static void PlaceAddresses(
        Dictionary<string, object> props, VCardContact c, WriteResult unplaced)
    {
        foreach (var address in c.Addresses)
        {
            if (address.IsEmpty) continue;

            string slot = ContactSlots.ForAddress(props, address.Type);
            if (slot == null)
            {
                ContactMapping.Unplaced(unplaced, VCardWriter.AddressLine(address));
                continue;
            }
            props[slot] = address;
        }
    }

    static void ReadAddresses(VCardContact c, Dictionary<string, object> props)
    {
        foreach (var entry in ContactSlots.AddressReadOrder)
        {
            object value;
            if (!props.TryGetValue(entry.Slot, out value)) continue;

            var stored = value as ContactAddress;
            if (stored == null) continue;

            c.Addresses.Add(new ContactAddress
            {
                Street = stored.Street,
                City = stored.City,
                Region = stored.Region,
                PostalCode = stored.PostalCode,
                Country = stored.Country,
                Type = entry.Type
            });
        }
    }

    static void ScalarsRoundTrip()
    {
        var c = new VCardContact
        {
            FirstName = "Scalar",
            LastName = "Case",
            MiddleName = "Middle",
            Nickname = "Scal",
            Organization = "Acme",
            JobTitle = "Engineer",
            Notes = "a note",
            Birthday = "1990-02-28"
        };

        WriteResult unplaced;
        var back = ReadBack(Place(c, out unplaced));

        Check("organisation round-trips", "Acme", back.Organization);
        Check("job title round-trips", "Engineer", back.JobTitle);
        Check("notes round-trip", "a note", back.Notes);
        Check("nickname round-trips", "Scal", back.Nickname);
        Check("middle name round-trips", "Middle", back.MiddleName);
        Check("birthday round-trips", "1990-02-28", back.Birthday);

        // A birthday with no year has no DateTimeOffset to live in, so it does not reach the phone.
        // The upload patches the stored card rather than rebuilding it, so Google keeps its copy.
        c.Birthday = "--0409";
        back = ReadBack(Place(c, out unplaced));
        Check("year-less birthday does not reach the phone", "", back.Birthday);

        // An impossible date must not take the contact down with it.
        c.Birthday = "19990231";
        back = ReadBack(Place(c, out unplaced));
        Check("impossible date is ignored", "", back.Birthday);
        Check("and the rest of the contact is fine", "Acme", back.Organization);
    }

    static VCardContact WithPhones(params string[] typeThenNumber)
    {
        var c = new VCardContact { FirstName = "Slot", LastName = "Test" };
        for (int i = 0; i < typeThenNumber.Length; i += 2)
            c.Phones.Add(new ContactPhone { Type = typeThenNumber[i], Number = typeThenNumber[i + 1] });
        return c;
    }

    static void SlotsFillInPreferenceOrder()
    {
        var c = WithPhones(
            "mobile", "1", "mobile", "2",
            "home", "3", "home", "4",
            "work", "5", "work", "6", "work", "7");

        WriteResult unplaced;
        var props = Place(c, out unplaced);

        Check("first mobile", "1", props["MobileTelephone"] as string);
        Check("second mobile", "2", props["AlternateMobileTelephone"] as string);
        Check("first home", "3", props["Telephone"] as string);
        Check("second home", "4", props["AlternateTelephone"] as string);
        Check("first work", "5", props["WorkTelephone"] as string);
        Check("second work", "6", props["AlternateWorkTelephone"] as string);
        Check("third work", "7", props["CompanyTelephone"] as string);
        Check("all seven voice slots used, none spare", 0, unplaced.UnplacedLines.Count);

        // There are seven voice slots, not eight: "Telephone" IS the home phone, and there is no
        // separate generic one. An eighth number has nowhere to go and must be reported.
        c.Phones.Add(new ContactPhone { Type = "other", Number = "8" });
        props = Place(c, out unplaced);
        Check("an eighth voice number does not fit", 1, unplaced.UnplacedLines.Count);
    }

    /// <summary>
    /// Every name these tables can hand to the contact store must be a real StoredContact
    /// property. This is the check that was missing: the tables named "HomeTelephone" - which
    /// does not exist, however plausible it looks beside WorkTelephone - and because slots are
    /// strings, it compiled, passed every test, and then failed on the phone with
    /// "ArgumentException: The parameter is incorrect." for every contact that had a home number.
    /// </summary>
    static void EverySlotNameIsARealContactProperty()
    {
        var known = new HashSet<string>(ContactSlots.KnownNames, StringComparer.Ordinal);

        var bad = new List<string>();
        foreach (var name in ContactSlots.AllUsedNames())
            if (!known.Contains(name) && !bad.Contains(name)) bad.Add(name);

        Check("no slot name is invented", "", string.Join(", ", bad.ToArray()));

        // And the guard itself works: a name that is not a property is caught.
        Check("the check has teeth", false, known.Contains("HomeTelephone"));
    }

    static void FaxAndVoiceSlotsStaySeparate()
    {
        // Seven voice numbers fill every voice slot. An eighth must NOT land in a fax slot: it
        // would come back as a fax and be uploaded to Google as one.
        var c = WithPhones(
            "mobile", "1", "mobile", "2", "home", "3", "home", "4",
            "work", "5", "work", "6", "work", "7",
            "mobile", "9");

        WriteResult unplaced;
        var props = Place(c, out unplaced);

        Check("fax slots left empty", false,
            props.ContainsKey("HomeFax") || props.ContainsKey("WorkFax"));
        Check("the eighth voice number is reported", 1, unplaced.UnplacedLines.Count);
        Check("reported as a mobile", true,
            unplaced.UnplacedLines[0].IndexOf("CELL", StringComparison.Ordinal) >= 0);

        // And with the voice slots full, faxes still get theirs.
        c.Phones.Add(new ContactPhone { Type = "homefax", Number = "fx1" });
        c.Phones.Add(new ContactPhone { Type = "workfax", Number = "fx2" });
        props = Place(c, out unplaced);
        Check("home fax placed", "fx1", props["HomeFax"] as string);
        Check("work fax placed", "fx2", props["WorkFax"] as string);

        // A home fax with HomeFax taken falls back to the other fax slot, not to a voice slot.
        var two = WithPhones("homefax", "a", "homefax", "b");
        props = Place(two, out unplaced);
        Check("second home fax uses the other fax slot", "b", props["WorkFax"] as string);
        Check("no voice slot was taken by a fax", false, props.ContainsKey("MobileTelephone"));
    }

    static void OverflowIsReportedNotDropped()
    {
        var c = new VCardContact { FirstName = "Many", LastName = "Fields" };
        for (int i = 1; i <= 5; i++)
            c.Emails.Add(new ContactEmail { Address = "a" + i + "@example.com", Type = "home" });
        for (int i = 1; i <= 4; i++)
            c.Addresses.Add(new ContactAddress { Street = "Street " + i, Type = "home" });
        for (int i = 1; i <= 3; i++)
            c.Websites.Add(new ContactWebsite { Url = "https://site" + i + ".example", Type = "other" });

        WriteResult unplaced;
        var props = Place(c, out unplaced);

        Check("three emails placed", 3, CountKeys(props, "Email", "WorkEmail", "OtherEmail"));
        Check("three addresses placed", 3, CountKeys(props, "Address", "WorkAddress", "OtherAddress"));
        Check("one website placed", 1, CountKeys(props, "Url"));
        // 2 emails + 1 address + 2 websites could not be placed.
        Check("everything over capacity is reported", 5, unplaced.UnplacedLines.Count);
        Check("reported emails", 2, CountStartingWith(unplaced.UnplacedLines, "EMAIL"));
        Check("reported address", 1, CountStartingWith(unplaced.UnplacedLines, "ADR"));
        Check("reported websites", 2, CountStartingWith(unplaced.UnplacedLines, "URL"));
    }

    static void SlotsRoundTripTypes()
    {
        var c = WithPhones(
            "mobile", "m1", "home", "h1", "work", "w1", "other", "o1",
            "homefax", "hf", "workfax", "wf");
        c.Emails.Add(new ContactEmail { Address = "h@example.com", Type = "home" });
        c.Emails.Add(new ContactEmail { Address = "w@example.com", Type = "work" });
        c.Emails.Add(new ContactEmail { Address = "o@example.com", Type = "other" });
        c.Addresses.Add(new ContactAddress { Street = "Home St", Type = "home" });
        c.Addresses.Add(new ContactAddress { Street = "Work Ave", Type = "work" });

        WriteResult unplaced;
        var back = ReadBack(Place(c, out unplaced));

        Check("nothing overflowed", 0, unplaced.UnplacedLines.Count);
        Check("phone count survives", 6, back.Phones.Count);

        var byNumber = new Dictionary<string, string>();
        foreach (var p in back.Phones) byNumber[p.Number] = p.Type;
        Check("mobile round-trips", "mobile", byNumber["m1"]);
        Check("home round-trips", "home", byNumber["h1"]);
        Check("work round-trips", "work", byNumber["w1"]);
        // No slot on the phone carries "other", so an Other number is kept as a second home
        // number. Documented in ContactSlots.PhoneReadOrder and in the README's limitations.
        Check("other becomes home, deliberately", "home", byNumber["o1"]);
        Check("home fax round-trips", "homefax", byNumber["hf"]);
        Check("work fax round-trips", "workfax", byNumber["wf"]);

        var byAddress = new Dictionary<string, string>();
        foreach (var e in back.Emails) byAddress[e.Address] = e.Type;
        Check("home email round-trips", "home", byAddress["h@example.com"]);
        Check("work email round-trips", "work", byAddress["w@example.com"]);
        Check("other email round-trips", "other", byAddress["o@example.com"]);

        Check("address count survives", 2, back.Addresses.Count);
        Check("home address round-trips", "home", back.Addresses[0].Type);
        Check("work address round-trips", "work", back.Addresses[1].Type);
    }

    // The whole point of the design: a Google card that goes onto the phone and back must arrive
    // with every value intact, including the ones the phone had no room for.
    static void NothingIsLostThroughThePhone()
    {
        string crowded =
            "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:crowded\r\nFN:Crowded Contact\r\n" +
            "N:Contact;Crowded;;;\r\n" +
            "TEL;TYPE=CELL,VOICE:+1000000001\r\n" +
            "TEL;TYPE=CELL,VOICE:+1000000002\r\n" +
            "TEL;TYPE=HOME,VOICE:+1000000003\r\n" +
            "TEL;TYPE=HOME,VOICE:+1000000004\r\n" +
            "TEL;TYPE=WORK,VOICE:+1000000005\r\n" +
            "TEL;TYPE=WORK,VOICE:+1000000006\r\n" +
            "TEL;TYPE=WORK,VOICE:+1000000007\r\n" +
            "TEL;TYPE=VOICE:+1000000008\r\n" +
            "TEL;TYPE=CELL,VOICE:+1000000009\r\n" +
            "TEL;TYPE=HOME,FAX:+1000000010\r\n" +
            "EMAIL;TYPE=INTERNET,HOME:one@example.com\r\n" +
            "EMAIL;TYPE=INTERNET,WORK:two@example.com\r\n" +
            "EMAIL;TYPE=INTERNET:three@example.com\r\n" +
            "EMAIL;TYPE=INTERNET,HOME:four@example.com\r\n" +
            "URL;TYPE=WORK:https://one.example\r\n" +
            "URL:https://two.example\r\n" +
            "CATEGORIES:myContacts\r\n" +
            "PHOTO;ENCODING=b;TYPE=JPEG:QUJD\r\n" +
            "END:VCARD\r\n";

        var fromGoogle = VCardParser.ParseFirst(crowded);

        // Download: place on the phone, note what did not fit, read the phone back.
        WriteResult unplaced;
        var props = Place(fromGoogle, out unplaced);
        var onPhone = ReadBack(props);

        Check("the phone could not take everything", true, unplaced.UnplacedLines.Count > 0);

        // Upload: patch the stored card with what the phone holds plus what never reached it.
        string uploaded = VCardWriter.Patch(crowded, onPhone, unplaced.UnplacedLines);
        var backAtGoogle = VCardParser.ParseFirst(uploaded);

        Check("every number came back", fromGoogle.Phones.Count, backAtGoogle.Phones.Count);
        Check("every email came back", fromGoogle.Emails.Count, backAtGoogle.Emails.Count);
        Check("every website came back", fromGoogle.Websites.Count, backAtGoogle.Websites.Count);
        Check("the photo came back", fromGoogle.PhotoBase64, backAtGoogle.PhotoBase64);

        foreach (var phone in fromGoogle.Phones)
            Check("number " + phone.Number + " survived", true, HasPhone(backAtGoogle, phone.Number));
        foreach (var email in fromGoogle.Emails)
            Check("email " + email.Address + " survived", true, HasEmail(backAtGoogle, email.Address));
        foreach (var website in fromGoogle.Websites)
            Check("website " + website.Url + " survived", true, HasWebsite(backAtGoogle, website.Url));

        Check("no number was duplicated", fromGoogle.Phones.Count, CountContaining(uploaded, "TEL;"));
    }

    // If a download produced a different fingerprint each time, every contact would look edited
    // and the next upload would push the whole address book back to Google.
    static void RepeatedDownloadsProduceTheSameHash()
    {
        var first = VCardParser.ParseFirst(GoogleCard);
        var second = VCardParser.ParseFirst(GoogleCard);

        WriteResult ignored;
        string hashOne = ContactHash.Compute(ReadBack(Place(first, out ignored)));
        string hashTwo = ContactHash.Compute(ReadBack(Place(second, out ignored)));

        Check("the same card gives the same fingerprint", hashOne, hashTwo);

        // And an edit on the phone must still be visible after the round trip through the slots.
        var edited = ReadBack(Place(VCardParser.ParseFirst(GoogleCard), out ignored));
        edited.Phones[0].Number = "+15550000000";
        Check("an edit through the slots still shows", true, ContactHash.Compute(edited) != hashOne);
    }

    static void DisplayNameFallsBack()
    {
        Check("FN wins", "Given Name",
            ContactMapping.DisplayNameFor(new VCardContact { DisplayName = "Given Name" }));
        Check("then first and last", "Ann Lee",
            ContactMapping.DisplayNameFor(new VCardContact { FirstName = "Ann", LastName = "Lee" }));
        Check("then the organisation", "Acme",
            ContactMapping.DisplayNameFor(new VCardContact { Organization = "Acme" }));

        var emailOnly = new VCardContact();
        emailOnly.Emails.Add(new ContactEmail { Address = "only@example.com" });
        Check("then an email address", "only@example.com", ContactMapping.DisplayNameFor(emailOnly));

        Check("and never null", "", ContactMapping.DisplayNameFor(new VCardContact()));
    }

    // ClearedKeys is what a rewrite clears out of the property map. A slot missing from it would
    // keep a stale value after the field was deleted on Google.
    static void ClearedKeysCoverEverySlot()
    {
        var cleared = new List<string>(ContactSlots.ClearedKeys);

        foreach (var entry in ContactSlots.PhoneReadOrder)
            Check("ClearedKeys covers " + entry.Slot, true, cleared.Contains(entry.Slot));
        foreach (var entry in ContactSlots.EmailReadOrder)
            Check("ClearedKeys covers " + entry.Slot, true, cleared.Contains(entry.Slot));
        foreach (var entry in ContactSlots.AddressReadOrder)
            Check("ClearedKeys covers " + entry.Slot, true, cleared.Contains(entry.Slot));
        Check("ClearedKeys covers the website slot",
            true, cleared.Contains(ContactSlots.WebsiteSlot));

        // Scalars only reach the map when they have a value, so they need clearing too.
        foreach (var key in new[] { "CompanyName", "JobTitle", "Notes", "Nickname",
                                    "AdditionalName", "Birthdate" })
            Check("ClearedKeys covers " + key, true, cleared.Contains(key));

        Check("nine phone slots: seven voice and two fax", 9, ContactSlots.PhoneReadOrder.Length);
        Check("three email slots", 3, ContactSlots.EmailReadOrder.Length);
        Check("three address slots", 3, ContactSlots.AddressReadOrder.Length);
    }

    // The bug this exists to prevent: StoredContact.GivenName and the property map's "GivenName"
    // are the same storage, so clearing one of these keys clears the name PhoneContacts just set.
    // Listing them alongside the slots made every contact arrive on the phone with no first or last
    // name at all.
    static void ClearedKeysLeaveTypedPropertiesAlone()
    {
        var cleared = new List<string>(ContactSlots.ClearedKeys);

        foreach (var typed in ContactSlots.TypedProperties)
            Check("ClearedKeys must not contain " + typed + " - it is the same storage as " +
                  "StoredContact." + typed, false, cleared.Contains(typed));

        Check("the five name properties are accounted for", 5, ContactSlots.TypedProperties.Length);
    }

    // The card that found the bug above, kept as a regression case.
    static void ContactWithANameKeepsIt()
    {
        const string card =
            "BEGIN:VCARD\r\n" +
            "VERSION:3.0\r\n" +
            "FN:Scammer Invesrment1\r\n" +
            "N:Invesrment1;Scammer;;;\r\n" +
            "TEL;TYPE=CELL:+1 385-236-4084\r\n" +
            "CATEGORIES:myContacts\r\n" +
            "END:VCARD\r\n";

        var c = VCardParser.ParseFirst(card);
        Check("first name parsed", "Scammer", c.FirstName);
        Check("last name parsed", "Invesrment1", c.LastName);
        Check("display name parsed", "Scammer Invesrment1", c.DisplayName);
        Check("the number parsed", "+1 385-236-4084", c.Phones[0].Number);
        Check("as a mobile", "mobile", c.Phones[0].Type);

        // A card with no UID: Google always sends one, but SyncEngine falls back to the path and
        // the name must survive either way.
        Check("no UID is not fatal", "", c.Uid);

        // The names never go through the property map, so clearing the map cannot touch them - which
        // is exactly what was wrong.
        var props = new Dictionary<string, object>();
        foreach (var key in ContactSlots.ClearedKeys) props.Remove(key);
        ContactMapping.PlacePhones(props, c, new WriteResult());
        Check("the number reached a slot", "+1 385-236-4084", props["MobileTelephone"] as string);
        Check("no name key was written to the map", false,
            props.ContainsKey("GivenName") || props.ContainsKey("FamilyName") ||
            props.ContainsKey("DisplayName"));

        // A contact whose only point of contact is an email keeps its name too.
        const string emailOnly =
            "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:e1\r\n" +
            "FN:Ann Lee\r\nN:Lee;Ann;;;\r\n" +
            "EMAIL;TYPE=INTERNET,HOME:ann@example.com\r\nEND:VCARD\r\n";

        var e = VCardParser.ParseFirst(emailOnly);
        Check("email-only contact keeps its first name", "Ann", e.FirstName);
        Check("email-only contact keeps its last name", "Lee", e.LastName);
        Check("email-only display name is the name, not the address",
            "Ann Lee", ContactMapping.DisplayNameFor(e));
    }

    static int CountKeys(Dictionary<string, object> props, params string[] keys)
    {
        int count = 0;
        foreach (var key in keys) if (props.ContainsKey(key)) count++;
        return count;
    }

    static int CountStartingWith(List<string> lines, string prefix)
    {
        int count = 0;
        foreach (var line in lines)
            if (line.StartsWith(prefix, StringComparison.Ordinal)) count++;
        return count;
    }

    static bool HasPhone(VCardContact c, string number)
    {
        foreach (var p in c.Phones)
            if (VCardWriter.NormalisePhone(p.Number) == VCardWriter.NormalisePhone(number)) return true;
        return false;
    }

    static bool HasEmail(VCardContact c, string address)
    {
        foreach (var e in c.Emails)
            if (string.Equals(e.Address, address, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static bool HasWebsite(VCardContact c, string url)
    {
        foreach (var w in c.Websites)
            if (string.Equals(w.Url, url, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    // ================================================================
    // The credentials file
    // ================================================================
    static void CredentialsFileIsForgiving()
    {
        // The format as asked for, spacing included - note the space after "password:" and none
        // after "login:".
        var r = CredentialsFile.Parse(
            "login:someone@gmail.com\r\n" +
            "password: abcdefghijklmnop\r\n");

        Check("login read", "someone@gmail.com", r.Email);
        Check("password read", "abcdefghijklmnop", r.Password);
        Check("complete", true, r.IsComplete);
        Check("no complaint", null, r.Problem);

        // LF only, extra blank lines, comments, odd spacing and mixed case keys.
        r = CredentialsFile.Parse(
            "# my test account\n\n  LOGIN  :  someone@gmail.com  \n" +
            "; and the app password\nPassword=abcdefghijklmnop\n\n");
        Check("survives comments and spacing", "someone@gmail.com", r.Email);
        Check("accepts = as the separator", "abcdefghijklmnop", r.Password);
        Check("still complete", true, r.IsComplete);

        // Google shows an App Password as four groups of four, and pasting it that way is the
        // obvious thing to do.
        r = CredentialsFile.Parse("login: someone@gmail.com\npassword: abcd efgh ijkl mnop\n");
        Check("spaces are stripped from the password", "abcdefghijklmnop", r.Password);
        Check("and it counts as 16", null, r.Problem);

        // An address contains no colon, but splitting on the LAST separator rather than the first
        // would still be wrong the moment anything else did.
        r = CredentialsFile.Parse("email: first.last+tag@gmail.com\npass: abcdefghijklmnop\n");
        Check("aliases work", "first.last+tag@gmail.com", r.Email);
        Check("pass alias works", "abcdefghijklmnop", r.Password);

        // Now the ways it can be wrong, each of which should say so rather than fail at Google.
        r = CredentialsFile.Parse("");
        Check("empty file is reported", "the file is empty", r.Problem);

        r = CredentialsFile.Parse("this is not a config file\n");
        Check("nonsense is reported", "no \"login:\" or \"password:\" line found", r.Problem);

        r = CredentialsFile.Parse("login: someone@gmail.com\n");
        Check("missing password is reported", "no \"password:\" line found", r.Problem);
        Check("but the address is still handed over", "someone@gmail.com", r.Email);

        r = CredentialsFile.Parse("password: abcdefghijklmnop\n");
        Check("missing login is reported", "no \"login:\" line found", r.Problem);

        r = CredentialsFile.Parse("login: someone\npassword: abcdefghijklmnop\n");
        Check("a login that is not an address is reported",
            "the login line does not look like an email address", r.Problem);

        // The mistake this is really here to catch: the account password, not an App Password.
        r = CredentialsFile.Parse("login: someone@gmail.com\npassword: hunter2\n");
        Check("a password of the wrong length is reported",
            "the password is 7 characters; a Google App Password is 16", r.Problem);
        Check("and it is still offered, in case the check is what is wrong",
            "hunter2", r.Password);

        Check("the file name is the one the manifest associates",
            true, CredentialsFile.DefaultFileName.EndsWith(".cfg", StringComparison.Ordinal));
    }

    // ================================================================
    // Assertions
    // ================================================================
    static bool HasLine(string card, string line)
    {
        foreach (var candidate in Unfold(card))
            if (candidate == line) return true;
        return false;
    }

    // Lines whose PROPERTY starts with this - for counting how many N:, ORG:, VERSION: there are.
    static int CountLines(string card, string startsWith)
    {
        int count = 0;
        foreach (var candidate in Unfold(card))
            if (candidate.StartsWith(startsWith, StringComparison.Ordinal)) count++;
        return count;
    }

    // Lines containing this anywhere - for counting occurrences of a value, which sits after the
    // property and its parameters.
    static int CountContaining(string card, string text)
    {
        int count = 0;
        foreach (var candidate in Unfold(card))
            if (candidate.IndexOf(text, StringComparison.Ordinal) >= 0) count++;
        return count;
    }

    // The writer folds at 75 octets, so a search for a value has to unfold first.
    static List<string> Unfold(string card)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var raw in card.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                current.Append(line.Substring(1));
                continue;
            }
            if (current.Length > 0) lines.Add(current.ToString());
            current.Length = 0;
            current.Append(line);
        }
        if (current.Length > 0) lines.Add(current.ToString());
        return lines;
    }

    static void Check(string what, string expected, string actual)
    {
        if (expected == actual) { Pass(what); return; }
        Fail(what, "expected <" + expected + "> but got <" + actual + ">");
    }

    static void Check(string what, int expected, int actual)
    {
        if (expected == actual) { Pass(what); return; }
        Fail(what, "expected " + expected + " but got " + actual);
    }

    static void Check(string what, bool expected, bool actual)
    {
        if (expected == actual) { Pass(what); return; }
        Fail(what, "expected " + expected + " but got " + actual);
    }

    static void Pass(string what)
    {
        Console.WriteLine("  ok    " + what);
    }

    static void Fail(string what, string detail)
    {
        _failures++;
        Console.WriteLine("  FAIL  " + what + ": " + detail);
    }
}
