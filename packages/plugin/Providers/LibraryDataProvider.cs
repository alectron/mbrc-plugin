using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MusicBeePlugin.Providers;
using MusicBeePlugin.Models;
using MusicBeePlugin.Ffi;
using MusicBeePlugin.Utilities;

namespace MusicBeePlugin.Providers
{
    /// <summary>
    ///     Data provider implementation for music library operations.
    ///     Contains all MusicBee-specific API logic including query lifecycle,
    ///     null separator parsing, filter building, and type mappings.
    ///     Uses yield return for streaming results and proper cleanup via try/finally.
    /// </summary>
    public class LibraryDataProvider : ILibraryDataProvider
    {
        private static readonly string[] DoubleSeparator = { "\0\0" };
        private static readonly char[] NullSeparator = { '\0' };
        private static readonly string[] GenreSearchFields = { "Genre" };
        private static readonly string[] ArtistSearchFields = { "ArtistPeople" };
        private static readonly string[] AlbumSearchFields = { "Album" };

        // Tag sets read in one bulk Library_GetFileTags call per track (instead of
        // one MusicBee API round-trip per tag). Index order is consumed below.
        private static readonly Plugin.MetaDataType[] BrowseTrackFields =
        {
            Plugin.MetaDataType.Artist, Plugin.MetaDataType.TrackTitle, Plugin.MetaDataType.Album,
            Plugin.MetaDataType.AlbumArtist, Plugin.MetaDataType.Genre,
            Plugin.MetaDataType.TrackNo, Plugin.MetaDataType.DiscNo,
        };

        // The V6 typed-track tags: the browse fields plus the raw extended metadata
        // tags the core parses (Year/Rating). Duration and DateAdded are file
        // properties (read via GetFileProperty), not metadata tags. Index order is
        // consumed by GetTrackTags.
        private static readonly Plugin.MetaDataType[] TrackTagFields =
        {
            Plugin.MetaDataType.Artist, Plugin.MetaDataType.TrackTitle, Plugin.MetaDataType.Album,
            Plugin.MetaDataType.AlbumArtist, Plugin.MetaDataType.Genre,
            Plugin.MetaDataType.TrackNo, Plugin.MetaDataType.DiscNo,
            Plugin.MetaDataType.Year, Plugin.MetaDataType.Rating,
        };

        private static readonly Plugin.MetaDataType[] AlbumTrackFields =
        {
            Plugin.MetaDataType.Artist, Plugin.MetaDataType.TrackTitle, Plugin.MetaDataType.AlbumArtist,
            Plugin.MetaDataType.TrackNo, Plugin.MetaDataType.DiscNo,
        };

        private static readonly Plugin.MetaDataType[] ArtistAlbumFields =
        {
            Plugin.MetaDataType.AlbumArtist, Plugin.MetaDataType.Album,
        };

        private readonly Plugin.MusicBeeApiInterface _api;

        public LibraryDataProvider(Plugin.MusicBeeApiInterface api)
        {
            _api = api;
        }

        #region Radio Stations

        public List<RadioStation> GetRadioStations(int offset = 0, int limit = 4000)
        {
            var radioStations = Array.Empty<string>();
            var success = _api.Library_QueryFilesEx("domain=Radio", out radioStations);

            List<RadioStation> stations;
            if (success)
                stations = radioStations.Select(s => new RadioStation
                {
                    url = s ?? string.Empty,
                    name = _api.Library_GetFileTag(s, Plugin.MetaDataType.TrackTitle) ?? string.Empty
                }).ToList();
            else
                stations = new List<RadioStation>();

            return stations;
        }

        #endregion

        #region Browse Operations

        public IEnumerable<GenreData> BrowseGenres(int offset = 0, int limit = 4000)
        {
            if (!_api.Library_QueryLookupTable("genre", "count", null))
            {
                _api.Library_QueryLookupTable(null, null, null);
                yield break;
            }

            try
            {
                var rawValue = _api.Library_QueryGetLookupTableValue(null);
                foreach (var entry in rawValue.Split(DoubleSeparator, StringSplitOptions.None))
                {
                    var genreInfo = entry.Split(NullSeparator, StringSplitOptions.None);
                    if (genreInfo.Length >= 2)
                    {
                        yield return new GenreData
                        {
                            genre = genreInfo[0].Cleanup() ?? string.Empty,
                            count = int.Parse(genreInfo[1], CultureInfo.InvariantCulture),
                        };
                    }
                }
            }
            finally
            {
                _api.Library_QueryLookupTable(null, null, null);
            }
        }

