namespace Harness.Services;

// Files that belong to one profile, such as the expenses spreadsheet. They live outside the workspace so
// file tools cannot read another person's data (Personal:DataPath, default backend/personal).
public sealed class PersonalFiles(IHostEnvironment environment, IConfiguration configuration)
{
    public string Root { get; } = Path.GetFullPath(configuration["Personal:DataPath"] ??
                                                   Path.Combine(environment.ContentRootPath, "personal"));

    public string Folder(int profileId) => Path.Combine(Root, profileId.ToString());

    public string ExpensesPath(int profileId) => Path.Combine(Folder(profileId), "expenses.csv");

    // Concerts already reported by find_concerts with new_only, one key per line.
    public string SeenConcertsPath(int profileId) => Path.Combine(Folder(profileId), "concerts-seen.txt");

    // Job ads already reported by find_jobs with new_only, one Platsbanken id per line.
    public string SeenJobsPath(int profileId) => Path.Combine(Folder(profileId), "jobs-seen.txt");

    // Career pages the job radar watches (JobEmployers).
    public string JobEmployersPath(int profileId) => Path.Combine(Folder(profileId), "job-employers.json");

    public void DeleteProfile(int profileId)
    {
        if (Directory.Exists(Folder(profileId)))
            Directory.Delete(Folder(profileId), true);
    }
}
