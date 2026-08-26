using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.H4ip.Data;

/// <summary>
/// SQLite persistence for suggestions and play counts.
/// </summary>
public sealed class H4ipRepository : IDisposable
{
    private readonly string _dbPath;
    private readonly ILogger<H4ipRepository> _logger;
    private readonly object _lock = new();
    private SqliteConnection _connection;

    /// <summary>
    /// Initializes a new instance of the <see cref="H4ipRepository"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="logger">Logger.</param>
    public H4ipRepository(IApplicationPaths applicationPaths, ILogger<H4ipRepository> logger)
    {
        _logger = logger;
        _dbPath = Path.Combine(applicationPaths.DataPath, "h4ip.db");
        _connection = new SqliteConnection($"Data Source={_dbPath}");
        _connection.Open();
        Initialize();
    }

    /// <summary>
    /// Initializes the database.
    /// </summary>
    private void Initialize()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS Suggestions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Artist TEXT NOT NULL,
                AddedAt TEXT NOT NULL,
                Done INTEGER NOT NULL DEFAULT 0,
                Skipped INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS PlayCounts (
                UserId TEXT NOT NULL,
                ItemType TEXT NOT NULL,
                ItemName TEXT NOT NULL,
                Count INTEGER NOT NULL,
                PRIMARY KEY (UserId, ItemType, ItemName)
            );
            """;
        cmd.ExecuteNonQuery();

        // ponytail: one-time migration for pre-skip databases
        using var check = _connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Suggestions') WHERE name = 'Skipped'";
        if (Convert.ToInt64(check.ExecuteScalar()) == 0)
        {
            using var alter = _connection.CreateCommand();
            alter.CommandText = "ALTER TABLE Suggestions ADD COLUMN Skipped INTEGER NOT NULL DEFAULT 0";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Adds a suggestion.
    /// </summary>
    /// <param name="artist">The artist name.</param>
    public void AddSuggestion(string artist)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "INSERT INTO Suggestions (Artist, AddedAt, Done) VALUES ($artist, $at, 0)";
            cmd.Parameters.AddWithValue("$artist", artist);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Marks a suggestion as done.
    /// </summary>
    /// <param name="artist">The artist name.</param>
    public void MarkSuggestionDone(string artist)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE Suggestions SET Done = 1, Skipped = 0 WHERE Artist = $artist";
            cmd.Parameters.AddWithValue("$artist", artist);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Marks a suggestion as skipped.
    /// </summary>
    /// <param name="artist">The artist name.</param>
    public void SkipSuggestion(string artist)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE Suggestions SET Skipped = 1, Done = 0 WHERE Artist = $artist";
            cmd.Parameters.AddWithValue("$artist", artist);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Lists suggestions, optionally only pending ones.
    /// </summary>
    /// <param name="pendingOnly">Whether to only return pending suggestions.</param>
    /// <returns>A list of suggestions.</returns>
    public IReadOnlyList<(long Id, string Artist, string AddedAt, bool Done)> GetSuggestions(bool pendingOnly)
    {
        lock (_lock)
        {
            var results = new List<(long, string, string, bool)>();
            using var cmd = _connection.CreateCommand();
#pragma warning disable CA2100 // all queries are literals; values are parameterized
            cmd.CommandText = pendingOnly
                ? "SELECT Id, Artist, AddedAt, Done FROM Suggestions WHERE Done = 0 AND Skipped = 0 ORDER BY AddedAt"
                : "SELECT Id, Artist, AddedAt, Done FROM Suggestions ORDER BY AddedAt";
#pragma warning restore CA2100
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0));
            }

            return results;
        }
    }

    /// <summary>
    /// Increments the play count of an item for a user.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="itemType">The item type.</param>
    /// <param name="itemName">The item name.</param>
    public void IncrementPlayCount(string userId, string itemType, string itemName)
    {
        lock (_lock)
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO PlayCounts (UserId, ItemType, ItemName, Count) VALUES ($uid, $type, $name, 1)
                ON CONFLICT (UserId, ItemType, ItemName) DO UPDATE SET Count = Count + 1
                """;
            cmd.Parameters.AddWithValue("$uid", userId);
            cmd.Parameters.AddWithValue("$type", itemType);
            cmd.Parameters.AddWithValue("$name", itemName);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Gets a user's top items.
    /// </summary>
    /// <param name="userId">The user ID.</param>
    /// <param name="itemType">The item type.</param>
    /// <param name="limit">The number of items to return.</param>
    /// <returns>A list of top items.</returns>
    public IReadOnlyList<(string ItemName, int Count)> GetTopItems(string userId, string itemType, int limit)
    {
        lock (_lock)
        {
            var results = new List<(string, int)>();
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT ItemName, Count FROM PlayCounts WHERE UserId = $uid AND ItemType = $type ORDER BY Count DESC, ItemName LIMIT $limit";
            cmd.Parameters.AddWithValue("$uid", userId);
            cmd.Parameters.AddWithValue("$type", itemType);
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add((reader.GetString(0), reader.GetInt32(1)));
            }

            return results;
        }
    }

    /// <summary>
    /// Gets a user's top listeners for an item.
    /// </summary>
    /// <param name="itemType">The item type.</param>
    /// <param name="itemName">The item name.</param>
    /// <param name="limit">The number of listeners to return.</param>
    /// <returns>A list of top listeners.</returns>
    public IReadOnlyList<(string UserId, int Count)> GetTopListeners(string itemType, string itemName, int limit)
    {
        lock (_lock)
        {
            var results = new List<(string, int)>();
            using var cmd = _connection.CreateCommand();

            cmd.CommandText = "SELECT UserId, Count FROM PlayCounts WHERE ItemType = $type AND ItemName = $name ORDER BY Count DESC, UserId LIMIT $limit";
            cmd.Parameters.AddWithValue("$type", itemType);
            cmd.Parameters.AddWithValue("$name", itemName);
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add((reader.GetString(0), reader.GetInt32(1)));
            }

            return results;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _connection.Dispose();
        _connection = null!;
    }
}
