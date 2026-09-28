using System;
using System.Collections.Generic;
using System.Globalization;
using MusicBeePlugin.Providers;
using MusicBeePlugin.Models;
using MusicBeePlugin.Ffi;
using MusicBeePlugin.Utilities;

namespace MusicBeePlugin.Providers
{
    /// <summary>
    ///     Data provider implementation for track and now-playing operations.
    ///     Contains all MusicBee-specific API logic including bulk tag retrieval and query operations.
    /// </summary>
    public class TrackDataProvider : ITrackDataProvider
    {
        private static readonly string[] SearchFields = { "ArtistPeople", "Title" };

        // The now-playing list item field set (read in one bulk Library_GetFileTags
        // per windowed track). The V4 wire codec drops album/album_artist for
        // Android; both carry the full set so V6+ can surface them (#329/#196).
        private static readonly Plugin.MetaDataType[] NowPlayingFields =
        {
            Plugin.MetaDataType.Artist, Plugin.MetaDataType.Album,
            Plugin.MetaDataType.AlbumArtist, Plugin.MetaDataType.TrackTitle,
        };

        private readonly Plugin.MusicBeeApiInterface _api;

        public TrackDataProvider(Plugin.MusicBeeApiInterface api)
        {
            _api = api;
        }

        #region Now Playing Track Info

        public string GetNowPlayingFileUrl()
        {
            return _api.NowPlaying_GetFileUrl();
        }

        public TrackInfo GetNowPlayingTrackInfo()
        {
            // Use bulk API to get basic track info efficiently
            var fields = new[]
            {
                Plugin.MetaDataType.Artist,
                Plugin.MetaDataType.TrackTitle,
                Plugin.MetaDataType.Album,
                Plugin.MetaDataType.Year
            };

            var results = new string[fields.Length];
            var success = _api.NowPlaying_GetFileTags(fields, out results);

            // Raw values only: the V4 empty-field fallbacks (Unknown Artist/Album,
            // title -> file name) live in the Rust V4 wire codec now.
            return new TrackInfo
            {
                artist = SafeGetResult(success, results, 0),
                title = SafeGetResult(success, results, 1),
                album = SafeGetResult(success, results, 2),
                year = SafeGetResult(success, results, 3),
                path = GetNowPlayingFileUrl(),
            };
        }