        public IEnumerable<ArtistData> BrowseArtists(int offset = 0, int limit = 4000, bool albumArtists = false)
        {
            var artistType = albumArtists ? "albumartist" : "artist";

            if (!_api.Library_QueryLookupTable(artistType, "count", null))
            {
                _api.Library_QueryLookupTable(null, null, null);
                yield break;
            }

            try
            {
                var rawValue = _api.Library_QueryGetLookupTableValue(null);
                foreach (var entry in rawValue.Split(DoubleSeparator, StringSplitOptions.None))
                {
                    var artistInfo = entry.Split(NullSeparator);
                    if (artistInfo.Length >= 2)
                    {
                        yield return new ArtistData
                        {
                            artist = artistInfo[0].Cleanup() ?? string.Empty,
                            count = int.Parse(artistInfo[1], CultureInfo.InvariantCulture),
                        };
                    }
                }
            }
            finally
            {
                _api.Library_QueryLookupTable(null, null, null);
            }
        }

        /// <summary>
        ///     Albums in browse order, deduplicated on (album artist, album),
        ///     first occurrence wins. Every entry's count is 1 - this list is for
        ///     browsing, not for reporting track counts (see GetArtistAlbums for
        ///     those).
        /// </summary>
        public List<AlbumData> BrowseAlbums(int offset = 0, int limit = 4000)
        {
            // A first-wins HashSet keyed on artist\0album reproduces what
            // AlbumData's IEquatable + Distinct did, so ordering and count match.
            // Count stays 1 per entry: this is a browse list, not a tally.
            var albums = new List<AlbumData>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            if (_api.Library_QueryLookupTable("album", "albumartist" + '\0' + "album", null))
                try
                {
                    var entries = _api.Library_QueryGetLookupTableValue(null)
                        .Split(DoubleSeparator, StringSplitOptions.None)
                        .Where(s => !string.IsNullOrEmpty(s))
                        .Select(s => s.Trim());

                    foreach (var entry in entries)
                    {
                        var album = CreateAlbum(entry);
                        var key = album.artist + "\0" + album.album;
                        if (seen.Add(key))
                            albums.Add(album);
                    }
                }
                catch (IndexOutOfRangeException)
                {
                    // Handle exception silently like in original code
                }

            _api.Library_QueryLookupTable(null, null, null);
            return albums;
        }

        public IEnumerable<Track> BrowseTracks(int offset = 0, int limit = 4000)
        {
            if (!_api.Library_QueryFiles(null))
                yield break;

            while (true)
            {
                var currentTrack = _api.Library_QueryGetNextFile();
                if (string.IsNullOrEmpty(currentTrack))
                    break;

                _api.Library_GetFileTags(currentTrack, BrowseTrackFields, out var tags);

                yield return new Track
                {
                    artist = Tag(tags, 0).Cleanup(),
                    title = Tag(tags, 1).Cleanup(),
                    album = Tag(tags, 2).Cleanup(),
                    album_artist = Tag(tags, 3).Cleanup(),
                    genre = Tag(tags, 4).Cleanup(),
                    trackno = ParseTag(tags, 5),
                    disc = ParseTag(tags, 6),
                    src = currentTrack
                };
            }
        }

        private static readonly Plugin.MetaDataType[] CustomTagSlots = new[]
        {
            Plugin.MetaDataType.Custom1, Plugin.MetaDataType.Custom2,
            Plugin.MetaDataType.Custom3, Plugin.MetaDataType.Custom4,
            Plugin.MetaDataType.Custom5, Plugin.MetaDataType.Custom6,
            Plugin.MetaDataType.Custom7, Plugin.MetaDataType.Custom8,
            Plugin.MetaDataType.Custom9, Plugin.MetaDataType.Custom10,
            Plugin.MetaDataType.Custom11, Plugin.MetaDataType.Custom12,
            Plugin.MetaDataType.Custom13, Plugin.MetaDataType.Custom14,
            Plugin.MetaDataType.Custom15, Plugin.MetaDataType.Custom16
        };

