using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.H4ip.Data;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.H4ip;

/// <summary>
/// Listens for library and playback events and pushes them to the h4bot endpoint.
/// </summary>
public sealed class EventMonitorEntryPoint : IHostedService, IDisposable
{
    private const int BatchSize = 25;

    private readonly ILibraryManager _libraryManager;
    private readonly ISessionManager _sessionManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EventMonitorEntryPoint> _logger;
    private readonly H4ipRepository _repository;
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly HashSet<string> _announcedArtists = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _existingItemIds = new();
    private readonly Channel<object> _eventQueue = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private Task _drainerTask = Task.CompletedTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="EventMonitorEntryPoint"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="httpClientFactory">HTTP client factory.</param>
    /// <param name="repository">Playcount/suggestion repository.</param>
    /// <param name="userManager">User manager.</param>
    /// <param name="userDataManager">User data manager.</param>
    /// <param name="logger">Logger.</param>
    public EventMonitorEntryPoint(
        ILibraryManager libraryManager,
        ISessionManager sessionManager,
        IHttpClientFactory httpClientFactory,
        H4ipRepository repository,
        IUserManager userManager,
        IUserDataManager userDataManager,
        ILogger<EventMonitorEntryPoint> logger)
    {
        _libraryManager = libraryManager;
        _sessionManager = sessionManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _repository = repository;
        _userManager = userManager;
        _userDataManager = userDataManager;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _existingItemIds.UnionWith(_libraryManager.GetItemIds(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Audio, BaseItemKind.MusicArtist },
                Recursive = true,
            }));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to snapshot existing library items");
        }

        _libraryManager.ItemAdded += OnItemAdded;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _drainerTask = Task.Run(() => DrainEventsAsync(_cts.Token), CancellationToken.None);

        // Jellyfin's UserData.PlayCount is the source of truth, so recompute the cache on
        // every boot. SeedPlayCount overwrites, so this converges and self-heals stale data.
        _ = Task.Run(
            () =>
            {
                try
                {
                    BackfillPlayCounts();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to backfill play counts");
                }
            },
            CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await _drainerTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Adds an artist, or Announces a track on ItemAdded.
    /// </summary>
    /// <param name="sender">The sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (_existingItemIds.Contains(e.Item.Id))
        {
            return;
        }

        if (e.Item is MusicArtist artist)
        {
            _existingItemIds.Add(artist.Id);
            _announcedArtists.Add(artist.Name);
            _logger.LogInformation("New artist added: {Artist}", artist.Name);
            PostEvent(new { kind = "artist_added", artist = artist.Name, itemId = artist.Id.ToString("N") });
            return;
        }

        if (e.Item is Audio audio)
        {
            _existingItemIds.Add(audio.Id);
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
    /// Queues an event for batched broadcast.
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

        _eventQueue.Writer.TryWrite(payload);
    }

    /// <summary>
    /// Drains queued events and posts them to the bot in batches.
    /// </summary>
    /// <param name="token">Cancellation token.</param>
    private async Task DrainEventsAsync(CancellationToken token)
    {
        try
        {
            while (await _eventQueue.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                var batch = new List<object>();
                while (batch.Count < BatchSize && _eventQueue.Reader.TryRead(out var payload))
                {
                    batch.Add(payload);
                }

                await PostBatchAsync(batch, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Posts a batch of events to the bot endpoint.
    /// </summary>
    /// <param name="batch">The batch of payloads.</param>
    /// <param name="token">Cancellation token.</param>
    private async Task PostBatchAsync(List<object> batch, CancellationToken token)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null)
        {
            return;
        }

        try
        {
            using var client = _httpClientFactory.CreateClient();
            _logger.LogInformation("Broadcasting {Count} events to {Url}", batch.Count, config.BotUrl);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{config.BotUrl}/jellyfin/event");
                request.Headers.Add("X-H4ip-Secret", config.SharedSecret);
                request.Content = JsonContent.Create(batch);
                using var response = await client.SendAsync(request, token).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Broadcast succeeded ({Status})", response.StatusCode);
                    return;
                }

                _logger.LogWarning("Broadcast returned {Status}", response.StatusCode);
                if (attempt == 0)
                {
                    await Task.Delay(500, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to broadcast events");
        }
    }

    /// <summary>
    /// Backfills play counts for all users to populate the SQLite3 database.
    /// </summary>
    private void BackfillPlayCounts()
    {
        PlayCountBackfill.Run(_userManager, _libraryManager, _userDataManager, _repository, _logger);
    }
}