        public TrackDetails GetNowPlayingTrackDetails()
        {
            // Use bulk API to get comprehensive track details efficiently
            var metadataFields = new[]
            {
                Plugin.MetaDataType.AlbumArtist,
                Plugin.MetaDataType.Genre,
                Plugin.MetaDataType.TrackNo,
                Plugin.MetaDataType.TrackCount,
                Plugin.MetaDataType.DiscNo,
                Plugin.MetaDataType.DiscCount,
                Plugin.MetaDataType.Grouping,
                Plugin.MetaDataType.Publisher,
                Plugin.MetaDataType.RatingAlbum,
                Plugin.MetaDataType.Composer,
                Plugin.MetaDataType.Comment,
                Plugin.MetaDataType.Encoder
            };

            var metadataResults = new string[metadataFields.Length];
            var metadataSuccess = _api.NowPlaying_GetFileTags(metadataFields, out metadataResults);

            // Raw values only; the empty-albumArtist -> "Unknown Artist" fallback
            // lives in the Rust V4 wire codec now.
            var details = new TrackDetails
            {
                albumArtist = string.Empty,
                genre = string.Empty,
                trackNo = string.Empty,
                trackCount = string.Empty,
                discNo = string.Empty,
                discCount = string.Empty,
                grouping = string.Empty,
                publisher = string.Empty,
                ratingAlbum = string.Empty,
                composer = string.Empty,
                comment = string.Empty,
                encoder = string.Empty,
                custom1 = string.Empty, custom1Name = string.Empty,
                custom2 = string.Empty, custom2Name = string.Empty,
                custom3 = string.Empty, custom3Name = string.Empty,
                custom4 = string.Empty, custom4Name = string.Empty,
                custom5 = string.Empty, custom5Name = string.Empty,
                custom6 = string.Empty, custom6Name = string.Empty,
                custom7 = string.Empty, custom7Name = string.Empty,
                custom8 = string.Empty, custom8Name = string.Empty,
                custom9 = string.Empty, custom9Name = string.Empty,
                custom10 = string.Empty, custom10Name = string.Empty,
                custom11 = string.Empty, custom11Name = string.Empty,
                custom12 = string.Empty, custom12Name = string.Empty,
                custom13 = string.Empty, custom13Name = string.Empty,
                custom14 = string.Empty, custom14Name = string.Empty,
                custom15 = string.Empty, custom15Name = string.Empty,
                custom16 = string.Empty, custom16Name = string.Empty,
            };

            if (metadataSuccess && metadataResults != null && metadataResults.Length >= metadataFields.Length)
            {
                details.albumArtist = metadataResults[0].Cleanup();
                details.genre = metadataResults[1].Cleanup();
                details.trackNo = metadataResults[2].Cleanup();
                details.trackCount = metadataResults[3].Cleanup();
                details.discNo = metadataResults[4].Cleanup();
                details.discCount = metadataResults[5].Cleanup();
                details.grouping = metadataResults[6].Cleanup();
                details.publisher = metadataResults[7].Cleanup();
                details.ratingAlbum = metadataResults[8].Cleanup();
                details.composer = metadataResults[9].Cleanup();
                details.comment = metadataResults[10].Cleanup();
                details.encoder = metadataResults[11].Cleanup();
            }

            // Populate custom metadata tags (Custom1..Custom16) and their configured field names
            try
            {
                PopulateCustomTags(details);
            }
            catch
            {
                // Non-critical: failure to read custom tags should not fail TrackDetails
            }

            // Get file properties (these use different API methods)
            details.kind = _api.NowPlaying_GetFileProperty(Plugin.FilePropertyType.Kind).Cleanup();
            details.format = _api.NowPlaying_GetFileProperty(Plugin.FilePropertyType.Format).Cleanup();
            details.size = _api.NowPlaying_GetFileProperty(Plugin.FilePropertyType.Size).Cleanup();
            details.channels = _api.NowPlaying_GetFileProperty(Plugin.FilePropertyType.Channels).Cleanup();
            details.sampleRate = _api.NowPlaying_GetFileProperty(Plugin.FilePropertyType.SampleRate).Cleanup();
            details.bitrate = _api.NowPlaying_GetFileProperty(Plugin.FilePropertyType.Bitrate).Cleanup();
            details.dateModified = _api.NowPlaying_GetFileProperty(Plugin.FilePropertyType.DateModified).Cleanup();
            details.dateAdded = _api.NowPlaying_GetFileProperty(Plugin.FilePropertyType.DateAdded).Cleanup();
            details.lastPlayed = _api.NowPlaying_GetFileProperty(Plugin.FilePropertyType.LastPlayed).Cleanup();
            details.playCount = _api.NowPlaying_GetFileProperty(Plugin.FilePropertyType.PlayCount).Cleanup();
            details.skipCount = _api.NowPlaying_GetFileProperty(Plugin.FilePropertyType.SkipCount).Cleanup();
            details.duration = _api.NowPlaying_GetFileProperty(Plugin.FilePropertyType.Duration).Cleanup();

            return details;
        }

        public PlaybackPositionResponse GetPlaybackPosition()
        {
            var current = _api.Player_GetPosition();
            var total = _api.NowPlaying_GetDuration();
            return new PlaybackPositionResponse { current = current, total = total };
        }

        #endregion

        #region Media Content

        public string GetNowPlayingArtwork()
        {
            return _api.NowPlaying_GetArtwork();
        }

        public string GetNowPlayingDownloadedArtwork()
        {
            return _api.ApiRevision >= 17 ? _api.NowPlaying_GetDownloadedArtwork() : string.Empty;
        }

        public string GetNowPlayingLyrics()
        {
            var lyrics = _api.NowPlaying_GetLyrics();

            if (!string.IsNullOrEmpty(lyrics))
                return lyrics;

            return _api.ApiRevision >= 17 ? _api.NowPlaying_GetDownloadedLyrics() : string.Empty;
        }

