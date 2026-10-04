namespace Harness.Models;

// A folder the user has opened to the file and command tools, addressed by its short name.
public class AllowedFolder
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}
