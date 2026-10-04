using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Harness.Services;

// The employers the job radar watches for the current profile: career pages whose job boards are read
// directly. Kept as a small JSON file next to the profile's other personal files.
public sealed class JobEmployers(PersonalFiles files, CurrentProfile profile, CareerBoards boards)
{
    public record Employer(string Id, string Name, string Url, string System, string Feed);

    // Watched by name: searched in Platsbanken, and recruiters' ads that name the company are kept.
    public const string ByName = "name";

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private string FilePath => files.JobEmployersPath(profile.Id ?? throw new InvalidOperationException("Needs a profile."));

    public async Task<List<Employer>> ListAsync(CancellationToken ct) =>
        File.Exists(FilePath)
            ? JsonSerializer.Deserialize<List<Employer>>(await File.ReadAllTextAsync(FilePath, ct), s_json) ?? []
            : [];

    // Recognises the job system behind the address; the same board is only added once. A company name, or a
    // career page Leona cannot read, is watched by name instead: in Platsbanken and in recruiters' ads.
    public async Task<Employer> AddAsync(string url, string? name, CancellationToken ct)
    {
        url = url.Trim();
        CareerBoards.Board board;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var address))
        {
            var company = (name ?? url).Trim();
            if (company.Length is < 2 or > 80)
                throw new ArgumentException("Give a career page address or a company name.");
            board = new CareerBoards.Board(ByName, "name:" + company.ToLowerInvariant(), company, "");
        }
        else
        {
            try
            {
                board = await boards.DetectAsync(url, ct);
            }
            catch (CareerBoards.UnknownSystemException)
            {
                var company = string.IsNullOrWhiteSpace(name) ? CareerBoards.NameFromHost(address) : name.Trim();
                board = new CareerBoards.Board(ByName, "name:" + company.ToLowerInvariant(), company, url);
            }
        }

        var employers = await ListAsync(ct);
        if (employers.FirstOrDefault(e => e.Feed == board.Feed) is { } existing)
            return existing;
        if (employers.Count >= 100)
            throw new ArgumentException("The job radar can watch at most 100 employers.");

        var employer = new Employer(Id(board.Feed), string.IsNullOrWhiteSpace(name) ? board.Name : name.Trim(),
            board.Url, board.System, board.Feed);
        employers.Add(employer);
        await SaveAsync(employers, ct);
        return employer;
    }

    public async Task<bool> RemoveAsync(string id, CancellationToken ct)
    {
        var employers = await ListAsync(ct);
        if (employers.RemoveAll(e => e.Id == id) == 0)
            return false;
        await SaveAsync(employers, ct);
        return true;
    }

    private async Task SaveAsync(List<Employer> employers, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(employers, s_json), ct);
        File.Move(temporary, FilePath, true);
    }

    private static string Id(string feed) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(feed)))[..12].ToLowerInvariant();
}