        private Plugin.MetaDataType? ResolveTagToMetaDataType(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return null;

            var clean = tag.Trim();

            // Direct standard names
            switch (clean.ToLowerInvariant())
            {
                case "tracktitle":
                case "title":
                    return Plugin.MetaDataType.TrackTitle;
                case "artist":
                    return Plugin.MetaDataType.Artist;
                case "album":
                    return Plugin.MetaDataType.Album;
                case "albumartist":
                    return Plugin.MetaDataType.AlbumArtist;
                case "genre":
                    return Plugin.MetaDataType.Genre;
                case "genres":
                    return Plugin.MetaDataType.Genres;
                case "year":
                    return Plugin.MetaDataType.Year;
                case "composer":
                    return Plugin.MetaDataType.Composer;
                case "comment":
                    return Plugin.MetaDataType.Comment;
                case "lyrics":
                    return Plugin.MetaDataType.Lyrics;
                case "mood":
                    return Plugin.MetaDataType.Mood;
                case "occasion":
                    return Plugin.MetaDataType.Occasion;
                case "grouping":
                    return Plugin.MetaDataType.Grouping;
                case "publisher":
                    return Plugin.MetaDataType.Publisher;
                case "bpm":
                case "beatspermin":
                    return Plugin.MetaDataType.BeatsPerMin;
            }

            // Direct enum match (e.g. "Custom1", "Custom2")
            if (Enum.TryParse<Plugin.MetaDataType>(clean, true, out var parsedEnum))
                return parsedEnum;

            // User-defined custom tag display names (e.g. "Energy", "Instruments")
            if (_api.Setting_GetFieldName != null)
            {
                foreach (var slot in CustomTagSlots)
                {
                    try
                    {
                        var fieldName = _api.Setting_GetFieldName(slot);
                        if (!string.IsNullOrEmpty(fieldName) &&
                            fieldName.Equals(clean, StringComparison.OrdinalIgnoreCase))
                        {
                            return slot;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return null;
        }

        private bool QueryLookupValues(string keyTag, List<string> values, int limit)
        {
            if (!_api.Library_QueryLookupTable(keyTag, "count", null))
            {
                _api.Library_QueryLookupTable(null, null, null);
                return false;
            }

            try
            {
                var rawValue = _api.Library_QueryGetLookupTableValue(null);
                if (!string.IsNullOrEmpty(rawValue))
                {
                    var seen = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
                    foreach (var item in rawValue.Split(DoubleSeparator, StringSplitOptions.None))
                    {
                        var parts = item.Split(NullSeparator, StringSplitOptions.None);
                        if (parts.Length >= 1)
                        {
                            var rawVal = parts[0].Cleanup();
                            if (!string.IsNullOrWhiteSpace(rawVal))
                            {
                                var split = rawVal.Split(new[] { ';', '\0' }, StringSplitOptions.RemoveEmptyEntries);
                                foreach (var part in split)
                                {
                                    var cleaned = part.Trim();
                                    if (!string.IsNullOrEmpty(cleaned) && seen.Add(cleaned))
                                    {
                                        values.Add(cleaned);
                                        if (limit > 0 && values.Count >= limit)
                                            return true;
                                    }
                                }
                            }
                        }
                    }
                }
                return values.Count > 0;
            }
            finally
            {
                _api.Library_QueryLookupTable(null, null, null);
            }
        }

        private void ScanFilesForGenres(List<string> values, int limit)
        {
            if (!_api.Library_QueryFiles(null))
                return;

            var seen = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var file = _api.Library_QueryGetNextFile();
                if (string.IsNullOrEmpty(file))
                    break;

                var val = _api.Library_GetFileTag(file, Plugin.MetaDataType.Genres);
                if (string.IsNullOrWhiteSpace(val))
                {
                    val = _api.Library_GetFileTag(file, Plugin.MetaDataType.Genre);
                }

                if (!string.IsNullOrWhiteSpace(val))
                {
                    var split = val.Split(new[] { ';', '\0' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in split)
                    {
                        var cleaned = part.Trim();
                        if (!string.IsNullOrEmpty(cleaned) && seen.Add(cleaned))
                        {
                            values.Add(cleaned);
                            if (limit > 0 && values.Count >= limit)
                                return;
                        }
                    }
                }
            }
        }

        private void FallbackQueryFilesForTag(Plugin.MetaDataType slot, List<string> values, int limit)
        {
            if (!_api.Library_QueryFiles(null))
                return;

            var seen = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
            while (true)
            {
                var file = _api.Library_QueryGetNextFile();
                if (string.IsNullOrEmpty(file))
                    break;

                var val = _api.Library_GetFileTag(file, slot);
                if (!string.IsNullOrWhiteSpace(val))
                {
                    var split = val.Split(new[] { ';', '\0' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in split)
                    {
                        var cleaned = part.Trim();
                        if (!string.IsNullOrEmpty(cleaned) && seen.Add(cleaned))
                        {
                            values.Add(cleaned);
                            if (limit > 0 && values.Count >= limit)
                                return;
                        }
                    }
                }
            }
        }

        public List<TagValuesEntry> BrowseTagValues(List<string> tags, int limit = 1000)
        {
            var result = new List<TagValuesEntry>();
            if (tags == null || tags.Count == 0)
                return result;

            foreach (var tag in tags)
            {
                if (string.IsNullOrWhiteSpace(tag))
                    continue;

                var entry = new TagValuesEntry
                {
                    tag = tag,
                    values = new List<string>()
                };

                var isGenre = tag.Equals("genre", StringComparison.OrdinalIgnoreCase) ||
                              tag.Equals("genres", StringComparison.OrdinalIgnoreCase);

                if (isGenre)
                {
                    // Query lookup tables first
                    QueryLookupValues("genres", entry.values, limit);
                    QueryLookupValues("genre", entry.values, limit);

                    // Also scan library files to capture all multi-value genres from tracks
                    ScanFilesForGenres(entry.values, limit);
                }
                else
                {
                    // 1. Try querying lookup table using original tag name
                    var success = QueryLookupValues(tag, entry.values, limit);

                    var slot = ResolveTagToMetaDataType(tag);

                    // 2. If no values found and tag maps to a known slot (e.g. Custom1), try slot name
                    if (!success && slot.HasValue)
                    {
                        var slotName = slot.Value.ToString();
                        if (!slotName.Equals(tag, StringComparison.OrdinalIgnoreCase))
                        {
                            success = QueryLookupValues(slotName, entry.values, limit);
                        }
                    }

                    // 3. Fallback: If lookup table returned nothing and we know the slot, scan library files
                    if (entry.values.Count == 0 && slot.HasValue)
                    {
                        FallbackQueryFilesForTag(slot.Value, entry.values, limit);
                    }
                }

                entry.values.Sort(StringComparer.OrdinalIgnoreCase);
                result.Add(entry);
            }

            return result;
        }

        #endregion

        #region Hierarchical Navigation

        public IEnumerable<Track> GetAlbumTracks(string album, SearchSource searchSource)
        {
            var filter = XmlFilterHelper.CreateFilter(
                AlbumSearchFields, album, true, searchSource);

            if (!_api.Library_QueryFiles(filter))
                yield break;

            while (true)
            {
                var currentTrack = _api.Library_QueryGetNextFile();
                if (string.IsNullOrEmpty(currentTrack))
                    break;

                _api.Library_GetFileTags(currentTrack, AlbumTrackFields, out var tags);

                // album/genre are intentionally left empty here (the old
                // ToTrackDto null-coalesced the unset internal fields to empty).
                yield return new Track
                {
                    artist = Tag(tags, 0),
                    title = Tag(tags, 1),
                    album_artist = Tag(tags, 2),
                    trackno = ParseTag(tags, 3),
                    disc = ParseTag(tags, 4),
                    src = currentTrack,
                    album = string.Empty,
                    genre = string.Empty
                };
            }
        }

        /// <summary>
        ///     Every track filed under one genre.
        /// </summary>
        /// <remarks>
        ///     Walking the genre's artists and their albums cannot answer this:
        ///     GetAlbumTracks leaves the genre field empty, so an artist filed
        ///     under two genres yields tracks nothing can tell apart. Asking the
        ///     library for the genre directly is both exact and one query.
        /// </remarks>
        public IEnumerable<Track> GetGenreTracks(string genre, SearchSource searchSource)
        {
            var filter = XmlFilterHelper.CreateFilter(
                GenreSearchFields, genre, true, searchSource);

            if (!_api.Library_QueryFiles(filter))
                yield break;

            while (true)
            {
                var currentTrack = _api.Library_QueryGetNextFile();
                if (string.IsNullOrEmpty(currentTrack))
                    break;

                _api.Library_GetFileTags(currentTrack, AlbumTrackFields, out var tags);

                yield return new Track
                {
                    artist = Tag(tags, 0),
                    title = Tag(tags, 1),
                    album_artist = Tag(tags, 2),
                    trackno = ParseTag(tags, 3),
                    disc = ParseTag(tags, 4),
                    src = currentTrack,
                    album = string.Empty,
                    genre = genre
                };
            }
        }

        /// <summary>
        ///     One entry per (album artist, album) for the given artist, each
        ///     carrying its track count. Unlike BrowseAlbums, the count here is
        ///     real.
        /// </summary>
        public List<AlbumData> GetArtistAlbums(string artist, SearchSource searchSource)
        {
            // The retired internal AlbumData did this via IEquatable +
            // IncreaseCount; here a Dictionary keyed on albumArtist\0album
            // carries the count.
            var albums = new Dictionary<string, AlbumData>(StringComparer.Ordinal);
            var filter = XmlFilterHelper.CreateFilter(
                ArtistSearchFields, artist, true, searchSource);

            if (_api.Library_QueryFiles(filter))
                while (true)
                {
                    var currentFile = _api.Library_QueryGetNextFile();
                    if (string.IsNullOrEmpty(currentFile))
                        break;

                    _api.Library_GetFileTags(currentFile, ArtistAlbumFields, out var tags);
                    var albumArtist = Tag(tags, 0);
                    var album = Tag(tags, 1);
                    var key = albumArtist + "\0" + album;

                    if (albums.TryGetValue(key, out var existing))
                        existing.count++;
                    else
                        albums[key] = new AlbumData { artist = albumArtist, album = album, count = 1 };
                }

            return albums.Values.ToList();
        }

        public IEnumerable<ArtistData> GetGenreArtists(string genre, SearchSource searchSource)
        {
            var filter = XmlFilterHelper.CreateFilter(
                GenreSearchFields, genre, true, searchSource);

            if (!_api.Library_QueryLookupTable("artist", "count", filter))
            {
                _api.Library_QueryLookupTable(null, null, null);
                yield break;
            }

            try
            {
                var rawValue = _api.Library_QueryGetLookupTableValue(null);
                foreach (var entry in rawValue.Split(DoubleSeparator, StringSplitOptions.None))
                {
                    var artistInfo = entry.Split(NullSeparator);
                    if (artistInfo.Length >= 2)
                    {
                        yield return new ArtistData
                        {
                            artist = artistInfo[0] ?? string.Empty,
                            count = int.Parse(artistInfo[1], CultureInfo.InvariantCulture),
                        };
                    }
                }
            }
            finally
            {
                _api.Library_QueryLookupTable(null, null, null);
            }
        }

        #endregion

        #region Artwork

        public byte[] GetArtworkDataForTrack(string trackPath)
        {
            var locations = Plugin.PictureLocations.EmbedInFile |
                            Plugin.PictureLocations.LinkToSource |
                            Plugin.PictureLocations.LinkToOrganisedCopy;

            var pictureUrl = string.Empty;
            var data = Array.Empty<byte>();

            _api.Library_GetArtworkEx(
                trackPath,
                0,
                true,
                out locations,
                out pictureUrl,
                out data
            );

            return data;
        }

        #endregion

        #region Album Cover Cache Support

        /// <summary>
        ///     One identity per album - artist, album, the first track path
        ///     seen, and the newest modification time across its tracks. The
        ///     core fingerprints the library from these, so the Modified value
        ///     is what decides whether the cover cache rebuilds.
        /// </summary>
        public List<(string Artist, string Album, string Path, long Modified)> GetAlbumIdentities()
        {
            // One `Library_QueryFiles` pass folded to one identity per album: a
            // bulk two-tag read and one property read per track. The three-call
            // shape it replaces cost 2-3 full scans plus about 4N tag reads.
            var albums = new Dictionary<string, (string Artist, string Album, string Path, long Modified)>();

            if (!_api.Library_QueryFiles(null))
                return new List<(string, string, string, long)>();

            var fields = new[] { Plugin.MetaDataType.AlbumArtist, Plugin.MetaDataType.Album };

            while (true)
            {
                var track = _api.Library_QueryGetNextFile();
                if (string.IsNullOrEmpty(track))
                    break;

                var success = _api.Library_GetFileTags(track, fields, out var tags);
                var artist = success && tags.Length > 0 ? tags[0].Cleanup() : string.Empty;
                var album = success && tags.Length > 1 ? tags[1].Cleanup() : string.Empty;

                // Fold case-insensitively so two tracks that differ only in tag
                // casing collapse to the one album the core's hashed key would.
                var dedupeKey = artist.ToLowerInvariant() + "\0" + album.ToLowerInvariant();
                var modified = ToUnixSeconds(
                    _api.Library_GetFileProperty(track, Plugin.FilePropertyType.DateModified));

                if (albums.TryGetValue(dedupeKey, out var existing))
                {
                    // Keep the first path seen; carry the newest modification time.
                    if (modified > existing.Modified)
                        albums[dedupeKey] = (existing.Artist, existing.Album, existing.Path, modified);
                }
                else
                {
                    albums[dedupeKey] = (artist, album, track, modified);
                }
            }

            return albums.Values.ToList();
        }

        #endregion

        #region Track Metadata

        public Dictionary<string, (string Artist, string Album)> GetBatchTrackMetadata(IEnumerable<string> trackPaths)
        {
            var result = new Dictionary<string, (string Artist, string Album)>();
            var metaDataFields = new[]
            {
                Plugin.MetaDataType.AlbumArtist,
                Plugin.MetaDataType.Album
            };

            foreach (var trackPath in trackPaths)
            {
                if (string.IsNullOrEmpty(trackPath))
                    continue;

                var tags = new string[metaDataFields.Length];
                var success = _api.Library_GetFileTags(trackPath, metaDataFields, out tags);

                var artist = success && tags.Length > 0 ? tags[0].Cleanup() : string.Empty;
                var album = success && tags.Length > 1 ? tags[1].Cleanup() : string.Empty;

                result[trackPath] = (artist, album);
            }

            return result;
        }

        #endregion

        #region Library Cache

        /// <summary>
        ///     Every track path in the library, in the same browse order as
        ///     BrowseTracks. The order is the contract, not a side effect: the
        ///     core stores these as an ordinal index and serves browse pages by
        ///     position into it, so returning them in a different order silently
        ///     renumbers every client's track list.
        /// </summary>
        public List<string> GetAllTrackPaths()
        {
            // The array form of the same Library_QueryFiles(null) stream
            // BrowseTracks walks, which is what makes the orders match.
            var success = _api.Library_QueryFilesEx(null, out var files);
            return success && files != null ? files.ToList() : new List<string>();
        }

        public List<Track> GetTracksForPaths(IEnumerable<string> paths)
        {
            var tracks = new List<Track>();
            foreach (var path in paths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(path))
                    continue;

                _api.Library_GetFileTags(path, BrowseTrackFields, out var tags);

                tracks.Add(new Track
                {
                    artist = Tag(tags, 0).Cleanup(),
                    title = Tag(tags, 1).Cleanup(),
                    album = Tag(tags, 2).Cleanup(),
                    album_artist = Tag(tags, 3).Cleanup(),
                    genre = Tag(tags, 4).Cleanup(),
                    trackno = ParseTag(tags, 5),
                    disc = ParseTag(tags, 6),
                    src = path,
                });
            }

            return tracks;
        }

        public List<TrackTags> GetTrackTags(IEnumerable<string> paths)
        {
            var tracks = new List<TrackTags>();
            foreach (var path in paths ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(path))
                    continue;

                _api.Library_GetFileTags(path, TrackTagFields, out var tags);
                // Duration ("m:ss") and DateAdded (display date) are file properties,
                // not metadata tags.
                var duration = _api.Library_GetFileProperty(path, Plugin.FilePropertyType.Duration);
                var dateAdded = _api.Library_GetFileProperty(path, Plugin.FilePropertyType.DateAdded);

                tracks.Add(new TrackTags
                {
                    src = path,
                    artist = Tag(tags, 0).Cleanup(),
                    title = Tag(tags, 1).Cleanup(),
                    album = Tag(tags, 2).Cleanup(),
                    album_artist = Tag(tags, 3).Cleanup(),
                    genre = Tag(tags, 4).Cleanup(),
                    track_no = ParseTag(tags, 5),
                    disc_no = ParseTag(tags, 6),
                    // Raw values, parsed core-side (year 4-digit extract, rating float).
                    year = Tag(tags, 7),
                    rating = Tag(tags, 8),
                    // Duration parsed to ms core-side; DateAdded is locale-ambiguous as
                    // a display string, so convert to ISO-8601 UTC here (#114).
                    duration = duration ?? string.Empty,
                    date_added = dateAdded.ToIso8601Utc(),
                });
            }

            return tracks;
        }

        public (List<string> Added, List<string> Updated, List<string> Deleted) GetSyncDelta(long updatedSince)
        {
            var empty = new List<string>();

            // Older MusicBee builds may not bind this delegate; treat as "no delta"
            // so the core falls back to a full index re-fetch.
            if (_api.Library_GetSyncDelta == null)
                return (empty, new List<string>(), new List<string>());

            // MusicBee compares against local file modification times, so feed a
            // local DateTime. Pass no cached files: we only want new + updated here
            // (deletes come from the core's re-fetched path index, so we avoid
            // marshalling the whole library's paths back across the boundary).
            var since = DateTimeOffset.FromUnixTimeSeconds(updatedSince).LocalDateTime;
            var ok = _api.Library_GetSyncDelta(
                Array.Empty<string>(),
                since,
                Plugin.LibraryCategory.Music,
                out var newFiles,
                out var updatedFiles,
                out var deletedFiles);

            if (!ok)
                return (empty, new List<string>(), new List<string>());

            return (
                (newFiles ?? Array.Empty<string>()).ToList(),
                (updatedFiles ?? Array.Empty<string>()).ToList(),
                (deletedFiles ?? Array.Empty<string>()).ToList());
        }

        #endregion

        #region Helper Methods

        /// <summary>Safe indexer into a bulk Library_GetFileTags result (empty when missing).</summary>
        private static string Tag(string[] tags, int index) =>
            tags != null && index < tags.Length && tags[index] != null ? tags[index] : string.Empty;

        /// <summary>A bulk-tag value parsed as an int (0 when missing/unparseable).</summary>
        private static int ParseTag(string[] tags, int index) =>
            int.TryParse(Tag(tags, index), out var value) ? value : 0;

        /// <summary>
        ///     Parse a MusicBee DateModified display string to unix seconds. The
        ///     core compares this against its own "now" (real unix seconds) to
        ///     decide whether a cached cover is stale, so both must be the same
        ///     instant on the same clock. Unparseable => 0 (treated as very old,
        ///     so the cover is kept).
        /// </summary>
        private static long ToUnixSeconds(string dateModified)
        {
            return DateTime.TryParse(dateModified, out var dt)
                ? ((DateTimeOffset)dt).ToUnixTimeSeconds()
                : 0;
        }

        private static AlbumData CreateAlbum(string queryResult)
        {
            var albumInfo = queryResult.Split(NullSeparator);
            albumInfo = albumInfo.Select(s => s.Cleanup()).ToArray();

            if (albumInfo.Length == 1)
                return new AlbumData { artist = albumInfo[0], album = string.Empty, count = 1 };
            if (albumInfo.Length == 2 && queryResult.StartsWith("\0", StringComparison.Ordinal))
                return new AlbumData { artist = albumInfo[1], album = string.Empty, count = 1 };

            return albumInfo.Length == 3
                ? new AlbumData { artist = albumInfo[1], album = albumInfo[2], count = 1 }
                : new AlbumData { artist = albumInfo[0], album = albumInfo[1], count = 1 };
        }

        #endregion
    }
}
