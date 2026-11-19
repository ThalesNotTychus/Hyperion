using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexMultiLauncher;

public sealed record GitWorktreeEntry(string Path, string? Branch, string? HeadCommit, bool IsMain, bool IsDetached);

public sealed record GitRepositoryStatus(
    bool IsGitRepository,
    string WorkingDirectory,
    string? RepositoryRoot,
    string? CurrentBranch,
    string? HeadCommitShort,
    int ChangedFileCount,
    IReadOnlyList<GitWorktreeEntry> Worktrees);

public sealed record GitDiffLine(string Kind, int? OldLine, int? NewLine, string Content);

public sealed record GitDiffHunk(string Header, int OldStart, int NewStart, IReadOnlyList<GitDiffLine> Lines);

public sealed record GitFileDiff(
    string FilePath,
    string? OldFilePath,
    string Status,
    int Additions,
    int Deletions,
    IReadOnlyList<GitDiffHunk> Hunks);

public sealed record GitDiffSnapshot(
    bool IsGitRepository,
    string WorkingDirectory,
    string? Branch,
    int TotalFiles,
    int TotalAdditions,
    int TotalDeletions,
    IReadOnlyList<GitFileDiff> Files);

public sealed record DiffLineComment(
    string FilePath,
    int LineNumber,
    string Side,
    string CodeSnippet,
    string Comment);

public sealed record IsolatedWorktreeCreated(string RepositoryRoot, string WorktreePath, string BranchName, string BaseRef);