        public string GetSyncedLyrics()
        {
            // Prefer real synchronized (LRC) lyrics for the current file, then
            // unsynchronised, before the paramless now-playing lyrics - a track can
            // have synced LRC the paramless call does not return (#113).
            var url = _api.NowPlaying_GetFileUrl();
            if (!string.IsNullOrEmpty(url) && _api.Library_GetLyrics != null)
            {
                var synced = _api.Library_GetLyrics(url, Plugin.LyricsType.Synchronised);
                if (!string.IsNullOrEmpty(synced))
                    return synced;

                var unsynced = _api.Library_GetLyrics(url, Plugin.LyricsType.UnSynchronised);
                if (!string.IsNullOrEmpty(unsynced))
                    return unsynced;
            }

            return GetNowPlayingLyrics();
        }

        #endregion

        #region Now Playing List Operations

        public string SearchNowPlayingList(string query, SearchSource searchSource)
        {
            var filter = XmlFilterHelper.CreateFilter(SearchFields, query, false, searchSource);
            if (!_api.NowPlayingList_QueryFiles(filter))
                return string.Empty;

            while (true)
            {
                var currentTrack = _api.NowPlayingList_QueryGetNextFile();
                if (string.IsNullOrEmpty(currentTrack))
                    break;

                // Use bulk API for efficiency - get both artist and title in one call
                var fields = new[] { Plugin.MetaDataType.Artist, Plugin.MetaDataType.TrackTitle };
                var results = new string[2];
                var success = _api.Library_GetFileTags(currentTrack, fields, out results);

                if (!success || results.Length < 2)
                    continue;

                var artist = results[0] ?? string.Empty;
                var title = results[1] ?? string.Empty;

                if (title.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    artist.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    return currentTrack;
            }

            return null;
        }

        public IEnumerable<NowPlayingListTrack> GetNowPlayingListOrdered(int offset, int limit)
        {
            if (!_api.NowPlayingList_QueryFiles(null))
                yield break;

            // Walk the ordered sequence (current track forward) exactly as before,
            // but read tags ONLY inside the window [offset, offset+limit). Items
            // before the window advance the index cursor without a tag read, so a
            // page costs O(limit) tag reads, not O(list).
            var windowEnd = limit > 0 ? (long)offset + limit : long.MaxValue;
            var position = 1;
            var itemIndex = _api.NowPlayingList_GetCurrentIndex();
            while (position <= windowEnd)
            {
                var trackPath = _api.NowPlayingList_GetListFileUrl(itemIndex);
                if (string.IsNullOrEmpty(trackPath))
                    break;

                if (position > offset)
                {
                    // By INDEX, not URL: every virtual track of a single-file
                    // .cue album reports the same container URL, so a URL-keyed
                    // read returns the container's empty tags for all of them and
                    // the album shows N rows of "Unknown Artist" (#87).
                    var success = _api.NowPlayingList_GetFileTags(itemIndex, NowPlayingFields, out var results);
                    // Raw values; the empty title -> file name fallback lives in the
                    // Rust V4 wire codec now. position is the actual list index.
                    yield return new NowPlayingListTrack
                    {
                        artist = SafeGetResult(success, results, 0),
                        album = SafeGetResult(success, results, 1),
                        album_artist = SafeGetResult(success, results, 2),
                        title = SafeGetResult(success, results, 3),
                        position = itemIndex,
                        path = trackPath
                    };
                }

                itemIndex = _api.NowPlayingList_GetNextIndex(position);
                position++;
            }
        }

        public NowPlayingOrder GetNowPlayingListOrder()
        {
            var order = new NowPlayingOrder { positions = new List<int>(), paths = new List<string>() };
            if (!_api.NowPlayingList_QueryFiles(null))
                return order;

            var position = 1;
            var itemIndex = _api.NowPlayingList_GetCurrentIndex();
            while (true)
            {
                var trackPath = _api.NowPlayingList_GetListFileUrl(itemIndex);
                if (string.IsNullOrEmpty(trackPath))
                    return order;

                order.positions.Add(itemIndex);
                order.paths.Add(trackPath);
                itemIndex = _api.NowPlayingList_GetNextIndex(position);
                position++;
            }
        }

        public IEnumerable<NowPlayingListTrack> GetNowPlayingListPage(int offset, int limit)
        {
            if (!_api.NowPlayingList_QueryFiles(null))
                yield break;

            // By INDEX rather than the query cursor, so a row's path and tags come
            // from the same index and cannot be attributed to different tracks.
            // That also lets the window be entered directly instead of pulling a
            // path per item to advance a cursor.
            var windowEnd = limit > 0 ? (long)offset + limit : long.MaxValue;
            var index = offset > 0 ? offset : 0;
            var position = index + 1;
            while (position <= windowEnd)
            {
                var trackPath = _api.NowPlayingList_GetListFileUrl(index);
                if (string.IsNullOrEmpty(trackPath))
                    break;

                var success = _api.NowPlayingList_GetFileTags(index, NowPlayingFields, out var results);
                // Raw values; the V4 empty-artist -> "Unknown Artist" and empty
                // title -> file name fallbacks live in the Rust V4 wire codec.
                yield return new NowPlayingListTrack
                {
                    artist = SafeGetResult(success, results, 0),
                    album = SafeGetResult(success, results, 1),
                    album_artist = SafeGetResult(success, results, 2),
                    title = SafeGetResult(success, results, 3),
                    position = position,
                    path = trackPath
                };

                index++;
                position++;
            }
        }

        public int GetNowPlayingListCount()
        {
            return GetNowPlayingListPaths().Length;
        }

        public string[] GetNowPlayingListPaths()
        {
            // Paths-only enumeration: the whole list's URLs in one call, no tag
            // reads. What the page query cannot give, since it reads its window.
            return _api.NowPlayingList_QueryFilesEx(null, out var files) && files != null
                ? files
                : Array.Empty<string>();
        }

        #endregion

        #region Rating Operations

        public string GetNowPlayingRating()
        {
            var currentTrack = _api.NowPlaying_GetFileUrl();
            if (string.IsNullOrEmpty(currentTrack))
                return string.Empty;

            var rating = _api.Library_GetFileTag(currentTrack, Plugin.MetaDataType.Rating);

            // Convert decimal separator to dots for consistency
            var decimalSeparator = Convert.ToChar(CultureInfo.CurrentCulture.NumberFormat
                .NumberDecimalSeparator, CultureInfo.InvariantCulture);
            return rating.Replace(decimalSeparator, '.');
        }

        public bool SetNowPlayingRating(string rating)
        {
            var currentTrack = _api.NowPlaying_GetFileUrl();
            if (string.IsNullOrEmpty(currentTrack))
                return false;

            try
            {
                var decimalSeparator = Convert.ToChar(CultureInfo.CurrentCulture
                    .NumberFormat.NumberDecimalSeparator, CultureInfo.InvariantCulture);
                rating = rating.Replace('.', decimalSeparator);

                if (!float.TryParse(rating, out var fRating))
                    fRating = -1;

                // Only set rating if it's valid (0-5) or empty string
                if ((fRating >= 0 && fRating <= 5) || rating == "")
                {
                    var value = rating == ""
                        ? rating.ToString(CultureInfo.CurrentCulture)
                        : fRating.ToString(CultureInfo.CurrentCulture);

                    var success = _api.Library_SetFileTag(currentTrack, Plugin.MetaDataType.Rating, value);
                    if (!success) return false;

                    var commitSuccess = _api.Library_CommitTagsToFile(currentTrack);
                    _api.Player_GetShowRatingTrack(); // Returns void - refresh UI
                    _api.MB_RefreshPanels(); // Returns void - refresh UI
                    return commitSuccess;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Last.fm Operations

        public LastfmStatus GetNowPlayingLastfmStatus()
        {
            // Compared literal-first: the tag read is null whenever nothing is loaded,
            // which is the ordinary state at startup.
            var apiReply = _api.NowPlaying_GetFileTag(Plugin.MetaDataType.RatingLove);
            if ("L".Equals(apiReply, StringComparison.Ordinal) ||
                "lfm".Equals(apiReply, StringComparison.Ordinal) ||
                "Llfm".Equals(apiReply, StringComparison.Ordinal))
                return LastfmStatus.Love;
            if ("B".Equals(apiReply, StringComparison.Ordinal) ||
                "Blfm".Equals(apiReply, StringComparison.Ordinal))
                return LastfmStatus.Ban;
            return LastfmStatus.Normal;
        }

        public bool SetNowPlayingLastfmStatus(LastfmStatus status)
        {
            var fileUrl = _api.NowPlaying_GetFileUrl();
            string tagValue;

            switch (status)
            {
                case LastfmStatus.Love:
                    tagValue = "Llfm";
                    break;
                case LastfmStatus.Ban:
                    tagValue = "Blfm";
                    break;
                case LastfmStatus.Normal:
                    tagValue = "lfm";
                    break;
                default:
                    return false;
            }

            return _api.Library_SetFileTag(fileUrl, Plugin.MetaDataType.RatingLove, tagValue);
        }

        #endregion

        #region Tag Operations

        public bool SetTrackTag(string fileUrl, string tagName, string value)
        {
            var metadataType = GetMetaDataTypeFromTagName(tagName);
            if (metadataType == null)
                return false;

            return _api.Library_SetFileTag(fileUrl, metadataType.Value, value);
        }

        public bool CommitTrackTags(string fileUrl)
        {
            var success = _api.Library_CommitTagsToFile(fileUrl);
            try
            {
                _api.MB_RefreshPanels();
            }
            catch
            {
                // Non-critical: failure to refresh panels shouldn't fail commit
            }
            return success;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        ///     Safely retrieves a result from a string array with bounds checking.
        /// </summary>
        private static string SafeGetResult(bool success, string[] results, int index)
        {
            return success && results != null && results.Length > index
                ? results[index]
                : string.Empty;
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

        private void PopulateCustomTags(TrackDetails details)
        {
            if (_api.NowPlaying_GetFileTag == null)
                return;

            string[] results = null;
            var success = _api.NowPlaying_GetFileTags != null &&
                          _api.NowPlaying_GetFileTags(CustomTagSlots, out results) &&
                          results != null && results.Length >= CustomTagSlots.Length;

            for (var i = 0; i < CustomTagSlots.Length; i++)
            {
                var slot = CustomTagSlots[i];
                var val = success ? results[i].Cleanup() : (_api.NowPlaying_GetFileTag(slot) ?? string.Empty).Cleanup();
                var name = (_api.Setting_GetFieldName != null ? _api.Setting_GetFieldName(slot) : null) ?? string.Empty;

                switch (i)
                {
                    case 0: details.custom1 = val; details.custom1Name = name; break;
                    case 1: details.custom2 = val; details.custom2Name = name; break;
                    case 2: details.custom3 = val; details.custom3Name = name; break;
                    case 3: details.custom4 = val; details.custom4Name = name; break;
                    case 4: details.custom5 = val; details.custom5Name = name; break;
                    case 5: details.custom6 = val; details.custom6Name = name; break;
                    case 6: details.custom7 = val; details.custom7Name = name; break;
                    case 7: details.custom8 = val; details.custom8Name = name; break;
                    case 8: details.custom9 = val; details.custom9Name = name; break;
                    case 9: details.custom10 = val; details.custom10Name = name; break;
                    case 10: details.custom11 = val; details.custom11Name = name; break;
                    case 11: details.custom12 = val; details.custom12Name = name; break;
                    case 12: details.custom13 = val; details.custom13Name = name; break;
                    case 13: details.custom14 = val; details.custom14Name = name; break;
                    case 14: details.custom15 = val; details.custom15Name = name; break;
                    case 15: details.custom16 = val; details.custom16Name = name; break;
                }
            }
        }

        private Plugin.MetaDataType? GetMetaDataTypeFromTagName(string tagName)
        {
            if (string.IsNullOrWhiteSpace(tagName))
                return null;

            var clean = tagName.Trim();

            // 1. Direct standard names
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

            // 2. Direct enum name matching (e.g. "Custom1", "Custom2")
            if (Enum.TryParse<Plugin.MetaDataType>(clean, true, out var parsedEnum))
            {
                return parsedEnum;
            }

            // 3. User-defined custom tag display names (e.g. "Energy", "Instruments")
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
                        // Ignore any failure during name resolution
                    }
                }
            }

            return null;
        }

        #endregion
    }
}
