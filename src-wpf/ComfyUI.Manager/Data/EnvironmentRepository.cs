using System;
using System.Collections.Generic;
using ComfyUI.Manager.Models;
using Microsoft.Data.Sqlite;
using Environment = ComfyUI.Manager.Models.Environment;

namespace ComfyUI.Manager.Data;

/// <summary>
/// EnvironmentRepository:CRUD for the <c>environments</c> table.
/// </summary>
public sealed class EnvironmentRepository
{
    private readonly SqliteConnectionFactory _factory;

    public EnvironmentRepository(SqliteConnectionFactory factory)
    {
        _factory = factory;
    }

    public List<Environment> ListAll()
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, name, root_path, comfyui_layout, comfyui_source,
                   venv_path, python_executable, custom_nodes_path,
                   extra_model_paths_yaml, port, enabled_node_ids_json,
                   status, base_python_path, python_version, pid,
                   bed_profile_id, bed_status, bed_failed_reason
            FROM environments
            ORDER BY name";
        using var reader = cmd.ExecuteReader();
        var list = new List<Environment>();
        while (reader.Read())
        {
            list.Add(Read(reader));
        }
        return list;
    }

    public Environment? Get(string envId)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, name, root_path, comfyui_layout, comfyui_source,
                   venv_path, python_executable, custom_nodes_path,
                   extra_model_paths_yaml, port, enabled_node_ids_json,
                   status, base_python_path, python_version, pid,
                   bed_profile_id, bed_status, bed_failed_reason
            FROM environments WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", envId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public void Upsert(Environment env)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO environments
                (id, name, root_path, comfyui_layout, comfyui_source,
                 venv_path, python_executable, custom_nodes_path,
                 extra_model_paths_yaml, port, enabled_node_ids_json,
                 status, base_python_path, python_version, pid,
                 bed_profile_id, bed_status, bed_failed_reason)
            VALUES
                (@id, @name, @root_path, @comfyui_layout, @comfyui_source,
                 @venv_path, @python_executable, @custom_nodes_path,
                 @extra_model_paths_yaml, @port, @enabled_node_ids_json,
                 @status, @base_python_path, @python_version, @pid,
                 @bed_profile_id, @bed_status, @bed_failed_reason)
            ON CONFLICT(id) DO UPDATE SET
                name=excluded.name,
                root_path=excluded.root_path,
                comfyui_layout=excluded.comfyui_layout,
                comfyui_source=excluded.comfyui_source,
                venv_path=excluded.venv_path,
                python_executable=excluded.python_executable,
                custom_nodes_path=excluded.custom_nodes_path,
                extra_model_paths_yaml=excluded.extra_model_paths_yaml,
                port=excluded.port,
                enabled_node_ids_json=excluded.enabled_node_ids_json,
                status=excluded.status,
                base_python_path=excluded.base_python_path,
                python_version=excluded.python_version,
                pid=excluded.pid,
                bed_profile_id=excluded.bed_profile_id,
                bed_status=excluded.bed_status,
                bed_failed_reason=excluded.bed_failed_reason";
        Bind(cmd, env);
        cmd.ExecuteNonQuery();
    }

    public void Delete(string envId)
    {
        using var conn = _factory.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM environments WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", envId);
        cmd.ExecuteNonQuery();
    }

    private static Environment Read(SqliteDataReader reader)
    {
        var result = new Environment
        {
            Id = reader.GetString(0),
            Name = reader.GetString(1),
            RootPath = reader.GetString(2),
            ComfyuiLayout = reader.GetString(3),
            ComfyuiSource = reader.IsDBNull(4) ? null : reader.GetString(4),
            VenvPath = reader.IsDBNull(5) ? null : reader.GetString(5),
            PythonExecutable = reader.IsDBNull(6) ? null : reader.GetString(6),
            CustomNodesPath = reader.IsDBNull(7) ? null : reader.GetString(7),
            ExtraModelPathsYaml = reader.IsDBNull(8) ? null : reader.GetString(8),
            Port = reader.IsDBNull(9) ? null : reader.GetInt32(9),
            EnabledNodeIdsJson = reader.GetString(10),
            Status = reader.GetString(11),
            BasePythonPath = reader.GetString(12),
            PythonVersion = reader.GetString(13),
            Pid = reader.IsDBNull(14) ? null : reader.GetInt32(14),
            BedProfileId = reader.IsDBNull(15) ? null : reader.GetString(15),
            BedStatus = reader.IsDBNull(16) ? null : reader.GetString(16),
            BedFailedReason = reader.IsDBNull(17) ? null : reader.GetString(17),
        };

        // 老行 fallback:升级前 db 没有 base_python_path / python_version 列,
        // SELECT 会得到空字符串(因为 DEFAULT '')。在 Read 层做回填,
        // UI 不需要重复 null-check。Pid 永远在最后一列。
        if (string.IsNullOrEmpty(result.BasePythonPath))
            result.BasePythonPath = result.PythonExecutable ?? "";
        if (string.IsNullOrEmpty(result.PythonVersion))
            result.PythonVersion = "<unknown>";
        return result;
    }

    private static void Bind(SqliteCommand cmd, Environment env)
    {
        cmd.Parameters.AddWithValue("@id", env.Id);
        cmd.Parameters.AddWithValue("@name", env.Name);
        cmd.Parameters.AddWithValue("@root_path", env.RootPath);
        cmd.Parameters.AddWithValue("@comfyui_layout", env.ComfyuiLayout);
        cmd.Parameters.AddWithValue("@comfyui_source",
            (object?)env.ComfyuiSource ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@venv_path",
            (object?)env.VenvPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@python_executable",
            (object?)env.PythonExecutable ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@custom_nodes_path",
            (object?)env.CustomNodesPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@extra_model_paths_yaml",
            (object?)env.ExtraModelPathsYaml ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@port",
            (object?)env.Port ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@enabled_node_ids_json",
            env.EnabledNodeIdsJson);
        cmd.Parameters.AddWithValue("@status", env.Status);
        cmd.Parameters.AddWithValue("@base_python_path", env.BasePythonPath);
        cmd.Parameters.AddWithValue("@python_version", env.PythonVersion);
        cmd.Parameters.AddWithValue("@pid",
            (object?)env.Pid ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bed_profile_id",
            (object?)env.BedProfileId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bed_status",
            (object?)env.BedStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@bed_failed_reason",
            (object?)env.BedFailedReason ?? DBNull.Value);
    }
}
