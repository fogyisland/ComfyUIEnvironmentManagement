using System.Text.Json.Serialization;

namespace ComfyUI.Manager.Models;

/// <summary>
/// UI 偏好(窗口尺寸/位置/侧栏状态/最近选中 env/最近视图)— v0.6.5.21。
/// 走 <c>&lt;projectRoot&gt;/config/ui-preferences.json</c>(G6),失败静默回退默认值。
/// </summary>
public class UiPreferences
{
    [JsonPropertyName("window_width")]     public double? WindowWidth    { get; set; }
    [JsonPropertyName("window_height")]    public double? WindowHeight   { get; set; }
    [JsonPropertyName("window_left")]      public double? WindowLeft     { get; set; }
    [JsonPropertyName("window_top")]       public double? WindowTop      { get; set; }
    [JsonPropertyName("window_maximized")] public bool    WindowMaximized { get; set; }
    [JsonPropertyName("sidebar_visible")]  public bool    SidebarVisible { get; set; } = true;
    [JsonPropertyName("last_selected_env_id")] public string? LastSelectedEnvId { get; set; }
    [JsonPropertyName("last_view_name")]       public string? LastViewName     { get; set; }
}
