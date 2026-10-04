namespace Harness.Models;

// A single row (Id 1) of shared preferences; missing rows fall back to these defaults.
public class AppSettings
{
    public int Id { get; set; } = 1;
    // Kept per profile (Profile.CustomInstructions); filled in when settings are loaded.
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public string CustomInstructions { get; set; } = "";
    public int ContextWindow { get; set; } = 16384;
    public int MaxOutputTokens { get; set; } = 2048;
    public int ThinkingTokens { get; set; } = 4096;
    public string KeepAlive { get; set; } = "30m";
    public int SearchResults { get; set; } = 8;
    public int PageCharacters { get; set; } = 8000;
    public bool AutoTitles { get; set; } = true;
    public bool MemoryEnabled { get; set; } = true;
    // Used by new chats on every device and by scheduled tasks without their own model. Empty means the
    // model Ollama lists first, which is the one installed or changed most recently.
    public string DefaultModel { get; set; } = "";
}
