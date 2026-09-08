using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.H4ip.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.H4ip;

/// <summary>
/// Recomputes play counts from Jellyfin user data, which is the authoritative source.
/// </summary>
public static class PlayCountBackfill
{
    /// <summary>
    /// Recomputes the cached artist/album/track play counts for every user.
    /// </summary>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="repository">The play count repository.</param>
    /// <param name="logger">The logger.</param>
    public static void Run(
        IUserManager userManager,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        H4ipRepository repository,
        ILogger logger)
    {
        foreach (var user in userManager.GetUsers())
        {
            var userId = user.Id.ToString();
            var result = libraryManager.GetItemsResult(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                IsPlayed = true,
                Recursive = true,
            });

            var counts = new Dictionary<(string Type, string Name), int>();
            foreach (var item in result.Items)
            {
                if (item is not Audio audio)
                {
                    continue;
                }

                var playCount = userDataManager.GetUserData(user, item)?.PlayCount ?? 0;
                if (playCount <= 0)
                {
                    continue;
                }

                var artist = GetArtistName(audio);
                if (!string.IsNullOrEmpty(artist))
                {
                    AddToCount(counts, "artist", artist, playCount);
                }

                if (!string.IsNullOrEmpty(audio.Album))
                {
                    AddToCount(counts, "album", audio.Album, playCount);
                }

                if (!string.IsNullOrEmpty(audio.Name))
                {
                    AddToCount(counts, "track", audio.Name, playCount);
                }
            }

            foreach (var (key, count) in counts)
            {
                repository.SeedPlayCount(userId, key.Type, key.Name, count);
            }

            if (counts.Count > 0)
            {
                var top = counts
                    .Where(kv => kv.Key.Type == "artist")
                    .OrderByDescending(kv => kv.Value)
                    .FirstOrDefault();
                logger.LogInformation(
                    "Rebuilt {Count} play-count entries for user {User}; top artist {Artist} with {Plays} plays",
                    counts.Count,
                    user.Username,
                    top.Key.Name ?? "(none)",
                    top.Value);
            }
        }

        static void AddToCount(Dictionary<(string Type, string Name), int> counts, string type, string name, int plays)
        {
            var key = (type, name);
            counts.TryGetValue(key, out var current);
            counts[key] = current + plays;
        }
    }

    /// <summary>
    /// Gets the primary artist name for an audio item.
    /// </summary>
    /// <param name="audio">The audio item.</param>
    private static string? GetArtistName(Audio audio)
    {
        return audio.AlbumArtists.Count > 0 ? audio.AlbumArtists[0] : audio.Artists.Count > 0 ? audio.Artists[0] : null;
    }
}
