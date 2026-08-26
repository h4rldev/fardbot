using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.H4ip.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.H4ip;

/// <summary>
/// Listens for library and playback events and pushes them to the h4bot endpoint.
/// </summary>
public class EventMonitorEntryPoint : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ISessionManager _sessionManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EventMonitorEntryPoint> _logger;
    private readonly H4ipRepository _repository;
    private readonly IUserManager _userManager;

    // ponytail: scan-scoped dedupe set. Grows with unique artist count (bounded for a home library);
    // move to a DB-backed "already announced" check if it ever grows unbounded.
    private readonly HashSet<string> _announcedArtists = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Initializes a new instance of the <see cref="EventMonitorEntryPoint"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="repository">Playcount/suggestion repository.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="logger">Logger.</param>
    public EventMonitorEntryPoint(
        ILibraryManager libraryManager,
        ISessionManager sessionManager,
        IHttpClientFactory httpClientFactory,
        H4ipRepository repository,
        IUserManager userManager,
        ILogger<EventMonitorEntryPoint> logger)
    {
        _libraryManager = libraryManager;
        _sessionManager = sessionManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _repository = repository;
        _userManager = userManager;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;

        try
        {
            BackfillPlayCounts();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to backfill play counts");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds an artist, or Announces a track on ItemAdded.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is MusicArtist artist)
        {
            _announcedArtists.Add(artist.Name);
            _logger.LogInformation("New artist added: {Artist}", artist.Name);
            PostEvent(new { kind = "artist_added", artist = artist.Name, itemId = artist.Id.ToString("N") });
            return;
        }

        if (e.Item is Audio audio)
        {
            AnnounceTrack(audio);
        }
    }

    /// <summary>
    /// Announces a track on ItemUpdated.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Audio audio)
        {
            AnnounceTrack(audio);
        }
    }

    /// <summary>
    /// Increments the play count on PlaybackStopped.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (!e.PlayedToCompletion || e.Users.Count == 0 || e.Item is not Audio audio)
        {
            return;
        }

        var userId = e.Users[0].Id.ToString();
        var artist = GetArtistName(audio);
        if (!string.IsNullOrEmpty(artist))
        {
            _repository.IncrementPlayCount(userId, "artist", artist);
        }

        if (!string.IsNullOrEmpty(audio.Album))
        {
            _repository.IncrementPlayCount(userId, "album", audio.Album);
        }

        if (!string.IsNullOrEmpty(audio.Name))
        {
            _repository.IncrementPlayCount(userId, "track", audio.Name);
        }

        _logger.LogInformation("Incremented play count for {Artist} by {User}", artist ?? audio.Name, e.Users[0].Username);
    }

    /// <summary>
    /// Announces a track.
    /// </summary>
    /// <param name="audio">The audio item.</param>
    private void AnnounceTrack(Audio audio)
    {
        var artist = GetArtistName(audio);
        if (string.IsNullOrEmpty(artist) || _announcedArtists.Contains(artist))
        {
            return;
        }

        _logger.LogInformation("New track added: {Track} by {Artist}", audio.Name, artist);
        PostEvent(new { kind = "track_added", artist, track = audio.Name, album = audio.Album, itemId = audio.Id.ToString("N") });
    }

    /// <summary>
    /// Gets the artist name from an audio item.
    /// </summary>
    /// <param name="audio">The audio item.</param>
    private static string? GetArtistName(Audio audio)
    {
        return audio.AlbumArtists.Count > 0 ? audio.AlbumArtists[0] : audio.Artists.Count > 0 ? audio.Artists[0] : null;
    }

    /// <summary>
    /// Triggers an event broadcast.
    /// </summary>
    /// <param name="payload">The payload.</param>
    private void PostEvent(object payload)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrEmpty(config.BotUrl) || string.IsNullOrEmpty(config.SharedSecret))
        {
            _logger.LogWarning("H4ip not configured; skipping broadcast");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                using var client = _httpClientFactory.CreateClient();
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{config.BotUrl}/jellyfin/event");
                request.Headers.Add("X-H4ip-Secret", config.SharedSecret);
                request.Content = JsonContent.Create(payload);
                _logger.LogInformation("Broadcasting event to {Url}", config.BotUrl);
                using var response = await client.SendAsync(request).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Broadcast succeeded ({Status})", response.StatusCode);
                }
                else
                {
                    _logger.LogWarning("Broadcast returned {Status}", response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to broadcast event");
            }
        });
    }

    /// <summary>
    /// Backfills play counts for all users to populate the SQLite3 database.
    /// </summary>
    private void BackfillPlayCounts()
    {
        foreach (var user in _userManager.GetUsers())
        {
            var items = _libraryManager.GetItems(new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { BaseItemKind.Audio },
                Filters = new[] { ItemFilter.IsPlayed },
                Recursive = true,
            });

            foreach (var item in items)
            {
                if (item is not Audio audio)
                {
                    continue;
                }

                var playCount = item.UserData?.PlayCount ?? 0;
                if (playCount <= 0)
                {
                    continue;
                }

                var userId = user.Id.ToString();
                var artist = GetArtistName(audio);

                if (!string.IsNullOrEmpty(artist))
                {
                    _repository.SeedPlayCount(userId, "artist", artist, playCount);
                }

                if (!string.IsNullOrEmpty(audio.Album))
                {
                    _repository.SeedPlayCount(userId, "album", audio.Album, playCount);
                }

                if (!string.IsNullOrEmpty(audio.Name))
                {
                    _repository.SeedPlayCount(userId, "track", audio.Name, playCount);
                }
            }
        }
    }
}
