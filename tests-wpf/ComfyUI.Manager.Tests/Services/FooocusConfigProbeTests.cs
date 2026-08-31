using System.Collections.Generic;
using ComfyUI.Manager.Models;
using ComfyUI.Manager.Services;
using Xunit;

namespace ComfyUI.Manager.Tests.Services;

/// <summary>
/// v1.0.0.x (2026-09-01) T23b:测试 <see cref="FooocusConfigProbe.ParseStdout"/>
/// 纯函数解析逻辑。Subprocess integration test 跳过(需要 env venv + Fooocus 源码,
/// CI 环境不一定有),focused unit test 锁 4 dict + 5 path JSON 解析。
/// </summary>
public sealed class FooocusConfigProbeTests
{
    [Fact]
    public void ParseStdout_ValidJson_AllFieldsExtracted()
    {
        // Fooocus upstream default preset(default.json)实际输出类似格式。
        // 4 dict 非空 + 5 path 正常解析。
        const string stdout = """
            PROBE_OK:{
                "checkpoint_downloads": {
                    "juggernautXL_v8Rundiffusion.safetensors": "https://huggingface.co/lllyasviel/fav_models/resolve/main/fav/juggernautXL_v8Rundiffusion.safetensors"
                },
                "lora_downloads": {
                    "sd_xl_offset_example-lora_1.0.safetensors": "https://huggingface.co/stabilityai/stable-diffusion-xl-base-1.0/resolve/main/sd_xl_offset_example-lora_1.0.safetensors"
                },
                "embeddings_downloads": {},
                "vae_downloads": {},
                "paths": {
                    "checkpoints": "models/checkpoints",
                    "loras": "models/loras",
                    "embeddings": "models/embeddings",
                    "vae": "models/vae"
                }
            }
            """;

        var cfg = FooocusConfigProbe.ParseStdout(stdout);

        Assert.NotNull(cfg);
        Assert.Single(cfg.CheckpointDownloads);
        Assert.Equal("https://huggingface.co/lllyasviel/fav_models/resolve/main/fav/juggernautXL_v8Rundiffusion.safetensors",
            cfg.CheckpointDownloads["juggernautXL_v8Rundiffusion.safetensors"]);
        Assert.Single(cfg.LoraDownloads);
        Assert.Empty(cfg.EmbeddingsDownloads);
        Assert.Empty(cfg.VaeDownloads);
        Assert.Equal("models/checkpoints", cfg.Paths["checkpoints"]);
        Assert.Equal("models/loras", cfg.Paths["loras"]);
        Assert.Equal("models/embeddings", cfg.Paths["embeddings"]);
        Assert.Equal("models/vae", cfg.Paths["vae"]);
    }

    [Fact]
    public void ParseStdout_AllDictsEmpty_PathDefaultsStillParsed()
    {
        // preset 干净(用户删了所有 preset models)—— 4 dict 空,5 path 还在
        const string stdout = """
            PROBE_OK:{
                "checkpoint_downloads": {},
                "lora_downloads": {},
                "embeddings_downloads": {},
                "vae_downloads": {},
                "paths": {
                    "checkpoints": "models/checkpoints",
                    "loras": "models/loras",
                    "embeddings": "models/embeddings",
                    "vae": "models/vae"
                }
            }
            """;

        var cfg = FooocusConfigProbe.ParseStdout(stdout);

        Assert.NotNull(cfg);
        Assert.Empty(cfg.CheckpointDownloads);
        Assert.Empty(cfg.LoraDownloads);
        Assert.Empty(cfg.EmbeddingsDownloads);
        Assert.Empty(cfg.VaeDownloads);
        // 5 path 仍在
        Assert.Equal(4, cfg.Paths.Count);
    }

    [Fact]
    public void ParseStdout_ChineseCharactersInUrl_Preserved()
    {
        // 测 UTF-8 字符 + 镜像 T23a StandardOutputEncoding=UTF8 配对
        const string stdout = """
            PROBE_OK:{
                "checkpoint_downloads": {"测试模型.safetensors": "https://huggingface.co/lllyasviel/测试/resolve/main/测试.safetensors"},
                "lora_downloads": {},
                "embeddings_downloads": {},
                "vae_downloads": {},
                "paths": {
                    "checkpoints": "模型/checkpoints",
                    "loras": "models/loras",
                    "embeddings": "models/embeddings",
                    "vae": "models/vae"
                }
            }
            """;

        var cfg = FooocusConfigProbe.ParseStdout(stdout);

        Assert.NotNull(cfg);
        Assert.Contains("测试模型.safetensors", cfg.CheckpointDownloads.Keys);
        Assert.Equal("模型/checkpoints", cfg.Paths["checkpoints"]);
    }

    [Fact]
    public void ParseStdout_ProbeErrorPrefix_ReturnsNull()
    {
        // Python 端 from modules import config 失败(例如 modules 目录损坏)
        const string stdout = "PROBE_ERROR:ModuleNotFoundError(\"No module named 'modules'\")";

        var cfg = FooocusConfigProbe.ParseStdout(stdout);

        Assert.Null(cfg);
    }

    [Fact]
    public void ParseStdout_EmptyStdout_ReturnsNull()
    {
        // 30s timeout 时 Python 完全没启动 → stdout 空
        var cfg = FooocusConfigProbe.ParseStdout("");

        Assert.Null(cfg);
    }

    [Fact]
    public void ParseStdout_MissingProbeOkPrefix_ReturnsNull()
    {
        // Python 端 print() 了非 JSON 内容(例如 Fooocus 上游哪天改了 probe 格式)
        const string stdout = "Some random output from fooocus probe";

        var cfg = FooocusConfigProbe.ParseStdout(stdout);

        Assert.Null(cfg);
    }

    [Fact]
    public void ParseStdout_MalformedJson_ReturnsNull()
    {
        const string stdout = "PROBE_OK:{\"checkpoint_downloads\": invalid json";

        var cfg = FooocusConfigProbe.ParseStdout(stdout);

        Assert.Null(cfg);
    }
}