/// <summary>
/// Manages isolated Git worktrees for parallel multi-agent execution, parses structured diffs
/// for side-by-side and inline review, and merges winning branches back safely.
/// </summary>
public sealed class GitWorktreeService
{
    private static readonly Regex HunkHeaderRegex = new(
        @"^@@ -(?<oldStart>\d+)(?:,(?<oldCount>\d+))? \+(?<newStart>\d+)(?:,(?<newCount>\d+))? @@(?<tail>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SafeSlugRegex = new(@"[^a-z0-9\-]+", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly string _managedWorktreesRoot;

    public GitWorktreeService(string launcherRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(launcherRoot);
        _managedWorktreesRoot = ProfilePathSafety.GetContainedFullPath(Path.GetFullPath(launcherRoot), "data", "worktrees");
    }

    public string ManagedWorktreesRoot => _managedWorktreesRoot;

    public static string FindGitExecutable()
    {
        var candidates = new List<string>();

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Path.IsPathFullyQualified(dir))
                    candidates.Add(Path.Combine(dir, "git.exe"));
            }
        }

        foreach (var baseDir in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        }.Where(p => !string.IsNullOrWhiteSpace(p)))
        {
            candidates.Add(Path.Combine(baseDir, "Git", "cmd", "git.exe"));
            candidates.Add(Path.Combine(baseDir, "Git", "bin", "git.exe"));
        }

        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory);
        if (!string.IsNullOrWhiteSpace(systemRoot))
            candidates.Add(Path.Combine(systemRoot, "Tools", "Git", "cmd", "git.exe"));

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                ProfilePathSafety.ThrowIfReparsePath(candidate);
                return Path.GetFullPath(candidate);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
            }
        }

        throw new InvalidOperationException("未找到可用的 git.exe，请安装 Git for Windows 后使用 Worktree 隔离与 Diff 审查功能。");
    }

    public static string FindPreferredPowerShellExecutable()
    {
        var candidates = new List<string>();
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
            candidates.Add(Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"));

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Path.IsPathFullyQualified(dir))
                    candidates.Add(Path.Combine(dir, "pwsh.exe"));
            }
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                ProfilePathSafety.ThrowIfReparsePath(candidate);
                return Path.GetFullPath(candidate);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
            }
        }

        var winPs = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(winPs) ? winPs : "powershell.exe";
    }

    public async Task<GitRepositoryStatus> InspectRepositoryAsync(string directory, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return new GitRepositoryStatus(false, directory ?? string.Empty, null, null, null, 0, []);

        var fullDir = Path.GetFullPath(directory);
        var topLevel = await RunGitAsync(fullDir, ["rev-parse", "--show-toplevel"], throwOnError: false, token).ConfigureAwait(false);
        if (topLevel.ExitCode != 0 || string.IsNullOrWhiteSpace(topLevel.Stdout))
            return new GitRepositoryStatus(false, fullDir, null, null, null, 0, []);

        var repoRoot = Path.GetFullPath(topLevel.Stdout.Trim());
        var branchResult = await RunGitAsync(fullDir, ["branch", "--show-current"], throwOnError: false, token).ConfigureAwait(false);
        var headResult = await RunGitAsync(fullDir, ["rev-parse", "--short", "HEAD"], throwOnError: false, token).ConfigureAwait(false);
        var statusResult = await RunGitAsync(fullDir, ["status", "--porcelain=v1", "-uall"], throwOnError: false, token).ConfigureAwait(false);
        var worktreeResult = await RunGitAsync(repoRoot, ["worktree", "list", "--porcelain"], throwOnError: false, token).ConfigureAwait(false);

        var changedCount = statusResult.ExitCode == 0
            ? statusResult.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length
            : 0;
        var worktrees = worktreeResult.ExitCode == 0 ? ParseWorktreeList(worktreeResult.Stdout) : [];
        var branch = string.IsNullOrWhiteSpace(branchResult.Stdout) ? "DETACHED" : branchResult.Stdout.Trim();
        var head = string.IsNullOrWhiteSpace(headResult.Stdout) ? null : headResult.Stdout.Trim();

        return new GitRepositoryStatus(true, fullDir, repoRoot, branch, head, changedCount, worktrees);
    }

    public async Task<IsolatedWorktreeCreated> CreateIsolatedWorktreeAsync(
        string workingDirectory,
        string scopeId,
        string labelHint,
        string? baseRef,
        CancellationToken token)
    {
        var status = await InspectRepositoryAsync(workingDirectory, token).ConfigureAwait(false);
        if (!status.IsGitRepository || string.IsNullOrWhiteSpace(status.RepositoryRoot))
            throw new InvalidOperationException("所选项目目录不是 Git 仓库，无法创建独立 Git Worktree。请先在目录中执行 git init 并提交初始版本，或切换为普通目录模式。");

        var repoRoot = status.RepositoryRoot;
        if (string.IsNullOrWhiteSpace(status.HeadCommitShort))
        {
            var hasIdentity = (await RunGitAsync(repoRoot, ["config", "user.email"], throwOnError: false, token).ConfigureAwait(false)).ExitCode == 0;
            var initCommitArgs = hasIdentity
                ? new[] { "commit", "--allow-empty", "-m", "chore: initialize repository" }
                : new[] { "-c", "user.name=Hyperion", "-c", "user.email=hyperion@local", "commit", "--allow-empty", "-m", "chore: initialize repository" };
            await RunGitAsync(repoRoot, initCommitArgs, throwOnError: true, token).ConfigureAwait(false);
        }

        var slug = BuildSlug(labelHint);
        var shortToken = Guid.NewGuid().ToString("N")[..8];
        var safeScope = BuildSlug(scopeId);
        var branchName = $"ade/{slug}-{shortToken}";
        var worktreePath = ProfilePathSafety.GetContainedFullPath(_managedWorktreesRoot, safeScope, $"{slug}-{shortToken}");
        ProfilePathSafety.ThrowIfReparsePath(worktreePath);
        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);

        var targetBase = string.IsNullOrWhiteSpace(baseRef) ? "HEAD" : baseRef.Trim();
        await RunGitAsync(repoRoot, ["worktree", "add", "-b", branchName, worktreePath, targetBase], throwOnError: true, token).ConfigureAwait(false);
        return new IsolatedWorktreeCreated(repoRoot, worktreePath, branchName, targetBase);
    }

    public async Task RemoveWorktreeAsync(string repositoryRoot, string worktreePath, bool force, CancellationToken token)
    {
        var contained = ProfilePathSafety.GetContainedFullPath(_managedWorktreesRoot, Path.GetRelativePath(_managedWorktreesRoot, Path.GetFullPath(worktreePath)));
        ProfilePathSafety.ThrowIfReparsePath(contained);
        var args = force
            ? new[] { "worktree", "remove", "--force", contained }
            : new[] { "worktree", "remove", contained };
        if (Directory.Exists(repositoryRoot))
        {
            await RunGitAsync(repositoryRoot, args, throwOnError: false, token).ConfigureAwait(false);
            await RunGitAsync(repositoryRoot, ["worktree", "prune"], throwOnError: false, token).ConfigureAwait(false);
        }
        if (Directory.Exists(contained))
            Directory.Delete(contained, recursive: true);
    }

    public async Task<GitDiffSnapshot> GetDiffSnapshotAsync(string workingDirectory, CancellationToken token)
    {
        var status = await InspectRepositoryAsync(workingDirectory, token).ConfigureAwait(false);
        if (!status.IsGitRepository)
            return new GitDiffSnapshot(false, workingDirectory, null, 0, 0, 0, []);

        var hasHead = !string.IsNullOrWhiteSpace(status.HeadCommitShort);
        var diffArgs = hasHead
            ? new[] { "diff", "HEAD", "--unified=3", "--no-color", "--no-ext-diff" }
            : new[] { "diff", "--unified=3", "--no-color", "--no-ext-diff" };
        var diffResult = await RunGitAsync(status.WorkingDirectory, diffArgs, throwOnError: false, token).ConfigureAwait(false);
        var files = ParseUnifiedDiff(diffResult.Stdout).ToList();

        var porcelain = await RunGitAsync(status.WorkingDirectory, ["status", "--porcelain=v1", "-uall"], throwOnError: false, token).ConfigureAwait(false);
        if (porcelain.ExitCode == 0)
        {
            var seen = new HashSet<string>(files.Select(f => f.FilePath), StringComparer.OrdinalIgnoreCase);
            foreach (var rawLine in porcelain.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                if (rawLine.Length < 4 || !rawLine.StartsWith("?? ", StringComparison.Ordinal)) continue;
                var relPath = rawLine[3..].Trim().Trim('"').Replace('\\', '/');
                if (seen.Contains(relPath)) continue;
                var synthetic = TryCreateUntrackedFileDiff(status.WorkingDirectory, relPath);
                if (synthetic is not null)
                {
                    files.Add(synthetic);
                    seen.Add(relPath);
                }
            }
        }

        return new GitDiffSnapshot(
            true,
            status.WorkingDirectory,
            status.CurrentBranch,
            files.Count,
            files.Sum(file => file.Additions),
            files.Sum(file => file.Deletions),
            files);
    }

    public async Task<string> MergeBranchIntoRepositoryAsync(string repositoryRoot, string branchName, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        if (!Directory.Exists(repositoryRoot)) throw new InvalidOperationException("主仓库目录不存在。");

        var status = await RunGitAsync(repositoryRoot, ["status", "--porcelain=v1"], throwOnError: true, token).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(status.Stdout))
            throw new InvalidOperationException("主仓库当前有未提交的本地修改，请先提交或暂存（git stash）后再合入胜出分支。");

        var hasIdentity = (await RunGitAsync(repositoryRoot, ["config", "user.email"], throwOnError: false, token).ConfigureAwait(false)).ExitCode == 0;
        var mergeArgs = hasIdentity
            ? new[] { "merge", "--no-ff", branchName, "-m", $"Merge Hyperion branch '{branchName}'" }
            : new[] { "-c", "user.name=Hyperion", "-c", "user.email=hyperion@local", "merge", "--no-ff", branchName, "-m", $"Merge Hyperion branch '{branchName}'" };

        var merge = await RunGitAsync(
            repositoryRoot,
            mergeArgs,
            throwOnError: false,
            token).ConfigureAwait(false);

        if (merge.ExitCode != 0)
        {
            await RunGitAsync(repositoryRoot, ["merge", "--abort"], throwOnError: false, token).ConfigureAwait(false);
            throw new InvalidOperationException($"合并分支 {branchName} 时发生冲突，已自动中止合并并保留原分支：{merge.Stderr.Trim()}");
        }

        return string.IsNullOrWhiteSpace(merge.Stdout) ? $"已成功将 {branchName} 合并至主工作区。" : merge.Stdout.Trim();
    }

    public static IReadOnlyList<GitFileDiff> ParseUnifiedDiff(string? patch)
    {
        if (string.IsNullOrWhiteSpace(patch)) return [];
        var normalized = patch.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var results = new List<GitFileDiff>();

        string? oldPath = null;
        string? newPath = null;
        var fileStatus = "Modified";
        var hunks = new List<GitDiffHunk>();
        string? currentHeader = null;
        int oldLine = 0, newLine = 0, hunkOldStart = 0, hunkNewStart = 0;
        var hunkLines = new List<GitDiffLine>();
        int additions = 0, deletions = 0;

        void FlushHunk()
        {
            if (currentHeader is not null)
            {
                hunks.Add(new GitDiffHunk(currentHeader, hunkOldStart, hunkNewStart, hunkLines.ToArray()));
                currentHeader = null;
                hunkLines.Clear();
            }
        }

        void FlushFile()
        {
            FlushHunk();
            var primaryPath = newPath is not (null or "/dev/null") ? newPath : oldPath;
            if (!string.IsNullOrWhiteSpace(primaryPath))
            {
                results.Add(new GitFileDiff(
                    primaryPath!,
                    oldPath is "/dev/null" ? null : oldPath,
                    fileStatus,
                    additions,
                    deletions,
                    hunks.ToArray()));
            }
            oldPath = null;
            newPath = null;
            fileStatus = "Modified";
            hunks.Clear();
            additions = 0;
            deletions = 0;
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FlushFile();
                var parts = line.Split(' ', 4);
                if (parts.Length >= 4)
                {
                    oldPath = TrimDiffPrefix(parts[2], "a/");
                    newPath = TrimDiffPrefix(parts[3], "b/");
                }
                continue;
            }

            if (currentHeader is null)
            {
                if (line.StartsWith("new file mode ", StringComparison.Ordinal)) { fileStatus = "Added"; continue; }
                if (line.StartsWith("deleted file mode ", StringComparison.Ordinal)) { fileStatus = "Deleted"; continue; }
                if (line.StartsWith("rename from ", StringComparison.Ordinal)) { fileStatus = "Renamed"; oldPath = line["rename from ".Length..].Trim(); continue; }
                if (line.StartsWith("rename to ", StringComparison.Ordinal)) { fileStatus = "Renamed"; newPath = line["rename to ".Length..].Trim(); continue; }
                if (line.StartsWith("--- ", StringComparison.Ordinal))
                {
                    var raw = line[4..].Trim();
                    oldPath = raw == "/dev/null" ? "/dev/null" : TrimDiffPrefix(raw, "a/");
                    continue;
                }
                if (line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    var raw = line[4..].Trim();
                    newPath = raw == "/dev/null" ? "/dev/null" : TrimDiffPrefix(raw, "b/");
                    continue;
                }
            }

            if (line.StartsWith("@@ ", StringComparison.Ordinal))
            {
                FlushHunk();
                var match = HunkHeaderRegex.Match(line);
                if (match.Success)
                {
                    hunkOldStart = int.Parse(match.Groups["oldStart"].Value, System.Globalization.CultureInfo.InvariantCulture);
                    hunkNewStart = int.Parse(match.Groups["newStart"].Value, System.Globalization.CultureInfo.InvariantCulture);
                    oldLine = hunkOldStart;
                    newLine = hunkNewStart;
                    currentHeader = line;
                }
                continue;
            }

            if (currentHeader is null) continue;
            if (line.StartsWith(@"\ No newline at end of file", StringComparison.Ordinal)) continue;

            if (line.StartsWith('+'))
            {
                hunkLines.Add(new GitDiffLine("add", null, newLine++, line[1..]));
                additions++;
            }
            else if (line.StartsWith('-'))
            {
                hunkLines.Add(new GitDiffLine("del", oldLine++, null, line[1..]));
                deletions++;
            }
            else if (line.StartsWith(' '))
            {
                hunkLines.Add(new GitDiffLine("ctx", oldLine++, newLine++, line[1..]));
            }
        }

        FlushFile();
        return results;
    }

    public static string BuildInlineReviewPrompt(string targetMention, string? overallNote, IReadOnlyList<DiffLineComment> comments)
    {
        ArgumentNullException.ThrowIfNull(comments);
        if (comments.Count == 0 && string.IsNullOrWhiteSpace(overallNote))
            throw new InvalidOperationException("请至少添加一条行内代码批注或审查说明。");

        var builder = new StringBuilder();
        var prefix = string.IsNullOrWhiteSpace(targetMention) ? string.Empty : (targetMention.StartsWith('@') ? targetMention + " " : "@" + targetMention + " ");
        builder.Append(prefix);
        builder.AppendLine("请根据以下代码审查（Diff Review）逐行批注修改对应文件，并保持其余无关逻辑稳定：");
        if (!string.IsNullOrWhiteSpace(overallNote))
        {
            builder.AppendLine();
            builder.Append("总体审查意见：").AppendLine(overallNote.Trim());
        }

        for (var i = 0; i < comments.Count; i++)
        {
            var item = comments[i];
            builder.AppendLine();
            builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"{i + 1}. 文件 `{item.FilePath}` 第 {item.LineNumber} 行（{(item.Side == "old" ? "旧行" : "新行")}）：").AppendLine();
            if (!string.IsNullOrWhiteSpace(item.CodeSnippet))
                builder.Append("   代码片段：`").Append(item.CodeSnippet.Trim()).AppendLine("`");
            builder.Append("   修改要求：").AppendLine(item.Comment.Trim());
        }

        return builder.ToString().Trim();
    }

    private static GitFileDiff? TryCreateUntrackedFileDiff(string workingDirectory, string relativePath)
    {
        try
        {
            var fullPath = ProfilePathSafety.GetContainedFullPath(workingDirectory, relativePath.Split('/'));
            ProfilePathSafety.ThrowIfReparsePath(fullPath);
            if (!File.Exists(fullPath)) return null;
            var info = new FileInfo(fullPath);
            if (info.Length > 256 * 1024)
            {
                return new GitFileDiff(relativePath, null, "Untracked", 0, 0,
                    [new GitDiffHunk("@@ -0,0 +1,1 @@", 0, 1, [new GitDiffLine("ctx", null, 1, $"（新文件 {RuntimeFormatting.FormatBytes(info.Length)}，已省略预览）")])]);
            }

            var bytes = File.ReadAllBytes(fullPath);
            if (bytes.Take(Math.Min(bytes.Length, 4096)).Contains((byte)0))
            {
                return new GitFileDiff(relativePath, null, "Untracked", 0, 0,
                    [new GitDiffHunk("@@ -0,0 +1,1 @@", 0, 1, [new GitDiffLine("ctx", null, 1, "（二进制新文件）")])]);
            }

            var text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
            var rawLines = text.Split('\n');
            if (rawLines.Length > 0 && rawLines[^1].Length == 0)
                rawLines = rawLines[..^1];
            var diffLines = rawLines.Select((content, idx) => new GitDiffLine("add", null, idx + 1, content)).ToArray();
            return new GitFileDiff(
                relativePath,
                null,
                "Untracked",
                diffLines.Length,
                0,
                [new GitDiffHunk($"@@ -0,0 +1,{diffLines.Length} @@", 0, 1, diffLines)]);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<GitWorktreeEntry> ParseWorktreeList(string output)
    {
        var entries = new List<GitWorktreeEntry>();
        string? path = null, branch = null, head = null;
        var detached = false;

        void Commit()
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                entries.Add(new GitWorktreeEntry(path!, branch, head, entries.Count == 0, detached));
            }
            path = null; branch = null; head = null; detached = false;
        }

        foreach (var raw in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(raw)) { Commit(); continue; }
            if (raw.StartsWith("worktree ", StringComparison.Ordinal)) path = raw["worktree ".Length..].Trim();
            else if (raw.StartsWith("HEAD ", StringComparison.Ordinal)) head = raw["HEAD ".Length..].Trim()[..Math.Min(8, raw["HEAD ".Length..].Trim().Length)];
            else if (raw.StartsWith("branch refs/heads/", StringComparison.Ordinal)) branch = raw["branch refs/heads/".Length..].Trim();
            else if (raw.Equals("detached", StringComparison.Ordinal)) detached = true;
        }
        Commit();
        return entries;
    }

    private static string TrimDiffPrefix(string value, string prefix)
    {
        var trimmed = value.Trim().Trim('"');
        return trimmed.StartsWith(prefix, StringComparison.Ordinal) ? trimmed[prefix.Length..] : trimmed;
    }

    public static string BuildSlug(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "task";
        var ascii = new StringBuilder(input.Length);
        foreach (var ch in input.Trim().ToLowerInvariant())
        {
            if (ch is (>= 'a' and <= 'z') or (>= '0' and <= '9')) ascii.Append(ch);
            else if (ch is '-' or '_' or ' ' or '/') ascii.Append('-');
        }
        var cleaned = SafeSlugRegex.Replace(ascii.ToString(), "-").Trim('-');
        if (cleaned.Length == 0) cleaned = "task";
        return cleaned.Length > 28 ? cleaned[..28].Trim('-') : cleaned;
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        bool throwOnError,
        CancellationToken token = default)
    {
        var git = FindGitExecutable();
        var start = new ProcessStartInfo(git)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.quotepath=false");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("无法启动 git 进程。");
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            if (throwOnError && process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Git 操作失败：{(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim()}");
            }
            return (process.ExitCode, stdout, stderr);
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        }
    }
}
