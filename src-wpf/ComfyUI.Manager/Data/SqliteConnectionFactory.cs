using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// SqliteConnectionFactory:locates the catalog.db file and produces opened
/// connections with WAL journaling and foreign keys enabled.
///
/// The WPF side is a read-only consumer; the Python service owns schema
/// initialization. If the db file is missing we throw a clear error
/// instructing the caller to run the Python service once to bootstrap.
/// </summary>
public sealed class SqliteConnectionFactory
{
    private readonly string _dbPath;

    public string DbPath => _dbPath;

    public SqliteConnectionFactory()
    {
        _dbPath = ResolveDbPath();
    }

    /// <summary>
    /// Constructor used by tests to inject an explicit db path.
    /// </summary>
    public SqliteConnectionFactory(string dbPath)
    {
        _dbPath = dbPath;
    }

    private static string ResolveDbPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("COMFY_MGR_DB_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return overridePath;
        }

        var appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "ComfyUI-Manager", "catalog.db");
    }

    /// <summary>
    /// Opens a new SqliteConnection. Caller owns disposal.
    /// </summary>
    /// <exception cref="FileNotFoundException">
    /// Raised when the catalog db file does not exist; the Python service must
    /// initialize the schema before the WPF can read from it.
    /// </exception>
    public SqliteConnection Open()
    {
        if (!File.Exists(_dbPath))
        {
            throw new FileNotFoundException(
                $"catalog.db not found at {_dbPath}, " +
                "please run Python service once to initialize",
                _dbPath);
        }

        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();

        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
            pragma.ExecuteNonQuery();
        }

        return conn;
    }
}
