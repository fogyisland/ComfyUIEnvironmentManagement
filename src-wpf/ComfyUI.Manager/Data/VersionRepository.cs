using System;
using System.Collections.Generic;
using ComfyUI.Manager.Models;
using Microsoft.Data.Sqlite;

namespace ComfyUI.Manager.Data;

/// <summary>
/// VersionRecord:row of the <c>versions</c> table (M4, schema TBD).
/// Field shape mirrors version_history so callers can share code; the table
/// itself is not yet defined in connection.py so reads return empty for now.
/// </summary>
public sealed class VersionRecord
{
    public string Id { get; set; } = "";
    public string EnvId { get; set; } = "";
    public string Package { get; set; } = "";
    public string Action { get; set; } = "";
    public string? VersionBefore { get; set; }
    public string? VersionAfter { get; set; }
    public string? PkgVersion { get; set; }
    public string Result { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public string PerformedAt { get; set; } = "";
}

/// <summary>
/// VersionRepository:CRUD for the <c>versions</c> (M4) and
/// <c>version_history</c> tables.
/// </summary>
public sealed class VersionRepository
{
    private readonly SqliteConnectionFactory _factory;

    public VersionRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// Stub:the <c>versions</c> table (M4, see T21) is not yet part of
    /// connection.py. Returns empty list until the schema is finalized.
    /// </summary>
    public List<VersionRecord> ListByEnvAndPackage(string envId, string package)
    {
        return new List<VersionRecord>();
    }

    public List<VersionHistoryEntry> ListHistoryByEnvAndPackage(
        string envId, string package)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, action, version_before, version_after, pkg_version,
                   result, performed_at
            FROM version_history
            WHERE env_id = @env_id AND package = @package
            ORDER BY performed_at DESC";
        cmd.Parameters.AddWithValue("@env_id", envId);
        cmd.Parameters.AddWithValue("@package", package);
        using var reader = cmd.ExecuteReader();
        var list = new List<VersionHistoryEntry>();
        while (reader.Read())
        {
            list.Add(new VersionHistoryEntry
            {
                Id = reader.GetString(0),
                Action = reader.GetString(1),
                VersionBefore = reader.IsDBNull(2) ? null : reader.GetString(2),
                VersionAfter = reader.IsDBNull(3) ? null : reader.GetString(3),
                PkgVersion = reader.IsDBNull(4) ? null : reader.GetString(4),
                Result = reader.GetString(5),
                PerformedAt = reader.GetString(6),
            });
        }
        return list;
    }

    public void InsertHistory(VersionHistoryEntry entry)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO version_history
                (id, env_id, package, action, version_before, version_after,
                 pkg_version, result, error_message, performed_at)
            VALUES
                (@id, @env_id, @package, @action, @version_before,
                 @version_after, @pkg_version, @result, @error_message,
                 @performed_at)";
        cmd.Parameters.AddWithValue("@id", entry.Id);
        cmd.Parameters.AddWithValue("@env_id",
            string.IsNullOrEmpty(entry.EnvId) ? "" : entry.EnvId);
        cmd.Parameters.AddWithValue("@package",
            string.IsNullOrEmpty(entry.Package) ? "" : entry.Package);
        cmd.Parameters.AddWithValue("@action", entry.Action);
        cmd.Parameters.AddWithValue("@version_before",
            (object?)entry.VersionBefore ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@version_after",
            (object?)entry.VersionAfter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@pkg_version",
            (object?)entry.PkgVersion ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@result", entry.Result);
        cmd.Parameters.AddWithValue("@error_message",
            (object?)entry.ErrorMessage ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@performed_at", entry.PerformedAt);
        cmd.ExecuteNonQuery();
    }
}
